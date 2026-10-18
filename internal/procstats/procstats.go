// Package procstats collects the process list of the monitored server from /proc: the top processes by memory and CPU,
// the sums per program and, under memory pressure, a full snapshot.
//
// The package reads files through fs.FS only: it starts no program, opens no socket and makes no raw system call. What it
// reads is chosen by local users (names and command lines), so every read is bounded before it is parsed, every number is
// parsed into its target width, and no text of a process reaches an error or a log line.
package procstats

import (
	"container/heap"
	"context"
	"errors"
	"fmt"
	"io"
	"io/fs"
	"log/slog"
	"math"
	"os"
	"slices"
	"strconv"
	"sync"
	"syscall"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/scheduler"
	"github.com/LarsLaskowski/Vandox/internal/wire"
)

// Names and bounds of the process collector.
const (
	Name           = "processes" // collector name
	Source         = "processes" // source of every record
	TopProcesses   = 25          // processes listed per ranking without memory pressure
	MaxProcesses   = 4096        // processes read per run
	ClockTicks     = 100         // USER_HZ
	UnnamedCommand = "(unnamed)" // command and program of a process with an empty command line and an empty comm
)

const (
	listChunk     = 256               // directory entries read at once
	pfKthread     = 0x00200000        // PF_KTHREAD in the flags of stat
	payloadBudget = 1<<20 - 8*(1<<10) // JSON bytes of the entries of one record
	halvingRounds = 13                // rounds the safety net halves each list in
)

// Sources are the file trees the collector reads; a nil field takes its default.
type Sources struct {
	Proc fs.FS // default os.DirFS("/proc")
	Etc  fs.FS // default os.DirFS("/etc")
}

// Rule is a configured rule that names the program of a process.
type Rule struct {
	Program        string
	Command        string
	CmdlinePrefix  string
	AppendNextWord bool
}

// Processes collects the process snapshot; it implements scheduler.Collector.
type Processes struct {
	src   Sources
	rules []Rule
	log   *slog.Logger

	mu    sync.Mutex
	prev  *previousRun // the last successful run
	state warnings     // what the log last said
}

// New returns the process collector reading src and naming programs by rules; a nil log logs nothing.
func New(src Sources, rules []Rule, log *slog.Logger) *Processes {
	if src.Proc == nil {
		src.Proc = os.DirFS("/proc")
	}
	if src.Etc == nil {
		src.Etc = os.DirFS("/etc")
	}
	if log == nil {
		log = slog.New(slog.DiscardHandler)
	}
	return &Processes{src: src, rules: rules, log: log}
}

// Name returns Name.
func (c *Processes) Name() string {
	return Name
}

// Collect takes one process snapshot. It fails only when /proc cannot be listed, the boot time cannot be read or no process
// could be read; a process that cannot be read is left out and makes the snapshot incomplete.
func (c *Processes) Collect(ctx context.Context, run scheduler.Run) ([]model.Record, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	boot, err := readBootTime(c.src.Proc)
	if err != nil {
		return nil, err
	}
	users, usersErr := readPasswd(c.src.Etc)
	pids, seen, err := listPIDs(c.src.Proc)
	if err != nil {
		return nil, err
	}
	res, err := c.readAll(ctx, pids, boot, users)
	if err != nil {
		return nil, err
	}
	if len(res.procs) == 0 {
		if res.firstErr == nil {
			return nil, errors.New("procstats: no process found")
		}
		return nil, fmt.Errorf("procstats: no process could be read: %w", res.firstErr)
	}

	c.mu.Lock()
	prev := c.prev
	c.mu.Unlock()
	samples, programs := res.samples(run.Start, prev)
	progs := aggregate(programs, samples)
	sortProcesses(samples, allHavePSS(samples))
	sortPrograms(progs, allProgramsHavePSS(progs))
	listed := samples
	if !run.Pressure {
		listed = selectTop(samples)
	}
	snap, refused, complete := fill(listed, progs, payloadBudget)
	res.unread += refused
	if refused > 0 && res.firstErr == nil {
		res.firstErr = errEntryRefused
	}
	snap.Complete = complete && len(listed) == len(samples) && res.unread == 0 && seen == len(pids)
	total := uint32(min(max(seen-res.ended, 0), math.MaxUint32))
	snap.Total = &total

	record, err := fitRecord(run.Start, &snap)
	if err != nil {
		return nil, err
	}
	c.remember(run.Start, res)
	c.logChanges(res, usersErr)
	return []model.Record{record}, nil
}

