package store

import (
	"context"
	"errors"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

// MaxImportNameBytes is the longest file name BeginImport stores; longer names are cut.
const MaxImportNameBytes = 1024

// ErrInvalidImport is wrapped by the error BeginImport returns for a refused ImportFileStart.
var ErrInvalidImport = errors.New("store: invalid import")

// ErrImportConflict is wrapped by the error WriteBatch returns when Batch.Import does not match the stored
// import state (another run advanced it, or the file is complete or unknown); nothing is written.
var ErrImportConflict = errors.New("store: import state changed")

// ImportFileStart describes a file content the importer is about to import.
type ImportFileStart struct {
	SHA256     [32]byte  // of the decompressed content
	Size       int64     // bytes of the decompressed content, >= 0
	Name       string    // display path of the first import
	FileName   string    // logparse.File.Name the parser gets; stored byte for byte, at most MaxImportNameBytes
	ModTime    time.Time // logparse.File.ModTime; converted to UTC; zero or outside the storable range: unknown
	SourceType string    // parser type, logparse.CheckType
	StartedAt  time.Time // UTC
}

// ImportFile is the stored import state of a file content.
type ImportFile struct {
	ID          int64
	SHA256      [32]byte
	Size        int64
	Name        string
	FileName    string    // logparse.File.Name of the first import, exact
	ModTime     time.Time // logparse.File.ModTime of the first import, UTC; zero when unknown
	SourceType  string
	Records     int64 // records stored for the file so far
	Complete    bool
	StartedAt   time.Time
	CompletedAt time.Time // zero while not complete
}

// ImportStep ties a batch to the import of one file.
type ImportStep struct {
	FileID   int64
	Done     int64 // records of the file stored before this batch; must equal the stored count
	Complete bool  // the file is completely imported after this batch
}

// BeginImport returns the import state of the content f.SHA256, creating it (no records, not complete)
// when it is unknown. An existing state is returned unchanged.
func (s *Store) BeginImport(ctx context.Context, f ImportFileStart) (ImportFile, error) {
	return ImportFile{}, errors.New("not implemented")
}

// CheckImportRecord returns nil when WriteBatch accepts r in a batch with Import: the model rules, origin
// import, and the storable time range.
func CheckImportRecord(r *model.Record) error {
	return errors.New("not implemented")
}
