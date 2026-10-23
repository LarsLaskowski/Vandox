// Package agentstate keeps the agent's small persistent state in the spool directory.
package agentstate

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"io"
	"io/fs"
	"log/slog"
	"math"
	"os"
	"path/filepath"
	"regexp"
	"slices"
	"strconv"
	"strings"
	"sync"
	"syscall"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/scheduler"
)

// FileName is the name of the state file in the spool directory.
const FileName = "agent-state.json"

const (
	// maxFileSize is the largest state file the agent reads: 256 journal positions of about 130 bytes and 16 file positions of about
	// 650 bytes with a renamed file fit with room.
	maxFileSize = 64 * 1024
	// fileVersion is the only version of the state file.
	fileVersion = 1
	// urgentInterval is the least time between two saves of an urgent change: the 10 s grid of the state job minus half of its
	// interval, so that the jitter of a run's start (it is taken after the tick) does not push a save to the tick after, and a
	// committed position is saved within one state interval.
	urgentInterval = 5 * time.Second
	// flushWait is how long Flush waits for a save that is running.
	flushWait = 5 * time.Second
	// heartbeatInterval is the time after which the state is saved anyway.
	heartbeatInterval = 50 * time.Second
	// maxUptimeSize is the most bytes read from /proc/uptime.
	maxUptimeSize = 64
	// maxUptimeSeconds bounds the uptime a Duration can hold.
	maxUptimeSeconds = 1e9
	// tempSuffix is appended to FileName for the file written before the rename.
	tempSuffix = ".tmp"

	msgUnavailable = "agent state not available"
	msgNotSaved    = "agent state not saved"
	reasonRunning  = "agentstate: save still running"

	// maxHeadLen is the most bytes the head hash of a file position covers.
	maxHeadLen = 256
)

var (
	// flushAfter returns the channel that tells Flush to give up waiting; tests replace it.
	flushAfter = time.After

	// errSaveRunning is the error of a Flush that gave up waiting for a save that did not end.
	errSaveRunning = errors.New(reasonRunning)

	validDigest = regexp.MustCompile(`^[0-9a-f]{64}$`)
)

var validUUID = regexp.MustCompile(`^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$`)

// State is the persisted agent state.
type State struct {
	// Boot is the last boot the agent saw.
	Boot *Boot
	// Journal is the position in the kernel journal.
	Journal *JournalPosition
	// Backup is the open backup run.
	Backup *OpenRun
	// Logs holds the positions of the log shipping collector, nil when it has none.
	Logs *LogPositions
}

// Bounds of the saved log positions.
const (
	// MaxLogJournals is the most journal positions saved.
	MaxLogJournals = 256
	// MaxLogFiles is the most file positions saved.
	MaxLogFiles = 16
)

// LogPositions are the positions of the log shipping collector.
type LogPositions struct {
	// Journal holds one position per journal sequence number ID.
	Journal []JournalPosition
	// Files holds one position per configured log file.
	Files []FilePosition
}

// FilePosition is the position in a configured log file.
type FilePosition struct {
	// PathSHA256 is the 64 lower-case hex digits of the SHA-256 of the configured path.
	PathSHA256 string
	// Device is the device of the file.
	Device uint64
	// Inode is the inode of the file.
	Inode uint64
	// Offset is the offset of the next byte to read.
	Offset int64
	// HeadSHA256 is the SHA-256 of the first HeadLen bytes.
	HeadSHA256 string
	// HeadLen is the number of bytes the head hash covers, 0 to 256.
	HeadLen int
	// LastTime is the time of the last line read.
	LastTime time.Time
	// Renamed is the rotated file still being read, or nil.
	Renamed *RenamedFile
}

// RenamedFile is a rotated file that is still being read.
type RenamedFile struct {
	// Device is the device of the file.
	Device uint64
	// Inode is the inode of the file.
	Inode uint64
	// Offset is the offset of the next byte to read.
	Offset int64
	// Size is the size seen at the last run.
	Size int64
}

