package store

import (
	"context"
	"database/sql"
	"net/url"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

// openStore opens a store in dir and closes it when the test ends.
func openStore(t *testing.T, dir string) *Store {
	t.Helper()
	s, err := Open(context.Background(), dir)
	if err != nil {
		t.Fatalf("Open(%q) error = %v, want nil", dir, err)
	}
	t.Cleanup(func() { _ = s.Close() })
	return s
}

// queryString runs a query that returns one text column through db.
func queryString(t *testing.T, db *sql.DB, query string, args ...any) string {
	t.Helper()
	var got string
	if err := db.QueryRowContext(context.Background(), query, args...).Scan(&got); err != nil {
		t.Fatalf("query %q error = %v, want nil", query, err)
	}
	return got
}

// rawQueryString reads one text value from the database file at path through its own connection.
func rawQueryString(t *testing.T, path, query string) string {
	t.Helper()
	dsn := (&url.URL{Scheme: "file", Path: path, RawQuery: "mode=ro"}).String()
	db, err := sql.Open("sqlite", dsn)
	if err != nil {
		t.Fatalf("sql.Open(%q) error = %v, want nil", dsn, err)
	}
	defer func() { _ = db.Close() }()
	return queryString(t, db, query)
}

func TestOpen_CreatesDatabase(t *testing.T) {
	dir := t.TempDir()

	s := openStore(t, dir)

	path := filepath.Join(dir, FileName)
	info, err := os.Lstat(path)
	if err != nil {
		t.Fatalf("Lstat(%q) error = %v, want the database file to exist", path, err)
	}
	if got := info.Mode().Perm(); got != 0o600 {
		t.Errorf("mode of %s = %o, want 600", FileName, got)
	}
	if !s.Created() {
		t.Error("Created() = false after creating the file, want true")
	}
	if got := queryString(t, s.db, "PRAGMA journal_mode"); got != "wal" {
		t.Errorf("PRAGMA journal_mode = %q, want %q", got, "wal")
	}
	if got := queryString(t, s.db, "SELECT value FROM meta WHERE key = 'schema_version'"); got != strconv.Itoa(SchemaVersion) {
		t.Errorf("meta schema_version = %q, want %q", got, strconv.Itoa(SchemaVersion))
	}
}

func TestOpen_DataSurvivesRestart(t *testing.T) {
	dir := t.TempDir()
	first := openStore(t, dir)
	if _, err := first.db.ExecContext(context.Background(), "INSERT INTO meta(key, value) VALUES ('probe', 'kept')"); err != nil {
		t.Fatalf("insert error = %v, want nil", err)
	}
	if err := first.Close(); err != nil {
		t.Fatalf("Close() error = %v, want nil", err)
	}

	second := openStore(t, dir)

	if got := queryString(t, second.db, "SELECT value FROM meta WHERE key = 'probe'"); got != "kept" {
		t.Errorf("value after restart = %q, want %q", got, "kept")
	}
	if second.Created() {
		t.Error("Created() = true on reopening an existing database, want false")
	}
}

func TestStore_Ping(t *testing.T) {
	t.Run("open store", func(t *testing.T) {
		s := openStore(t, t.TempDir())

		if err := s.Ping(context.Background()); err != nil {
			t.Errorf("Ping() error = %v, want nil", err)
		}
	})

	t.Run("closed store", func(t *testing.T) {
		s := openStore(t, t.TempDir())
		if err := s.Close(); err != nil {
			t.Fatalf("Close() error = %v, want nil", err)
		}

		if err := s.Ping(context.Background()); err == nil {
			t.Error("Ping() after Close error = nil, want an error")
		}
	})

	t.Run("cancelled context", func(t *testing.T) {
		s := openStore(t, t.TempDir())
		ctx, cancel := context.WithCancel(context.Background())
		cancel()

		if err := s.Ping(ctx); err == nil {
			t.Error("Ping(cancelled ctx) error = nil, want an error")
		}
	})
}

// dirContents returns the sorted names in dir.
func dirContents(t *testing.T, dir string) []string {
	t.Helper()
	entries, err := os.ReadDir(dir)
	if err != nil {
		t.Fatalf("ReadDir(%q) error = %v, want nil", dir, err)
	}
	names := make([]string, 0, len(entries))
	for _, e := range entries {
		names = append(names, e.Name())
	}
	return names
}

func TestOpen_RefusesUnusableDirectory(t *testing.T) {
	t.Run("directory does not exist", func(t *testing.T) {
		parent := t.TempDir()
		dir := filepath.Join(parent, "missing")

		s, err := Open(context.Background(), dir)

		if err == nil {
			_ = s.Close()
			t.Fatal("Open(missing dir) error = nil, want an error")
		}
		if _, statErr := os.Lstat(dir); !os.IsNotExist(statErr) {
			t.Errorf("Lstat(%q) error = %v, want the directory not to be created", dir, statErr)
		}
	})

	t.Run("directory is a regular file", func(t *testing.T) {
		parent := t.TempDir()
		dir := filepath.Join(parent, "file")
		if err := os.WriteFile(dir, []byte("x"), 0o600); err != nil {
			t.Fatal(err)
		}

		s, err := Open(context.Background(), dir)

		if err == nil {
			_ = s.Close()
			t.Fatal("Open(regular file) error = nil, want an error")
		}
		if got := dirContents(t, parent); len(got) != 1 {
			t.Errorf("entries after Open = %v, want only the original file", got)
		}
	})
}

// refusalCase prepares a storage directory that Open must refuse.
type refusalCase struct {
	name  string
	setup func(t *testing.T, dir, outside string)
}

func symlinkCases() []refusalCase {
	return []refusalCase{
		{"database is a directory", func(t *testing.T, dir, _ string) {
			mustMkdir(t, filepath.Join(dir, FileName))
		}},
		{"database is a symlink to a file", func(t *testing.T, dir, outside string) {
			target := filepath.Join(outside, "target.db")
			mustWrite(t, target, "target content")
			mustSymlink(t, target, filepath.Join(dir, FileName))
		}},
		{"database is a symlink to a directory", func(t *testing.T, dir, outside string) {
			target := filepath.Join(outside, "targetdir")
			mustMkdir(t, target)
			mustSymlink(t, target, filepath.Join(dir, FileName))
		}},
		{"database is a dangling symlink", func(t *testing.T, dir, outside string) {
			mustSymlink(t, filepath.Join(outside, "absent.db"), filepath.Join(dir, FileName))
		}},
		{"wal file is a symlink", func(t *testing.T, dir, outside string) {
			createDatabase(t, dir)
			target := filepath.Join(outside, "target-wal")
			mustWrite(t, target, "target content")
			mustSymlink(t, target, filepath.Join(dir, FileName+"-wal"))
		}},
		{"shm file is a symlink", func(t *testing.T, dir, outside string) {
			createDatabase(t, dir)
			target := filepath.Join(outside, "target-shm")
			mustWrite(t, target, "target content")
			mustSymlink(t, target, filepath.Join(dir, FileName+"-shm"))
		}},
		{"wal file is a dangling symlink", func(t *testing.T, dir, outside string) {
			createDatabase(t, dir)
			mustSymlink(t, filepath.Join(outside, "absent-wal"), filepath.Join(dir, FileName+"-wal"))
		}},
	}
}

func mustMkdir(t *testing.T, path string) {
	t.Helper()
	if err := os.Mkdir(path, 0o700); err != nil {
		t.Fatal(err)
	}
}

func mustWrite(t *testing.T, path, content string) {
	t.Helper()
	if err := os.WriteFile(path, []byte(content), 0o640); err != nil {
		t.Fatal(err)
	}
}

func mustSymlink(t *testing.T, target, link string) {
	t.Helper()
	if err := os.Symlink(target, link); err != nil {
		t.Fatal(err)
	}
}

// createDatabase creates a regular database in dir and closes it again.
func createDatabase(t *testing.T, dir string) {
	t.Helper()
	s, err := Open(context.Background(), dir)
	if err != nil {
		t.Fatalf("setup Open(%q) error = %v, want nil", dir, err)
	}
	if err := s.Close(); err != nil {
		t.Fatalf("setup Close() error = %v, want nil", err)
	}
}

func TestOpen_RefusesLinksAndDirectories(t *testing.T) {
	for _, tc := range symlinkCases() {
		t.Run(tc.name, func(t *testing.T) {
			dir := t.TempDir()
			outside := t.TempDir()
			tc.setup(t, dir, outside)
			outsideBefore := dirContents(t, outside)
			dirBefore := dirContents(t, dir)

			s, err := Open(context.Background(), dir)

			if err == nil {
				_ = s.Close()
				t.Fatal("Open() error = nil, want an error")
			}
			if got := dirContents(t, outside); strings.Join(got, ",") != strings.Join(outsideBefore, ",") {
				t.Errorf("entries outside the storage directory = %v, want unchanged %v", got, outsideBefore)
			}
			if got := dirContents(t, dir); strings.Join(got, ",") != strings.Join(dirBefore, ",") {
				t.Errorf("entries in the storage directory = %v, want unchanged %v", got, dirBefore)
			}
		})
	}
}

func TestOpen_SymlinkTargetUntouched(t *testing.T) {
	dir := t.TempDir()
	outside := t.TempDir()
	target := filepath.Join(outside, "target.db")
	const content = "target content"
	mustWrite(t, target, content)
	if err := os.Chmod(target, 0o640); err != nil {
		t.Fatal(err)
	}
	mustSymlink(t, target, filepath.Join(dir, FileName))

	s, err := Open(context.Background(), dir)

	if err == nil {
		_ = s.Close()
		t.Fatal("Open() error = nil, want an error")
	}
	data, readErr := os.ReadFile(target)
	if readErr != nil {
		t.Fatal(readErr)
	}
	if string(data) != content {
		t.Errorf("content of the link target = %q, want %q", data, content)
	}
	info, statErr := os.Stat(target)
	if statErr != nil {
		t.Fatal(statErr)
	}
	if got := info.Mode().Perm(); got != 0o640 {
		t.Errorf("mode of the link target = %o, want 640", got)
	}
	if got := dirContents(t, outside); len(got) != 1 {
		t.Errorf("entries next to the link target = %v, want only target.db (no -wal or -shm)", got)
	}
}

func TestOpen_DanglingSymlinkTargetNotCreated(t *testing.T) {
	dir := t.TempDir()
	outside := t.TempDir()
	target := filepath.Join(outside, "absent.db")
	mustSymlink(t, target, filepath.Join(dir, FileName))

	s, err := Open(context.Background(), dir)

	if err == nil {
		_ = s.Close()
		t.Fatal("Open() error = nil, want an error")
	}
	if _, statErr := os.Lstat(target); !os.IsNotExist(statErr) {
		t.Errorf("Lstat(%q) error = %v, want the dangling target not to be created", target, statErr)
	}
}

func TestOpen_RefusesNonDatabaseContent(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, FileName)
	content := strings.Repeat("x", 4096)
	if err := os.WriteFile(path, []byte(content), 0o600); err != nil {
		t.Fatal(err)
	}

	s, err := Open(context.Background(), dir)

	if err == nil {
		_ = s.Close()
		t.Fatal("Open(non-SQLite file) error = nil, want an error")
	}
	data, readErr := os.ReadFile(path)
	if readErr != nil {
		t.Fatal(readErr)
	}
	if string(data) != content {
		t.Errorf("content of %s changed after the refused Open, want it untouched", FileName)
	}
}

