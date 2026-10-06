package store

import (
	"context"
	"database/sql"
	"path/filepath"
	"slices"
	"strconv"
	"strings"
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
	if SchemaVersion != 3 {
		t.Errorf("SchemaVersion = %d, want 3", SchemaVersion)
	}
	for _, want := range []struct{ typ, name string }{
		{"table", "meta"}, {"table", "records"}, {"table", "metrics"}, {"table", "log_lines"}, {"table", "log_fts"},
		{"index", "records_agent_seq"}, {"index", "records_kind_source_time"}, {"index", "records_kind_time"},
		{"index", "metrics_source_name_time"}, {"table", "import_files"},
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
	for _, table := range []string{"meta", "records", "metrics", "log_lines", "log_fts", "import_files"} {
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

// snapshotWithoutImportFiles returns every sqlite_master row (type, name, sql) except those of import_files and
// its automatic indexes as one text value.
const snapshotWithoutImportFiles = "SELECT coalesce(group_concat(type || '|' || name || '|' || coalesce(sql, ''), char(10)), '') " +
	"FROM (SELECT type, name, sql FROM sqlite_master WHERE name NOT LIKE '%import_files%' ORDER BY type, name)"

// versionTwoDatabase creates a database in dir with the schema steps 1 and 2 only, one metric record and one log
// line record of origin import, and closes it.
func versionTwoDatabase(t *testing.T, dir string) {
	t.Helper()
	ctx := context.Background()
	db, err := sql.Open("sqlite", dsn(filepath.Join(dir, FileName), writerQuery))
	if err != nil {
		t.Fatalf("sql.Open() error = %v, want nil", err)
	}
	defer func() { _ = db.Close() }()
	db.SetMaxOpenConns(1)
	if len(migrations) < 2 {
		t.Fatalf("migrations has %d steps, want at least 2", len(migrations))
	}
	if err := migrate(ctx, db, migrations[:2]); err != nil {
		t.Fatalf("migrate(steps 1 and 2) error = %v, want nil", err)
	}
	for _, stmt := range []string{
		"INSERT INTO records(kind, origin, source, captured_at, received_at) VALUES ('log_line', 'import', 'syslog', 1, 2)",
		"INSERT INTO log_lines(record_id, log, program, pid, message, truncated) VALUES (1, 'syslog', '', 0, 'kept line', 0)",
		"INSERT INTO log_fts(rowid, message) VALUES (1, 'kept line')",
	} {
		if _, err := db.ExecContext(ctx, stmt); err != nil {
			t.Fatalf("version 2 setup %q error = %v, want nil", stmt, err)
		}
	}
}

func TestOpen_MigratesVersion2KeepsRecords(t *testing.T) {
	dir := t.TempDir()
	versionTwoDatabase(t, dir)
	path := filepath.Join(dir, FileName)
	if got := rawQueryString(t, path, "SELECT value FROM meta WHERE key = 'schema_version'"); got != "2" {
		t.Fatalf("schema_version of the prepared database = %q, want %q", got, "2")
	}
	before := rawQueryString(t, path, snapshotWithoutImportFiles)

	s := openStore(t, dir)

	if got := queryString(t, s.db, "SELECT value FROM meta WHERE key = 'schema_version'"); got != "3" {
		t.Errorf("schema_version after the migration = %q, want %q", got, "3")
	}
	if got := masterCount(t, s.db, "table", "import_files"); got != 1 {
		t.Errorf("import_files after the migration: sqlite_master rows = %d, want 1", got)
	}
	if got := queryString(t, s.db, snapshotWithoutImportFiles); got != before {
		t.Errorf("schema objects of steps 1 and 2 after the migration = %q, want unchanged %q", got, before)
	}
	if got := queryString(t, s.db, "SELECT (SELECT count(*) FROM records) || '|' || (SELECT message FROM log_lines)"); got != "1|kept line" {
		t.Errorf("records and log line after the migration = %q, want %q", got, "1|kept line")
	}
	if err := integrityCheck(s); err != nil {
		t.Errorf("FTS5 integrity-check after the migration error = %v, want nil", err)
	}
	if got := countRows(t, s, "import_files"); got != 0 {
		t.Errorf("rows in import_files after the migration = %d, want 0", got)
	}
}

func TestMigrations_Step3CreatesImportFiles(t *testing.T) {
	s := openStore(t, t.TempDir())
	rows, err := s.db.QueryContext(context.Background(), "SELECT name, type, \"notnull\" FROM pragma_table_info('import_files') ORDER BY cid")
	if err != nil {
		t.Fatalf("table_info(import_files) error = %v, want nil", err)
	}
	defer func() { _ = rows.Close() }()
	var got []string
	for rows.Next() {
		var name, typ string
		var notNull int
		if err := rows.Scan(&name, &typ, &notNull); err != nil {
			t.Fatalf("scanning table_info error = %v, want nil", err)
		}
		got = append(got, name+" "+typ+" "+strconv.Itoa(notNull))
	}
	if err := rows.Err(); err != nil {
		t.Fatalf("reading table_info error = %v, want nil", err)
	}
	want := []string{
		"id INTEGER 0", "sha256 BLOB 1", "size INTEGER 1", "name TEXT 1", "file_name BLOB 1", "mod_time INTEGER 0",
		"source_type TEXT 1", "records INTEGER 1", "complete INTEGER 1", "started_at INTEGER 1", "completed_at INTEGER 0",
	}
	if !slices.Equal(got, want) {
		t.Errorf("columns of import_files (name type notnull) = %q, want %q", got, want)
	}
	if sqlText := queryString(t, s.db, "SELECT sql FROM sqlite_master WHERE name = 'import_files'"); !strings.Contains(sqlText, "STRICT") {
		t.Errorf("import_files definition = %q, want a STRICT table", sqlText)
	}
}

func TestMigrations_Step3Constraints(t *testing.T) {
	sum := make([]byte, 32)
	tests := []struct {
		name    string
		sha     []byte
		size    int64
		file    []byte
		records int64
		done    int64
		wantErr bool
	}{
		{"valid row", sum, 0, []byte("name"), 0, 0, false},
		{"file name of 1024 bytes", sum, 0, make([]byte, 1024), 0, 0, false},
		{"hash of 31 bytes", sum[:31], 0, []byte("n"), 0, 0, true},
		{"hash of 33 bytes", append(slices.Clone(sum), 0), 0, []byte("n"), 0, 0, true},
		{"negative size", sum, -1, []byte("n"), 0, 0, true},
		{"file name of 1025 bytes", sum, 0, make([]byte, 1025), 0, 0, true},
		{"negative records", sum, 0, []byte("n"), -1, 0, true},
		{"complete other than 0 or 1", sum, 0, []byte("n"), 0, 2, true},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			s := openStore(t, t.TempDir())

			_, err := s.db.ExecContext(context.Background(),
				"INSERT INTO import_files(sha256, size, name, file_name, source_type, records, complete, started_at) VALUES (?, ?, 'n', ?, 'syslog', ?, ?, 1)",
				tc.sha, tc.size, tc.file, tc.records, tc.done)

			if (err != nil) != tc.wantErr {
				t.Errorf("insert into import_files error = %v, want an error: %v", err, tc.wantErr)
			}
		})
	}
	t.Run("defaults and unique hash", func(t *testing.T) {
		s := openStore(t, t.TempDir())
		const insert = "INSERT INTO import_files(sha256, size, name, file_name, source_type, started_at) VALUES (?, 1, 'n', ?, 'syslog', 1)"
		if _, err := s.db.ExecContext(context.Background(), insert, sum, []byte("n")); err != nil {
			t.Fatalf("first insert error = %v, want nil", err)
		}

		if got := queryString(t, s.db, "SELECT records || '|' || complete || '|' || coalesce(completed_at, 'null') || '|' || coalesce(mod_time, 'null') FROM import_files"); got != "0|0|null|null" {
			t.Errorf("defaults of records, complete, completed_at, mod_time = %q, want %q", got, "0|0|null|null")
		}
		if _, err := s.db.ExecContext(context.Background(), insert, sum, []byte("n")); err == nil {
			t.Error("second insert with the same hash error = nil, want a unique violation")
		}
	})
}