// Boot identifies a boot of the monitored server.
type Boot struct {
	// ID is the boot id.
	ID string
	// Uptime is the last seen uptime.
	Uptime time.Duration
	// BootedAt is the boot time.
	BootedAt time.Time
}

// JournalPosition is the position of the last journal entry read.
type JournalPosition struct {
	// SeqnumID is the sequence number id of the journal file.
	SeqnumID string
	// Seqnum is the sequence number.
	Seqnum uint64
	// Realtime is the entry's real-time timestamp.
	Realtime time.Time
}

// OpenRun is a backup run that has not ended yet.
type OpenRun struct {
	// BootID is the boot the run started in.
	BootID string
	// StartedAt is when the run started.
	StartedAt time.Time
	// LastSeen is when a matching process was last seen.
	LastSeen time.Time
	// MaxProcesses is the highest number of matching processes.
	MaxProcesses uint32
}

// Status is the outcome of opening the state file.
type Status int

const (
	// StatusLoaded means the state file was read.
	StatusLoaded Status = iota
	// StatusFirstStart means there is no state file yet, without a warning.
	StatusFirstStart
	// StatusUnavailable means the state is missing or unusable.
	StatusUnavailable
)

// Store holds the state in memory and saves it to the state file.
type Store struct {
	dir string
	log *slog.Logger

	mu            sync.Mutex
	state         State
	seq           uint64
	dirty         bool
	urgentPending bool
	lastSave      time.Time
	saveFailed    bool
	barrier       func(ctx context.Context) error

	// saving is the write lock of the state file: a save holds it by sending and releases it by receiving. It covers encoding, writing
	// and the update of dirty and the sequence number.
	saving chan struct{}
}

// Open reads the state file in dir. It never creates the directory; a file that is not usable is treated as
// missing, and the first save replaces it.
func Open(dir string, log *slog.Logger) (*Store, Status) {
	s := &Store{dir: dir, log: log, saving: make(chan struct{}, 1)}
	st, status := load(dir)
	s.state = st
	if status == StatusUnavailable {
		log.Warn(msgUnavailable)
	}

	return s, status
}

// load reads and checks the state file in dir.
func load(dir string) (State, Status) {
	f, err := os.OpenFile(filepath.Join(dir, FileName), os.O_RDONLY|syscall.O_NOFOLLOW|syscall.O_NONBLOCK, 0)
	if err != nil {
		if errors.Is(err, fs.ErrNotExist) && isEmptyDir(dir) {
			return State{}, StatusFirstStart
		}

		return State{}, StatusUnavailable
	}
	defer func() { _ = f.Close() }()

	data, ok := readTrusted(f)
	if !ok {
		return State{}, StatusUnavailable
	}

	st, err := decode(data)
	if err != nil {
		return State{}, StatusUnavailable
	}

	return st, StatusLoaded
}

// isEmptyDir reports whether dir exists and holds no entry.
func isEmptyDir(dir string) bool {
	d, err := os.Open(dir)
	if err != nil {
		return false
	}
	defer func() { _ = d.Close() }()

	names, err := d.Readdirnames(1)

	return len(names) == 0 && errors.Is(err, io.EOF)
}

// readTrusted reads f when fstat shows a regular file of the agent's effective UID without group or other
// permission bits and of at most maxFileSize bytes.
func readTrusted(f *os.File) ([]byte, bool) {
	fi, err := f.Stat()
	if err != nil || !fi.Mode().IsRegular() || fi.Mode().Perm()&0o077 != 0 || fi.Size() > maxFileSize {
		return nil, false
	}

	sys, ok := fi.Sys().(*syscall.Stat_t)
	if !ok || int64(sys.Uid) != int64(os.Geteuid()) {
		return nil, false
	}

	data, err := io.ReadAll(io.LimitReader(f, maxFileSize+1))
	if err != nil || len(data) > maxFileSize {
		return nil, false
	}

	return data, true
}

// State returns a copy of the current state.
func (s *Store) State() State {
	s.mu.Lock()
	defer s.mu.Unlock()

	return copyState(s.state)
}