func TestOpen_RefusesNewerSchema(t *testing.T) {
	dir := t.TempDir()
	first := openStore(t, dir)
	newer := strconv.Itoa(SchemaVersion + 1)
	if _, err := first.db.ExecContext(context.Background(), "UPDATE meta SET value = ? WHERE key = 'schema_version'", newer); err != nil {
		t.Fatalf("update error = %v, want nil", err)
	}
	if err := first.Close(); err != nil {
		t.Fatalf("Close() error = %v, want nil", err)
	}
	path := filepath.Join(dir, FileName)
	before := rawQueryString(t, path, masterSnapshotQuery)

	s, err := Open(context.Background(), dir)

	if err == nil {
		_ = s.Close()
		t.Fatalf("Open(schema_version %s) error = nil, want an error", newer)
	}
	got := rawQueryString(t, path, "SELECT value FROM meta WHERE key = 'schema_version'")
	if got != newer {
		t.Errorf("schema_version after the refused Open = %q, want %q (not modified)", got, newer)
	}
	if after := rawQueryString(t, path, masterSnapshotQuery); after != before {
		t.Errorf("sqlite_master after the refused Open = %q, want unchanged %q", after, before)
	}
}

func TestOpen_SpecialCharactersInDirectory(t *testing.T) {
	parent := t.TempDir()
	dir := filepath.Join(parent, "we?ird#d%20 ir")
	mustMkdir(t, dir)

	s := openStore(t, dir)

	if !s.Created() {
		t.Error("Created() = false, want true")
	}
	if _, err := os.Lstat(filepath.Join(dir, FileName)); err != nil {
		t.Errorf("Lstat(%s in the special directory) error = %v, want the file to exist", FileName, err)
	}
	if got := dirContents(t, parent); len(got) != 1 || got[0] != "we?ird#d%20 ir" {
		t.Errorf("entries in the parent = %v, want only the special directory (no truncated path)", got)
	}
}