// listPIDs returns the lowest MaxProcesses canonical PIDs of proc in ascending order; seen counts all PID directories.
//
// The directory is read in chunks and a bounded max-heap keeps the lowest PIDs, so the listing never holds more than one
// chunk and MaxProcesses PIDs. A canonical PID is 1 to 10 digits without a leading zero within 32 bits and above 0.
func listPIDs(proc fs.FS) (pids []int32, seen int, err error) {
	dir, err := proc.Open(".")
	if err != nil {
		return nil, 0, fmt.Errorf("procstats: listing /proc: %w", err)
	}
	defer func() { _ = dir.Close() }()
	lister, ok := dir.(fs.ReadDirFile)
	if !ok {
		return nil, 0, errors.New("procstats: listing /proc: not a directory")
	}
	lowest := &pidHeap{}
	for {
		entries, err := lister.ReadDir(listChunk)
		for _, e := range entries {
			if pid, ok := pidOfEntry(e); ok {
				seen++
				keepLowest(lowest, pid)
			}
		}
		if errors.Is(err, io.EOF) {
			break
		}
		if err != nil {
			return nil, 0, fmt.Errorf("procstats: listing /proc: %w", err)
		}
	}
	pids = []int32(*lowest)
	slices.Sort(pids)
	return pids, seen, nil
}

// pidOfEntry returns the PID a directory entry is named after.
func pidOfEntry(e fs.DirEntry) (int32, bool) {
	name := e.Name()
	if !e.IsDir() || !isDigits(name) || len(name) > 10 || name[0] == '0' {
		return 0, false
	}
	pid, err := strconv.ParseInt(name, 10, 32)
	if err != nil || pid <= 0 {
		return 0, false
	}
	return int32(pid), true
}

// keepLowest adds pid to the heap of the lowest MaxProcesses PIDs.
func keepLowest(h *pidHeap, pid int32) {
	switch {
	case h.Len() < MaxProcesses:
		heap.Push(h, pid)
	case pid < (*h)[0]:
		(*h)[0] = pid
		heap.Fix(h, 0)
	}
}

// pidHeap is a max-heap of PIDs.
type pidHeap []int32

func (h pidHeap) Len() int           { return len(h) }
func (h pidHeap) Less(i, j int) bool { return h[i] > h[j] }
func (h pidHeap) Swap(i, j int)      { h[i], h[j] = h[j], h[i] }
func (h *pidHeap) Push(x any)        { *h = append(*h, x.(int32)) }
func (h *pidHeap) Pop() any {
	old := *h
	last := old[len(old)-1]
	*h = old[:len(old)-1]
	return last
}

// fitRecord returns the record of snap captured at start. The entries are budgeted so the line fits; as a safety net a
// record that is still too long is shortened by halving its processes and then its programs.
func fitRecord(start time.Time, snap *model.ProcessSnapshot) (model.Record, error) {
	record := model.Record{Meta: model.Meta{Source: Source, CapturedAt: start.UTC()}, Data: snap}
	for round := 0; ; round++ {
		checked := record
		checked.Origin, checked.Seq = model.OriginAgent, 1
		_, err := wire.CheckRecord(&checked)
		var tooLong *wire.RecordSizeError
		switch {
		case err == nil:
			return record, nil
		case !errors.As(err, &tooLong) || round >= 2*halvingRounds:
			return model.Record{}, fmt.Errorf("procstats: record refused: %w", err)
		case round < halvingRounds && len(snap.Processes) > 1:
			snap.Processes = snap.Processes[:len(snap.Processes)/2]
		default:
			snap.Programs = snap.Programs[:len(snap.Programs)/2]
		}
		snap.Complete = false
	}
}

// readResult is the outcome of reading all processes.
type readResult struct {
	procs    []readProc
	ended    int   // processes that ended during the read
	unread   int   // processes that could not be read
	noPSS    int   // processes read without PSS
	firstErr error // the first error that left a process out
}

// readProc is a process read, with what the CPU percentage needs.
type readProc struct {
	sample  model.ProcessSample
	program string
	start   uint64 // starttime
	ticks   uint64 // utime + stime
}

// samples returns the samples with their CPU percentage and the program of each; it is measured against prev.
func (r *readResult) samples(start time.Time, prev *previousRun) (samples []model.ProcessSample, programs []string) {
	samples = make([]model.ProcessSample, len(r.procs))
	programs = make([]string, len(r.procs))
	for i := range r.procs {
		rp := &r.procs[i]
		rp.sample.CPUPercent = cpuPercent(rp.sample.PID, rp.start, rp.ticks, rp.sample.StartedAt, start, prev)
		samples[i], programs[i] = rp.sample, rp.program
	}
	return samples, programs
}