// Update changes the state with fn, which runs under the store's lock and must not call the store; urgent marks
// a kill, boot, backup or log position change (saved after 5 s), else the change waits for the 50 s heartbeat.
func (s *Store) Update(urgent bool, fn func(*State)) {
	s.mu.Lock()
	defer s.mu.Unlock()

	fn(&s.state)
	s.seq++
	s.dirty = true
	if urgent {
		s.urgentPending = true
	}
}

// SetBarrier registers b, which every save of the state file (the state job and Flush) calls first; when b fails the save is not
// written and is logged as `agent state not saved` with the fixed text `agentstate: save: spool not durable`. A nil b removes the
// barrier.
func (s *Store) SetBarrier(b func(ctx context.Context) error) {
	s.mu.Lock()
	defer s.mu.Unlock()

	s.barrier = b
}

// Flush saves the state now if it has changed since the last save, waiting at most 5 s for a save that is running. It gives up with
// a warning and an error when that save does not end, and it logs a failed save itself.
func (s *Store) Flush(now time.Time) error {
	select {
	case s.saving <- struct{}{}:
	case <-flushAfter(flushWait):
		s.log.Warn(msgNotSaved, "err", reasonRunning)

		return errSaveRunning
	}
	defer func() { <-s.saving }()

	s.mu.Lock()
	if !s.dirty {
		s.mu.Unlock()

		return nil
	}
	st, seq := copyState(s.state), s.seq
	s.mu.Unlock()

	err := s.saveLocked(st)
	s.finishSave(now, seq, err)
	if err != nil {
		return errors.New(errText(err))
	}

	return nil
}

// saveLocked encodes and writes st; the caller holds the write lock.
func (s *Store) saveLocked(st State) error {
	data, err := encode(st)
	if err == nil {
		err = write(s.dir, data)
	}

	return err
}

// finishSave records the end of a save of the state of sequence number seq at now; the caller holds the write lock.
func (s *Store) finishSave(now time.Time, seq uint64, err error) {
	s.mu.Lock()
	defer s.mu.Unlock()

	s.lastSave = now
	if err != nil {
		if !s.saveFailed {
			s.saveFailed = true
			s.log.Warn(msgNotSaved, "err", errText(err))
		}

		return
	}
	s.saveFailed = false
	if s.seq == seq {
		s.dirty = false
		s.urgentPending = false
	}
}

// copyState returns a deep copy of st.
func copyState(st State) State {
	var out State
	if st.Boot != nil {
		b := *st.Boot
		out.Boot = &b
	}
	if st.Journal != nil {
		j := *st.Journal
		out.Journal = &j
	}
	if st.Backup != nil {
		r := *st.Backup
		out.Backup = &r
	}
	if st.Logs != nil {
		out.Logs = copyLogs(st.Logs)
	}

	return out
}

// copyLogs returns a deep copy of l.
func copyLogs(l *LogPositions) *LogPositions {
	out := &LogPositions{Journal: slices.Clone(l.Journal), Files: slices.Clone(l.Files)}
	for i := range out.Files {
		if r := out.Files[i].Renamed; r != nil {
			c := *r
			out.Files[i].Renamed = &c
		}
	}

	return out
}

// fileState is the JSON form of the state file.
type fileState struct {
	Version *int         `json:"version"`
	Boot    *fileBoot    `json:"boot,omitempty"`
	Journal *fileJournal `json:"journal,omitempty"`
	Backup  *fileBackup  `json:"backup,omitempty"`
	Logs    *fileLogs    `json:"logs,omitempty"`
}

type fileLogs struct {
	Journal []fileJournal `json:"journal,omitempty"`
	Files   []fileLogFile `json:"files,omitempty"`
}

type fileLogFile struct {
	PathSHA256 string       `json:"path_sha256"`
	Device     uint64       `json:"device"`
	Inode      uint64       `json:"inode"`
	Offset     int64        `json:"offset"`
	HeadSHA256 string       `json:"head_sha256"`
	HeadLen    int          `json:"head_len"`
	LastTime   time.Time    `json:"last_time"`
	Renamed    *fileRenamed `json:"renamed,omitempty"`
}