func TestStore_SupportsFTS5(t *testing.T) {
	s := openStore(t, t.TempDir())
	ctx := context.Background()

	if _, err := s.db.ExecContext(ctx, "CREATE VIRTUAL TABLE t USING fts5(body)"); err != nil {
		t.Fatalf("CREATE VIRTUAL TABLE ... USING fts5 error = %v, want nil", err)
	}
	if _, err := s.db.ExecContext(ctx, "INSERT INTO t(body) VALUES ('the quick brown fox')"); err != nil {
		t.Fatalf("insert error = %v, want nil", err)
	}

	if got := queryString(t, s.db, "SELECT body FROM t WHERE t MATCH ?", "quick"); got != "the quick brown fox" {
		t.Errorf("MATCH query = %q, want %q", got, "the quick brown fox")
	}
}

// readerOf returns the reader pool of s and fails the test when the store has none.
func readerOf(t *testing.T, s *Store) *sql.DB {
	t.Helper()
	if s.read == nil {
		t.Fatal("Store.read = nil, want the query-only reader pool")
	}
	return s.read
}

func TestStore_Pragmas(t *testing.T) {
	pragmas := []struct{ name, want string }{
		{"journal_mode", "wal"},
		{"synchronous", "2"},
		{"busy_timeout", "5000"},
		{"foreign_keys", "1"},
	}
	t.Run("writer pool", func(t *testing.T) {
		s := openStore(t, t.TempDir())
		for _, p := range pragmas {
			if got := queryString(t, s.db, "PRAGMA "+p.name); got != p.want {
				t.Errorf("writer PRAGMA %s = %q, want %q", p.name, got, p.want)
			}
		}
	})

	t.Run("reader pool", func(t *testing.T) {
		s := openStore(t, t.TempDir())
		reader := readerOf(t, s)
		for _, p := range append(pragmas, struct{ name, want string }{"query_only", "1"}) {
			if got := queryString(t, reader, "PRAGMA "+p.name); got != p.want {
				t.Errorf("reader PRAGMA %s = %q, want %q", p.name, got, p.want)
			}
		}
	})

	t.Run("insert through the reader pool fails", func(t *testing.T) {
		s := openStore(t, t.TempDir())
		reader := readerOf(t, s)

		_, err := reader.ExecContext(context.Background(), "INSERT INTO meta(key, value) VALUES ('reader', 'wrote')")

		if err == nil {
			t.Error("INSERT through the reader pool error = nil, want an error")
		}
	})
}