// remember stores the ticks of the processes read as the predecessor of the next run.
func (c *Processes) remember(start time.Time, r *readResult) {
	procs := make(map[int32]tickCount, len(r.procs))
	for i := range r.procs {
		procs[r.procs[i].sample.PID] = tickCount{start: r.procs[i].start, ticks: r.procs[i].ticks}
	}
	c.mu.Lock()
	c.prev = &previousRun{start: start, procs: procs}
	c.mu.Unlock()
}

// readAll reads the processes pids. A process that ended is skipped, one that cannot be read is skipped and counted.
func (c *Processes) readAll(ctx context.Context, pids []int32, boot time.Time, users map[uint32]string) (*readResult, error) {
	res := &readResult{}
	r := newReader(c.src.Proc)
	for _, pid := range pids {
		if err := ctx.Err(); err != nil {
			return nil, err
		}
		rp, err := c.readProcess(r, pid, boot, users)
		switch {
		case err == nil:
			res.procs = append(res.procs, rp)
			if rp.sample.PSSBytes == nil {
				res.noPSS++
			}
		case hasEnded(err):
			res.ended++
		default:
			res.unread++
			if res.firstErr == nil {
				res.firstErr = err
			}
		}
	}
	return res, nil
}

// hasEnded reports whether err says that the process is gone.
func hasEnded(err error) bool {
	return errors.Is(err, fs.ErrNotExist) || errors.Is(err, syscall.ESRCH)
}

// readProcess reads stat, status, cmdline and smaps_rollup of process pid, in this order.
func (c *Processes) readProcess(r *reader, pid int32, boot time.Time, users map[uint32]string) (readProc, error) {
	dir := strconv.Itoa(int(pid)) + "/"
	st, err := r.stat(dir, pid)
	if err != nil {
		return readProc{}, err
	}
	status, err := r.status(dir)
	if err != nil {
		return readProc{}, err
	}
	noAddressSpace := st.flags&pfKthread != 0 || st.state == 'Z'
	if !status.hasRSS && !noAddressSpace {
		return readProc{}, fmt.Errorf("procstats: %sstatus: %w", dir, errStatusFormat)
	}
	cmdline, truncated, err := r.cmdline(dir)
	if err != nil {
		return readProc{}, err
	}
	rss, pss, err := r.memory(dir, status, noAddressSpace)
	if err != nil {
		return readProc{}, err
	}
	startedAt, err := startTime(boot, st.starttime)
	if err != nil {
		return readProc{}, fmt.Errorf("procstats: %sstat: %w", dir, err)
	}

	comm, _ := cleanText(st.comm, maxTextBytes)
	command := commandFor(cmdline, comm)
	p := model.ProcessSample{
		PID: st.pid, PPID: st.ppid, User: userName(users, status.euid), Command: command, Cmdline: cmdline, Truncated: truncated,
		StartedAt: startedAt, RSSBytes: rss, PSSBytes: pss,
	}
	if st.state >= 'a' && st.state <= 'z' || st.state >= 'A' && st.state <= 'Z' {
		p.State = string(st.state)
	}
	if status.hasSwap {
		swap := status.swap
		p.SwapBytes = &swap
	}
	return readProc{sample: p, program: programFor(c.rules, cmdline, command), start: st.starttime, ticks: satAdd(st.utime, st.stime)}, nil
}

// userName returns the name of the user uid, or the UID in decimal.
func userName(users map[uint32]string, uid uint32) string {
	if name, ok := users[uid]; ok {
		return name
	}
	return strconv.FormatUint(uint64(uid), 10)
}

// errStartTime is the reason a start time is refused.
var errStartTime = errors.New("start time out of range")

// startTime returns the time a process started: the boot time plus starttime clock ticks, which must be before the year 10000.
func startTime(boot time.Time, starttime uint64) (time.Time, error) {
	secs := starttime / ClockTicks
	rem := starttime % ClockTicks
	bootSecs := boot.Unix()
	if bootSecs < 0 || secs > maxUnixSeconds || secs > maxUnixSeconds-uint64(bootSecs) || rem >= ClockTicks {
		return time.Time{}, errStartTime
	}
	return time.Unix(bootSecs+int64(secs), int64(rem)*int64(time.Second/ClockTicks)).UTC(), nil
}

// reader reads the files of the processes with one buffer per kind of file, reused for every process of a run.
type reader struct {
	proc                         fs.FS
	statBuf, statusBuf, smapsBuf []byte
	cmdBuf                       []byte
}