type fileRenamed struct {
	Device uint64 `json:"device"`
	Inode  uint64 `json:"inode"`
	Offset int64  `json:"offset"`
	Size   int64  `json:"size"`
}

type fileBoot struct {
	ID       string    `json:"id"`
	UptimeNs int64     `json:"uptime_ns"`
	BootedAt time.Time `json:"booted_at"`
}

type fileJournal struct {
	SeqnumID string    `json:"seqnum_id"`
	Seqnum   uint64    `json:"seqnum"`
	Realtime time.Time `json:"realtime"`
}

type fileBackup struct {
	BootID       string    `json:"boot_id"`
	StartedAt    time.Time `json:"started_at"`
	LastSeen     time.Time `json:"last_seen"`
	MaxProcesses uint32    `json:"max_processes"`
}

// errInvalid is the error of a state file that is not valid.
var errInvalid = errors.New("invalid state file")

// decode parses data strictly: one JSON object, version 1, no unknown keys, valid UUIDs and times.
func decode(data []byte) (State, error) {
	dec := json.NewDecoder(bytes.NewReader(data))
	dec.DisallowUnknownFields()

	var f fileState
	if err := dec.Decode(&f); err != nil {
		return State{}, errInvalid
	}
	if _, err := dec.Token(); !errors.Is(err, io.EOF) {
		return State{}, errInvalid
	}
	if f.Version == nil || *f.Version != fileVersion || !timesInRange(f) {
		return State{}, errInvalid
	}

	var st State
	var ok bool
	if st.Boot, ok = decodeBoot(f.Boot); !ok {
		return State{}, errInvalid
	}
	if st.Journal, ok = decodeJournal(f.Journal); !ok {
		return State{}, errInvalid
	}
	if st.Backup, ok = decodeBackup(f.Backup); !ok {
		return State{}, errInvalid
	}
	if st.Logs, ok = decodeLogs(f.Logs); !ok {
		return State{}, errInvalid
	}

	return st, nil
}

// decodeBoot checks the boot of the state file and returns it; nil without one.
func decodeBoot(b *fileBoot) (*Boot, bool) {
	if b == nil {
		return nil, true
	}
	if !validUUID.MatchString(b.ID) || b.UptimeNs < 0 {
		return nil, false
	}

	return &Boot{ID: b.ID, Uptime: time.Duration(b.UptimeNs), BootedAt: b.BootedAt}, true
}

// decodeJournal checks the journal position of the state file and returns it; nil without one.
func decodeJournal(j *fileJournal) (*JournalPosition, bool) {
	if j == nil {
		return nil, true
	}
	if !validUUID.MatchString(j.SeqnumID) {
		return nil, false
	}

	return &JournalPosition{SeqnumID: j.SeqnumID, Seqnum: j.Seqnum, Realtime: j.Realtime}, true
}

// decodeBackup checks the open backup run of the state file and returns it; nil without one.
func decodeBackup(b *fileBackup) (*OpenRun, bool) {
	if b == nil {
		return nil, true
	}
	if !validUUID.MatchString(b.BootID) || !validRun(b) {
		return nil, false
	}

	return &OpenRun{BootID: b.BootID, StartedAt: b.StartedAt, LastSeen: b.LastSeen, MaxProcesses: b.MaxProcesses}, true
}

// decodeLogs checks the log positions of the state file and returns them, nil without any; false means they are not valid.
func decodeLogs(f *fileLogs) (*LogPositions, bool) {
	if f == nil {
		return nil, true
	}
	if len(f.Journal) > MaxLogJournals || len(f.Files) > MaxLogFiles {
		return nil, false
	}
	logs := &LogPositions{}
	for _, j := range f.Journal {
		if !validUUID.MatchString(j.SeqnumID) {
			return nil, false
		}
		logs.Journal = append(logs.Journal, JournalPosition(j))
	}
	for _, p := range f.Files {
		pos, ok := decodeFilePosition(p)
		if !ok {
			return nil, false
		}
		logs.Files = append(logs.Files, pos)
	}

	return logs, true
}

