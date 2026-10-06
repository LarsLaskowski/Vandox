package store

import (
	"context"
	"database/sql"
	"path/filepath"
	"slices"
	"strconv"
	"sync"
	"testing"
)

// masterSnapshotQuery returns every sqlite_master row (type, name, sql) as one text value.
const masterSnapshotQuery = "SELECT coalesce(group_concat(type || '|' || name || '|' || coalesce(sql, ''), char(10)), '') " +
	"FROM (SELECT type, name, sql FROM sqlite_master ORDER BY type, name)"

// masterCount counts the sqlite_master rows of the given type and name.
func masterCount(t *testing.T, db *sql.DB, typ, name string) int {
	t.Helper()
	var n int
	if err := db.QueryRowContext(context.Background(),
		"SELECT count(*) FROM sqlite_master WHERE type = ? AND name = ?", typ, name).Scan(&n); err != nil {
		t.Fatalf("count of %s %s error = %v, want nil", typ, name, err)
	}
	return n
}

// legacyDatabase creates a database in dir in the layout of schema version 1 (only meta) with the given
// schema_version value and an extra row ('probe', 'kept').
func legacyDatabase(t *testing.T, dir, version string) {
	t.Helper()
	db, err := sql.Open("sqlite", dsn(filepath.Join(dir, FileName), "_pragma=journal_mode(WAL)"))
	if err != nil {
		t.Fatalf("sql.Open() error = %v, want nil", err)
	}
	defer func() { _ = db.Close() }()
	for _, stmt := range []string{
		"CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL) STRICT",
		"INSERT INTO meta(key, value) VALUES ('probe', 'kept')",
	} {
		if _, err := db.ExecContext(context.Background(), stmt); err != nil {
			t.Fatalf("legacy setup %q error = %v, want nil", stmt, err)
		}
	}
	if _, err := db.ExecContext(context.Background(), "INSERT INTO meta(key, value) VALUES ('schema_version', ?)", version); err != nil {
		t.Fatalf("legacy setup schema_version error = %v, want nil", err)
	}
}

// writerPool opens a database in a new directory the way Open's writer pool does, without migrating it.
func writerPool(t *testing.T) *sql.DB {
	t.Helper()
	db, err := sql.Open("sqlite", dsn(filepath.Join(t.TempDir(), FileName), writerQuery))
	if err != nil {
		t.Fatalf("sql.Open() error = %v, want nil", err)
	}
	db.SetMaxOpenConns(1)
	t.Cleanup(func() { _ = db.Close() })
	return db
}

func TestOpen_CreatesSchema(t *testing.T) {
	s := openStore(t, t.TempDir())

	if got := queryString(t, s.db, "SELECT value FROM meta WHERE key = 'schema_version'"); got != strconv.Itoa(SchemaVersion) {
		t.Errorf("meta schema_version = %q, want %q", got, strconv.Itoa(SchemaVersion))
	}
	if SchemaVersion != 2 {
		t.Errorf("SchemaVersion = %d, want 2", SchemaVersion)
	}
	for _, want := range []struct{ typ, name string }{
		{"table", "meta"}, {"table", "records"}, {"table", "metrics"}, {"table", "log_lines"}, {"table", "log_fts"},
		{"index", "records_agent_seq"}, {"index", "records_kind_source_time"}, {"index", "records_kind_time"},
		{"index", "metrics_source_name_time"},
	} {
		if got := masterCount(t, s.db, want.typ, want.name); got != 1 {
			t.Errorf("sqlite_master rows of %s %s = %d, want 1", want.typ, want.name, got)
		}
	}
	var triggers int
	if err := s.db.QueryRowContext(context.Background(), "SELECT count(*) FROM sqlite_master WHERE type = 'trigger'").Scan(&triggers); err != nil {
		t.Fatalf("trigger count error = %v, want nil", err)
	}
	if triggers != 0 {
		t.Errorf("triggers in sqlite_master = %d, want 0 (the FTS index is filled by the write path)", triggers)
	}
}

func TestOpen_SecondOpenAppliesNoStep(t *testing.T) {
	dir := t.TempDir()
	first := openStore(t, dir)
	for _, stmt := range []string{
		"INSERT INTO meta(key, value) VALUES ('probe', 'kept')",
		"INSERT INTO records(kind, origin, source, captured_at, received_at) VALUES ('metric', 'import', 'node', 1, 2)",
	} {
		if _, err := first.db.ExecContext(context.Background(), stmt); err != nil {
			t.Fatalf("%q error = %v, want nil", stmt, err)
		}
	}
	masterBefore := queryString(t, first.db, masterSnapshotQuery)
	dataBefore := queryString(t, first.db, "SELECT (SELECT count(*) FROM records) || '|' || (SELECT value FROM meta WHERE key = 'probe')")
	if err := first.Close(); err != nil {
		t.Fatalf("Close() error = %v, want nil", err)
	}

	second := openStore(t, dir)

	if got := queryString(t, second.db, masterSnapshotQuery); got != masterBefore {
		t.Errorf("sqlite_master after the second Open = %q, want unchanged %q", got, masterBefore)
	}
	if got := queryString(t, second.db, "SELECT (SELECT count(*) FROM records) || '|' || (SELECT value FROM meta WHERE key = 'probe')"); got != dataBefore {
		t.Errorf("data after the second Open = %q, want unchanged %q", got, dataBefore)
	}
}

