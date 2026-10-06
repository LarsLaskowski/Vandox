package store

import (
	"context"
	"database/sql"
	"errors"
	"fmt"
	"strings"
	"time"
	"unicode/utf8"

	"github.com/LarsLaskowski/Vandox/internal/logparse"
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
	if err := validateImportStart(f); err != nil {
		return ImportFile{}, err
	}
	var modTime any
	if !f.ModTime.IsZero() && inStorableRange(f.ModTime) {
		modTime = f.ModTime.UnixNano()
	}
	tx, err := s.db.BeginTx(ctx, nil)
	if err != nil {
		return ImportFile{}, fmt.Errorf("store: beginning import: %w", err)
	}
	defer func() { _ = tx.Rollback() }()
	_, err = tx.ExecContext(ctx, insertImportFile, f.SHA256[:], f.Size, importName(f.Name), []byte(f.FileName),
		modTime, f.SourceType, f.StartedAt.UnixNano())
	if err != nil {
		return ImportFile{}, fmt.Errorf("store: beginning import: %w", err)
	}
	file, err := selectImportFile(ctx, tx, f.SHA256)
	if err != nil {
		return ImportFile{}, fmt.Errorf("store: beginning import: %w", err)
	}
	if err := tx.Commit(); err != nil {
		return ImportFile{}, fmt.Errorf("store: beginning import: %w", err)
	}
	return file, nil
}

const (
	insertImportFile = "INSERT INTO import_files(sha256, size, name, file_name, mod_time, source_type, started_at) " +
		"VALUES (?, ?, ?, ?, ?, ?, ?) ON CONFLICT(sha256) DO NOTHING"
	selectImportFileBySum = "SELECT id, size, name, file_name, mod_time, source_type, records, complete, started_at, completed_at " +
		"FROM import_files WHERE sha256 = ?"
)

// validateImportStart refuses an ImportFileStart BeginImport must not store.
func validateImportStart(f ImportFileStart) error {
	switch {
	case f.StartedAt.IsZero():
		return invalidImport("started_at is required")
	case !isUTC(f.StartedAt):
		return invalidImport("started_at must be UTC")
	case !inStorableRange(f.StartedAt):
		return invalidImport("started_at is outside the storable range")
	case f.Size < 0:
		return invalidImport("size must not be negative")
	case len(f.FileName) > MaxImportNameBytes:
		return invalidImport("file name is longer than %d bytes", MaxImportNameBytes)
	}
	if err := logparse.CheckType(f.SourceType); err != nil {
		return fmt.Errorf("%w: %w", ErrInvalidImport, err)
	}
	return nil
}

// invalidImport returns an error wrapping ErrInvalidImport that names the rule broken.
func invalidImport(format string, args ...any) error {
	return fmt.Errorf("%w: "+format, append([]any{ErrInvalidImport}, args...)...)
}

// isUTC reports whether t has a zero offset from UTC.
func isUTC(t time.Time) bool {
	_, offset := t.Zone()
	return offset == 0
}

// importName returns name with invalid UTF-8 replaced by U+FFFD, cut to MaxImportNameBytes bytes at a rune
// boundary.
func importName(name string) string {
	name = strings.ToValidUTF8(name, "\uFFFD")
	if len(name) <= MaxImportNameBytes {
		return name
	}
	n := MaxImportNameBytes
	for n > 0 && !utf8.RuneStart(name[n]) {
		n--
	}
	return name[:n]
}

// selectImportFile reads the import state of the content sum.
func selectImportFile(ctx context.Context, tx *sql.Tx, sum [32]byte) (ImportFile, error) {
	f := ImportFile{SHA256: sum}
	var fileName []byte
	var modTime, completedAt sql.NullInt64
	var startedAt int64
	var complete int64
	err := tx.QueryRowContext(ctx, selectImportFileBySum, sum[:]).Scan(&f.ID, &f.Size, &f.Name, &fileName, &modTime,
		&f.SourceType, &f.Records, &complete, &startedAt, &completedAt)
	if err != nil {
		return ImportFile{}, err
	}
	f.FileName = string(fileName)
	f.Complete = complete == 1
	f.StartedAt = time.Unix(0, startedAt).UTC()
	if modTime.Valid {
		f.ModTime = time.Unix(0, modTime.Int64).UTC()
	}
	if completedAt.Valid {
		f.CompletedAt = time.Unix(0, completedAt.Int64).UTC()
	}
	return f, nil
}

// CheckImportRecord returns nil when WriteBatch accepts r in a batch with Import: the model rules, origin
// import, and the storable time range.
func CheckImportRecord(r *model.Record) error {
	if err := validateRecord(r, false); err != nil {
		return err
	}
	if r.Origin != model.OriginImport {
		return errors.New("origin must be import")
	}
	return nil
}

// advanceImport applies step to the import state in tx for a batch of n records received at receivedAt. It
// returns an error wrapping ErrImportConflict unless the stored count equals step.Done and the file is not
// complete.
func advanceImport(ctx context.Context, tx *sql.Tx, step ImportStep, n int, receivedAt time.Time) error {
	var completedAt any
	if step.Complete {
		completedAt = receivedAt.UnixNano()
	}
	res, err := tx.ExecContext(ctx, "UPDATE import_files SET records = records + ?, complete = ?, completed_at = ? "+
		"WHERE id = ? AND records = ? AND complete = 0", n, step.Complete, completedAt, step.FileID, step.Done)
	if err != nil {
		return err
	}
	changed, err := res.RowsAffected()
	if err != nil {
		return err
	}
	if changed != 1 {
		return fmt.Errorf("%w: file %d does not have %d records stored and is not complete", ErrImportConflict, step.FileID, step.Done)
	}
	return nil
}