func TestStore_ReadsAreNotBlockedByTheWriter(t *testing.T) {
	s := openStore(t, t.TempDir())
	ctx := context.Background()
	tx, err := s.db.BeginTx(ctx, nil)
	if err != nil {
		t.Fatalf("BeginTx() error = %v, want nil", err)
	}
	defer func() { _ = tx.Rollback() }()
	if _, err := tx.ExecContext(ctx, "INSERT INTO meta(key, value) VALUES ('held', 'open')"); err != nil {
		t.Fatalf("insert in the open write transaction error = %v, want nil", err)
	}
	q := RecordQuery{Kind: "metric", From: baseTime, To: baseTime.Add(time.Hour), Limit: 10}

	if got, err := s.Records(ctx, q); err != nil || len(got) != 0 {
		t.Errorf("Records() during an open write transaction = %v, %v, want no records and a nil error", got, err)
	}
	if err := s.Ping(ctx); err != nil {
		t.Errorf("Ping() during an open write transaction error = %v, want nil", err)
	}
}

func TestStore_ConcurrentWriters(t *testing.T) {
	const writers, perWriter = 8, 25
	s := openStore(t, t.TempDir())
	errs := make([]error, writers)
	var wg sync.WaitGroup
	for w := range writers {
		wg.Add(1)
		go func() {
			defer wg.Done()
			recs := make([]model.Record, 0, perWriter)
			for i := range perWriter {
				seq := uint64(w*perWriter + i + 1)
				recs = append(recs, metricRecord("node", "cpu.load", seq, baseTime.Add(time.Duration(seq)*time.Second)))
			}
			_, errs[w] = s.WriteBatch(context.Background(), agentBatch(recs...))
		}()
	}
	wg.Wait()

	for w, err := range errs {
		if err != nil {
			t.Errorf("WriteBatch() of writer %d error = %v, want nil", w, err)
		}
	}
	if got := countRows(t, s, "records"); got != writers*perWriter {
		t.Errorf("stored records = %d, want %d", got, writers*perWriter)
	}
}

func TestStore_Close(t *testing.T) {
	t.Run("open store closes both pools", func(t *testing.T) {
		s := openStore(t, t.TempDir())
		if err := s.Close(); err != nil {
			t.Fatalf("Close() error = %v, want nil", err)
		}
		ctx := context.Background()
		q := RecordQuery{Kind: "metric", From: baseTime, To: baseTime.Add(time.Hour), Limit: 10}

		if err := s.Ping(ctx); err == nil {
			t.Error("Ping() after Close error = nil, want an error")
		}
		if _, err := s.Records(ctx, q); err == nil {
			t.Error("Records() after Close error = nil, want an error")
		}
		if _, err := s.WriteBatch(ctx, agentBatch(metricRecord("node", "cpu.load", 1, baseTime))); err == nil {
			t.Error("WriteBatch() after Close error = nil, want an error")
		}
		if s.read != nil {
			if err := s.read.PingContext(ctx); err == nil {
				t.Error("reader pool PingContext() after Close error = nil, want an error")
			}
		}
		if err := s.db.PingContext(ctx); err == nil {
			t.Error("writer pool PingContext() after Close error = nil, want an error")
		}
	})
}