func newReader(proc fs.FS) *reader {
	return &reader{
		proc: proc, statBuf: make([]byte, maxStatBytes+1), statusBuf: make([]byte, maxStatusBytes+1),
		smapsBuf: make([]byte, maxSmapsBytes+1), cmdBuf: make([]byte, maxCmdlineRead+1),
	}
}

// read reads the file name into buf and returns the bytes read; a file that fills buf is longer than the limit.
func (r *reader) read(name string, buf []byte) ([]byte, error) {
	f, err := r.proc.Open(name)
	if err != nil {
		return nil, fmt.Errorf("procstats: %s: %w", name, err)
	}
	defer func() { _ = f.Close() }()
	n, err := io.ReadFull(f, buf)
	if err != nil && !errors.Is(err, io.EOF) && !errors.Is(err, io.ErrUnexpectedEOF) {
		return nil, fmt.Errorf("procstats: %s: %w", name, err)
	}
	return buf[:n], nil
}

// text reads a file that must be shorter than len(buf).
func (r *reader) text(name string, buf []byte) (string, error) {
	data, err := r.read(name, buf)
	if err != nil {
		return "", err
	}
	if len(data) == len(buf) {
		return "", fmt.Errorf("procstats: %s: %w", name, errTooLarge)
	}
	return string(data), nil
}

func (r *reader) stat(dir string, pid int32) (statFields, error) {
	data, err := r.text(dir+"stat", r.statBuf)
	if err != nil {
		return statFields{}, err
	}
	st, err := parseStat(pid, data)
	if err != nil {
		return statFields{}, fmt.Errorf("procstats: %sstat: %w", dir, err)
	}
	return st, nil
}

func (r *reader) status(dir string) (statusFields, error) {
	data, err := r.text(dir+"status", r.statusBuf)
	if err != nil {
		return statusFields{}, err
	}
	sf, err := parseStatus(data)
	if err != nil {
		return statusFields{}, fmt.Errorf("procstats: %sstatus: %w", dir, err)
	}
	return sf, nil
}

// cmdline reads at most maxCmdlineRead bytes of the command line and returns it cleaned.
func (r *reader) cmdline(dir string) (text string, truncated bool, err error) {
	data, err := r.read(dir+"cmdline", r.cmdBuf)
	if err != nil {
		return "", false, err
	}
	more := len(data) > maxCmdlineRead
	if more {
		data = data[:maxCmdlineRead]
	}
	text, truncated = parseCmdline(data, more)
	return text, truncated, nil
}

// memory returns the resident and proportional set size. A kernel thread and a zombie have no address space: smaps_rollup
// answers ESRCH and both are 0. When smaps_rollup is refused (EACCES, EPERM), the RSS is the VmRSS of status and the PSS
// is unknown (nil).
func (r *reader) memory(dir string, status statusFields, noAddressSpace bool) (rss uint64, pss *uint64, err error) {
	name := dir + "smaps_rollup"
	data, err := r.text(name, r.smapsBuf)
	switch {
	case err == nil:
		rss, p, perr := parseSmapsRollup(data)
		if perr != nil {
			return 0, nil, fmt.Errorf("procstats: %s: %w", name, perr)
		}
		return rss, &p, nil
	case noAddressSpace && errors.Is(err, syscall.ESRCH):
		zero := uint64(0)
		return 0, &zero, nil
	case errors.Is(err, syscall.EACCES) || errors.Is(err, syscall.EPERM):
		return status.rss, nil, nil
	}
	return 0, nil, err
}

// warnings is which warnings the log has said and not yet withdrawn.
type warnings struct {
	partly, unread, noUsers bool
}

// logChanges logs the changes of the warnings since the last successful run, each once.
func (c *Processes) logChanges(r *readResult, usersErr error) {
	c.mu.Lock()
	defer c.mu.Unlock()

	partly, unread, noUsers := r.noPSS > 0, r.unread > 0, usersErr != nil
	switch {
	case partly && !c.state.partly:
		c.log.Warn("process memory partly unknown", "collector", Name, "processes", r.noPSS)
	case !partly && c.state.partly:
		c.log.Info("process memory known again", "collector", Name)
	}
	switch {
	case unread && !c.state.unread:
		c.log.Warn("processes not read", "collector", Name, "processes", r.unread, "err", r.firstErr)
	case !unread && c.state.unread:
		c.log.Info("processes read again", "collector", Name)
	}
	switch {
	case noUsers && !c.state.noUsers:
		c.log.Warn("user names unavailable", "collector", Name, "err", usersErr)
	case !noUsers && c.state.noUsers:
		c.log.Info("user names available", "collector", Name)
	}
	c.state = warnings{partly: partly, unread: unread, noUsers: noUsers}
}
