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
	// maxFileSize is the largest state file the agent reads.
	maxFileSize = 16 * 1024
	// fileVersion is the only version of the state file.
	fileVersion = 1
	// urgentInterval is the least time between two saves of an urgent change.
	urgentInterval = 10 * time.Second
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
}

// Open reads the state file in dir. It never creates the directory; a file that is not usable is treated as
// missing, and the first save replaces it.
func Open(dir string, log *slog.Logger) (*Store, Status) {
	s := &Store{dir: dir, log: log}
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
// a kill, boot or backup change (saved after 10 s), else the change waits for the 50 s heartbeat.
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

	return out
}

// fileState is the JSON form of the state file.
type fileState struct {
	Version *int         `json:"version"`
	Boot    *fileBoot    `json:"boot,omitempty"`
	Journal *fileJournal `json:"journal,omitempty"`
	Backup  *fileBackup  `json:"backup,omitempty"`
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
	if f.Boot != nil {
		if !validUUID.MatchString(f.Boot.ID) || f.Boot.UptimeNs < 0 {
			return State{}, errInvalid
		}
		st.Boot = &Boot{ID: f.Boot.ID, Uptime: time.Duration(f.Boot.UptimeNs), BootedAt: f.Boot.BootedAt}
	}
	if f.Journal != nil {
		if !validUUID.MatchString(f.Journal.SeqnumID) {
			return State{}, errInvalid
		}
		st.Journal = &JournalPosition{SeqnumID: f.Journal.SeqnumID, Seqnum: f.Journal.Seqnum, Realtime: f.Journal.Realtime}
	}
	if f.Backup != nil {
		if !validUUID.MatchString(f.Backup.BootID) || !validRun(f.Backup) {
			return State{}, errInvalid
		}
		st.Backup = &OpenRun{BootID: f.Backup.BootID, StartedAt: f.Backup.StartedAt, LastSeen: f.Backup.LastSeen, MaxProcesses: f.Backup.MaxProcesses}
	}

	return st, nil
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

	return json.Marshal(f)
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

// Collect saves the state when an urgent change waits and 10 s have passed since the last save, or when 50 s
// have passed (the uptime heartbeat, which also carries a waiting position).
func (v *Saver) Collect(ctx context.Context, run scheduler.Run) ([]model.Record, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}

	s := v.store
	st, seq, ok := v.prepare(run.Start)
	if !ok {
		return nil, nil
	}

	data, err := encode(st)
	if err == nil {
		err = write(s.dir, data)
	}

	s.mu.Lock()
	defer s.mu.Unlock()
	s.lastSave = run.Start
	if err != nil {
		if !s.saveFailed {
			s.saveFailed = true
			s.log.Warn(msgNotSaved, "err", errText(err))
		}

		return nil, nil
	}
	s.saveFailed = false
	if s.seq == seq {
		s.dirty = false
		s.urgentPending = false
	}

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
