// Package agentstate keeps the agent's small persistent state in the spool directory.
package agentstate

import (
	"context"
	"errors"
	"io/fs"
	"log/slog"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/scheduler"
)

// FileName is the name of the state file in the spool directory.
const FileName = "agent-state.json"

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
}

// Open reads the state file in dir.
func Open(dir string, log *slog.Logger) (*Store, Status) {
	return &Store{dir: dir, log: log}, StatusUnavailable
}

// State returns a copy of the current state.
func (s *Store) State() State {
	return State{}
}

// Update changes the state with fn; urgent marks a kill, boot or backup change (saved after 10 s), else the
// change waits for the 50 s heartbeat.
func (s *Store) Update(urgent bool, fn func(*State)) {
	panic("not implemented")
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
	return ""
}

// Collect saves the state when a change waits.
func (v *Saver) Collect(ctx context.Context, run scheduler.Run) ([]model.Record, error) {
	return nil, errors.New("not implemented")
}