// decodeFilePosition checks one file position of the state file and returns it.
func decodeFilePosition(p fileLogFile) (FilePosition, bool) {
	if !validDigest.MatchString(p.PathSHA256) || !validDigest.MatchString(p.HeadSHA256) || p.HeadLen < 0 || p.HeadLen > maxHeadLen || p.Offset < 0 {
		return FilePosition{}, false
	}
	pos := FilePosition{PathSHA256: p.PathSHA256, Device: p.Device, Inode: p.Inode, Offset: p.Offset, HeadSHA256: p.HeadSHA256, HeadLen: p.HeadLen, LastTime: p.LastTime}
	if r := p.Renamed; r != nil {
		if r.Offset < 0 || r.Size < 0 {
			return FilePosition{}, false
		}
		pos.Renamed = &RenamedFile{Device: r.Device, Inode: r.Inode, Offset: r.Offset, Size: r.Size}
	}

	return pos, true
}

// maxYear is the first year a time in the state file may not reach.
const maxYear = 10000

// validRun reports whether the open run b is usable: set times in order and a process count.
func validRun(b *fileBackup) bool {
	return !b.StartedAt.IsZero() && !b.LastSeen.IsZero() && !b.LastSeen.Before(b.StartedAt) && b.MaxProcesses > 0
}

// timesInRange reports whether every time of f lies before the year 10000 UTC.
func timesInRange(f fileState) bool {
	var times []time.Time
	if f.Boot != nil {
		times = append(times, f.Boot.BootedAt)
	}
	if f.Journal != nil {
		times = append(times, f.Journal.Realtime)
	}
	if f.Backup != nil {
		times = append(times, f.Backup.StartedAt, f.Backup.LastSeen)
	}
	if f.Logs != nil {
		for _, j := range f.Logs.Journal {
			times = append(times, j.Realtime)
		}
		for _, p := range f.Logs.Files {
			times = append(times, p.LastTime)
		}
	}
	for _, t := range times {
		if t.UTC().Year() >= maxYear {
			return false
		}
	}

	return true
}

// encode returns the JSON form of st.
func encode(st State) ([]byte, error) {
	v := fileVersion
	f := fileState{Version: &v}
	if st.Boot != nil {
		f.Boot = &fileBoot{ID: st.Boot.ID, UptimeNs: int64(st.Boot.Uptime), BootedAt: st.Boot.BootedAt.UTC()}
	}
	if st.Journal != nil {
		f.Journal = &fileJournal{SeqnumID: st.Journal.SeqnumID, Seqnum: st.Journal.Seqnum, Realtime: st.Journal.Realtime.UTC()}
	}
	if st.Backup != nil {
		f.Backup = &fileBackup{BootID: st.Backup.BootID, StartedAt: st.Backup.StartedAt.UTC(), LastSeen: st.Backup.LastSeen.UTC(), MaxProcesses: st.Backup.MaxProcesses}
	}
	if st.Logs != nil {
		f.Logs = encodeLogs(st.Logs)
	}

	return json.Marshal(f)
}

// encodeLogs returns the JSON form of the log positions l.
func encodeLogs(l *LogPositions) *fileLogs {
	out := &fileLogs{}
	for _, j := range l.Journal {
		out.Journal = append(out.Journal, fileJournal{SeqnumID: j.SeqnumID, Seqnum: j.Seqnum, Realtime: j.Realtime.UTC()})
	}
	for _, p := range l.Files {
		f := fileLogFile{PathSHA256: p.PathSHA256, Device: p.Device, Inode: p.Inode, Offset: p.Offset, HeadSHA256: p.HeadSHA256, HeadLen: p.HeadLen, LastTime: p.LastTime.UTC()}
		if r := p.Renamed; r != nil {
			f.Renamed = &fileRenamed{Device: r.Device, Inode: r.Inode, Offset: r.Offset, Size: r.Size}
		}
		out.Files = append(out.Files, f)
	}

	return out
}

