// Package importer imports log files, directories and archives into the store.
package importer

import (
	"context"
	"errors"
	"time"

	"github.com/LarsLaskowski/Vandox/cmd/vandoxd/internal/store"
	"github.com/LarsLaskowski/Vandox/internal/logparse"
)

const (
	// MaxFiles is the number of entries one run handles: directory entries of any kind and tar headers except
	// PAX global headers; enforced while scanning.
	MaxFiles = 20000
	// MaxPathBytes is the longest relative path or entry name.
	MaxPathBytes = 1024
	// DefaultBatchRecords is the number of records per batch.
	DefaultBatchRecords = 2000
	// DefaultBatchBytes is the number of input bytes per batch.
	DefaultBatchBytes = 4 << 20
	// MaxProblems is the number of problems kept per file.
	MaxProblems = 10
	// ProgressLines is the number of lines between EventFileProgress.
	ProgressLines = 100000
	// DefaultProgressBytes is the number of decompressed bytes between EventScanProgress while a file is hashed.
	DefaultProgressBytes = 64 << 20
)

// ErrTooManyFiles is wrapped by the error Run returns when the input holds more than MaxFiles entries.
var ErrTooManyFiles = errors.New("importer: too many files")

// Store is the part of the database the importer writes to.
type Store interface {
	store.Writer
	store.ImportTracker
}

// Options configure Run.
type Options struct {
	Parsers       *logparse.Registry // required
	Store         Store              // required
	Now           func() time.Time   // required; times are converted to UTC
	Progress      func(Progress)     // optional; called synchronously
	BatchRecords  int                // 0: DefaultBatchRecords; at most store.MaxBatchRecords
	BatchBytes    int64              // 0: DefaultBatchBytes
	ProgressBytes int64              // 0: DefaultProgressBytes
}

// Outcome is what happened to a file.
type Outcome string

const (
	// OutcomeImported means the file's records were stored in this run.
	OutcomeImported Outcome = "imported"
	// OutcomeAlreadyImported means the file's content was imported completely before.
	OutcomeAlreadyImported Outcome = "already_imported"
	// OutcomeUnrecognized means the file was listed but not imported.
	OutcomeUnrecognized Outcome = "unrecognized"
	// OutcomeFailed means the file could not be imported.
	OutcomeFailed Outcome = "failed"
)

// Problem is input a parser skipped or a record the store would refuse.
type Problem struct {
	Line   int64 // 1-based, 0 when unknown
	Reason string
}

// FileResult is the result of one file.
type FileResult struct {
	Path         string // display path: relative to the root; archive entries as "<archive path>:<entry name>"
	SourceType   string // "" when not recognized
	Outcome      Outcome
	Reason       string // why unrecognized or failed
	Lines        int64
	Records      int64 // stored in this run
	Skipped      int64
	ResumedAfter int64 // records an earlier, interrupted run had stored
	First, Last  time.Time
	Problems     []Problem // at most MaxProblems
}

// Summary is the result of a run.
type Summary struct {
	Root                    string
	Started, Finished       time.Time
	Files                   []FileResult // every file found, in input order
	Lines, Records, Skipped int64
	First, Last             time.Time // capture-time range of the records stored; zero when none
	Interrupted             bool
}

// Count returns the number of files with outcome o.
func (s *Summary) Count(o Outcome) int {
	return 0
}

// Event names a progress event.
type Event string

const (
	// EventScanProgress is reported in pass 1 with Path and Bytes of a file being hashed.
	EventScanProgress Event = "scan_progress"
	// EventScanned is reported when pass 1 is done.
	EventScanned Event = "scanned"
	// EventFileStarted is reported when pass 2 starts a file.
	EventFileStarted Event = "file_started"
	// EventFileProgress is reported every ProgressLines lines of a file.
	EventFileProgress Event = "file_progress"
	// EventFileFinished is reported when a file is finished or listed.
	EventFileFinished Event = "file_finished"
)

// Progress reports the state of a run.
type Progress struct {
	Event          Event
	Files, Pending int // EventScanned: files found, files to import
	Path           string
	SourceType     string
	Lines, Records int64
	Bytes          int64       // EventScanProgress: decompressed bytes of the file read so far
	Result         *FileResult // EventFileFinished
}

// Run imports root and returns the summary; the error reports what stopped the run (the summary covers what
// was done until then).
func Run(ctx context.Context, root string, opts Options) (Summary, error) {
	return Summary{}, errors.New("not implemented")
}