func TestOpen_MigratesVersion1(t *testing.T) {
	dir := t.TempDir()
	legacyDatabase(t, dir, "1")

	s := openStore(t, dir)

	if s.Created() {
		t.Error("Created() = true for an existing file, want false")
	}
	if got := queryString(t, s.db, "SELECT value FROM meta WHERE key = 'schema_version'"); got != strconv.Itoa(SchemaVersion) {
		t.Errorf("schema_version after the migration = %q, want %q", got, strconv.Itoa(SchemaVersion))
	}
	if got := queryString(t, s.db, "SELECT value FROM meta WHERE key = 'probe'"); got != "kept" {
		t.Errorf("probe row after the migration = %q, want %q", got, "kept")
	}
	if got := masterCount(t, s.db, "table", "records"); got != 1 {
		t.Errorf("records table after the migration: sqlite_master rows = %d, want 1", got)
	}
}

func TestOpen_RefusesMalformedSchemaVersion(t *testing.T) {
	for _, tc := range []struct{ name, version string }{
		{"letter", "x"},
		{"empty", ""},
		{"decimal", "1.5"},
		{"negative", "-1"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			dir := t.TempDir()
			legacyDatabase(t, dir, tc.version)
			path := filepath.Join(dir, FileName)
			before := rawQueryString(t, path, masterSnapshotQuery)

			s, err := Open(context.Background(), dir)

			if err == nil {
				_ = s.Close()
				t.Fatalf("Open(schema_version %q) error = nil, want an error", tc.version)
			}
			if got := rawQueryString(t, path, "SELECT value FROM meta WHERE key = 'schema_version'"); got != tc.version {
				t.Errorf("schema_version after the refused Open = %q, want %q", got, tc.version)
			}
			if got := rawQueryString(t, path, masterSnapshotQuery); got != before {
				t.Errorf("sqlite_master after the refused Open = %q, want unchanged %q", got, before)
			}
		})
	}
}

func TestMigrate_FailingStepRollsBackThatStep(t *testing.T) {
	db := writerPool(t)
	steps := []migration{
		{version: 1, stmts: []string{
			"CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL) STRICT",
			"CREATE TABLE step_one (x INTEGER)",
		}},
		{version: 2, stmts: []string{
			"CREATE TABLE step_two_first (x INTEGER)",
			"INSERT INTO no_such_table VALUES (1)",
		}},
	}

	err := migrate(context.Background(), db, steps)

	if err == nil {
		t.Fatal("migrate() with a failing step 2 error = nil, want an error")
	}
	if got := queryString(t, db, "SELECT value FROM meta WHERE key = 'schema_version'"); got != "1" {
		t.Errorf("schema_version after the failed step = %q, want %q", got, "1")
	}
	if got := masterCount(t, db, "table", "step_two_first"); got != 0 {
		t.Errorf("table of the failing step's first statement: rows = %d, want 0 (whole step rolled back)", got)
	}
	if got := masterCount(t, db, "table", "step_one"); got != 1 {
		t.Errorf("table of step 1: rows = %d, want 1", got)
	}
}

func TestMigrate_AppliesEachStepOnce(t *testing.T) {
	db := writerPool(t)
	steps := []migration{
		{version: 1, stmts: []string{"CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL) STRICT"}},
		{version: 2, stmts: []string{"CREATE TABLE counter (x INTEGER)", "INSERT INTO counter VALUES (1)"}},
	}
	ctx := context.Background()

	for i := range 2 {
		if err := migrate(ctx, db, steps); err != nil {
			t.Fatalf("migrate() call %d error = %v, want nil", i+1, err)
		}
	}

	if got := queryString(t, db, "SELECT count(*) FROM counter"); got != "1" {
		t.Errorf("rows in counter after two migrate calls = %s, want 1 (steps are not repeated)", got)
	}
	if got := queryString(t, db, "SELECT value FROM meta WHERE key = 'schema_version'"); got != "2" {
		t.Errorf("schema_version = %q, want %q", got, "2")
	}
}