// write saves data to the state file: a fresh owner-only temporary file, fsync, rename, fsync of the directory.
func write(dir string, data []byte) error {
	final := filepath.Join(dir, FileName)
	tmp := final + tempSuffix

	if err := os.Remove(tmp); err != nil && !errors.Is(err, fs.ErrNotExist) {
		return err
	}

	f, err := os.OpenFile(tmp, os.O_WRONLY|os.O_CREATE|os.O_EXCL|syscall.O_NOFOLLOW, 0o600)
	if err != nil {
		return err
	}

	if err = writeSync(f, data); err != nil {
		_ = os.Remove(tmp)

		return err
	}
	if err = os.Rename(tmp, final); err != nil {
		_ = os.Remove(tmp)

		return err
	}

	d, err := os.Open(dir)
	if err != nil {
		return err
	}
	defer func() { _ = d.Close() }()

	return d.Sync()
}

// writeSync writes data to f, syncs and closes it.
func writeSync(f *os.File, data []byte) error {
	_, err := f.Write(data)
	if err == nil {
		err = f.Sync()
	}
	if cerr := f.Close(); err == nil {
		err = cerr
	}

	return err
}

// errText returns the text of the OS error in err without a path.
func errText(err error) string {
	var errno syscall.Errno
	if errors.As(err, &errno) {
		return "agentstate: save: " + errno.Error()
	}

	return "agentstate: save: failed"
}

// Saver is the job that saves the state.
type Saver struct {
	store *Store
	proc  fs.FS
}

// NewSaver returns the state job reading the uptime from proc.
func NewSaver(s *Store, proc fs.FS) *Saver {
	return &Saver{store: s, proc: proc}
}

// Name returns the collector name.
func (v *Saver) Name() string {
	return "state"
}

// Collect saves the state when an urgent change waits and 5 s have passed since the last save (the state job runs on a 10 s grid, so
// a change is saved at the next run), or when 50 s have passed (the uptime heartbeat, which also carries a waiting position). A save
// in progress by Flush is waited for.
func (v *Saver) Collect(ctx context.Context, run scheduler.Run) ([]model.Record, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}

	s := v.store
	select {
	case s.saving <- struct{}{}:
	case <-ctx.Done():
		return nil, ctx.Err()
	}
	defer func() { <-s.saving }()

	st, seq, ok := v.prepare(run.Start)
	if !ok {
		return nil, nil
	}

	s.finishSave(run.Start, seq, s.saveLocked(st))

	return nil, nil
}

// prepare decides whether a save is due at now and returns the state to save.
func (v *Saver) prepare(now time.Time) (State, uint64, bool) {
	s := v.store
	s.mu.Lock()
	defer s.mu.Unlock()

	elapsed := now.Sub(s.lastSave)
	if elapsed >= heartbeatInterval && s.state.Boot != nil {
		if up, ok := ReadUptime(v.proc); ok {
			s.state.Boot.Uptime = up
			s.seq++
			s.dirty = true
		}
	}

	due := s.dirty && (elapsed >= heartbeatInterval || (s.urgentPending && elapsed >= urgentInterval))
	if !due {
		return State{}, 0, false
	}

	if s.state.Boot != nil {
		if up, ok := ReadUptime(v.proc); ok {
			s.state.Boot.Uptime = up
		}
	}

	return copyState(s.state), s.seq, true
}

// ReadUptime reads the uptime from proc's uptime file.
func ReadUptime(proc fs.FS) (time.Duration, bool) {
	f, err := proc.Open("uptime")
	if err != nil {
		return 0, false
	}
	defer func() { _ = f.Close() }()

	raw, err := io.ReadAll(io.LimitReader(f, maxUptimeSize))
	if err != nil {
		return 0, false
	}

	fields := strings.Fields(string(raw))
	if len(fields) == 0 {
		return 0, false
	}

	secs, err := strconv.ParseFloat(fields[0], 64)
	if err != nil || math.IsNaN(secs) || secs < 0 || secs > maxUptimeSeconds {
		return 0, false
	}

	return time.Duration(math.Round(secs * float64(time.Second))), true
}