func TestMigrate_RefusesDatabaseNewerThanSteps(t *testing.T) {
	db := writerPool(t)
	ctx := context.Background()
	steps := []migration{{version: 1, stmts: []string{
		"CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL) STRICT",
	}}}
	if err := migrate(ctx, db, steps); err != nil {
		t.Fatalf("migrate() setup error = %v, want nil", err)
	}
	if _, err := db.ExecContext(ctx, "UPDATE meta SET value = '5' WHERE key = 'schema_version'"); err != nil {
		t.Fatalf("update error = %v, want nil", err)
	}

	if err := migrate(ctx, db, steps); err == nil {
		t.Error("migrate() on a database newer than the last step error = nil, want an error")
	}
	if got := queryString(t, db, "SELECT value FROM meta WHERE key = 'schema_version'"); got != "5" {
		t.Errorf("schema_version after the refusal = %q, want %q", got, "5")
	}
}

func TestMigrate_CancelledContext(t *testing.T) {
	db := writerPool(t)
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	steps := []migration{{version: 1, stmts: []string{"CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL) STRICT"}}}

	if err := migrate(ctx, db, steps); err == nil {
		t.Error("migrate(cancelled ctx) error = nil, want an error")
	}
}

// currentVersionAfter runs the setup statements on a new database and reads its version through a transaction.
func currentVersionAfter(t *testing.T, setup []string) (int, error) {
	t.Helper()
	db := writerPool(t)
	ctx := context.Background()
	for _, stmt := range setup {
		if _, err := db.ExecContext(ctx, stmt); err != nil {
			t.Fatalf("setup %q error = %v, want nil", stmt, err)
		}
	}
	tx, err := db.BeginTx(ctx, nil)
	if err != nil {
		t.Fatalf("BeginTx() error = %v, want nil", err)
	}
	defer func() { _ = tx.Rollback() }()
	return currentVersion(ctx, tx)
}

func TestCurrentVersion(t *testing.T) {
	const createMeta = "CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL) STRICT"
	for _, tc := range []struct {
		name    string
		setup   []string
		want    int
		wantErr bool
	}{
		{"no meta table", nil, 0, false},
		{"meta without the row", []string{createMeta}, 0, false},
		{"version row", []string{createMeta, "INSERT INTO meta VALUES ('schema_version', '7')"}, 7, false},
		{"not a number", []string{createMeta, "INSERT INTO meta VALUES ('schema_version', 'x')"}, 0, true},
	} {
		t.Run(tc.name, func(t *testing.T) {
			got, err := currentVersionAfter(t, tc.setup)

			if (err != nil) != tc.wantErr {
				t.Fatalf("currentVersion() error = %v, want error: %v", err, tc.wantErr)
			}
			if got != tc.want {
				t.Errorf("currentVersion() = %d, want %d", got, tc.want)
			}
		})
	}
}

func TestOpen_ConcurrentOpensOfANewDirectory(t *testing.T) {
	dir := t.TempDir()
	const opens = 2
	stores := make([]*Store, opens)
	errs := make([]error, opens)
	var wg sync.WaitGroup
	for i := range opens {
		wg.Add(1)
		go func() {
			defer wg.Done()
			stores[i], errs[i] = Open(context.Background(), dir)
		}()
	}
	wg.Wait()
	for _, s := range stores {
		if s != nil {
			t.Cleanup(func() { _ = s.Close() })
		}
	}

	for i, err := range errs {
		if err != nil {
			t.Fatalf("concurrent Open() %d error = %v, want nil", i, err)
		}
	}
	created := 0
	for _, s := range stores {
		if s.Created() {
			created++
		}
	}
	if created != 1 {
		t.Errorf("stores reporting Created() = %d, want exactly 1", created)
	}
	if got := queryString(t, stores[0].db, "SELECT value FROM meta WHERE key = 'schema_version'"); got != strconv.Itoa(SchemaVersion) {
		t.Errorf("schema_version = %q, want %q", got, strconv.Itoa(SchemaVersion))
	}
	for _, table := range []string{"meta", "records", "metrics", "log_lines", "log_fts"} {
		if got := masterCount(t, stores[0].db, "table", table); got != 1 {
			t.Errorf("sqlite_master rows of table %s = %d, want 1", table, got)
		}
	}
}

func TestMigrations_AreOrderedWithoutGaps(t *testing.T) {
	if len(migrations) == 0 {
		t.Fatal("migrations is empty, want at least the steps 1 and 2")
	}
	versions := make([]int, 0, len(migrations))
	want := make([]int, 0, len(migrations))
	for i, m := range migrations {
		versions = append(versions, m.version)
		want = append(want, i+1)
		if len(m.stmts) == 0 {
			t.Errorf("migrations[%d] has no statements, want at least one", i)
		}
	}
	if !slices.Equal(versions, want) {
		t.Errorf("migration versions = %v, want %v", versions, want)
	}
	if last := migrations[len(migrations)-1].version; last != SchemaVersion {
		t.Errorf("last migration version = %d, want SchemaVersion (%d)", last, SchemaVersion)
	}
}
