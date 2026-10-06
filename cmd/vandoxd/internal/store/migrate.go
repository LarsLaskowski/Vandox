package store

import (
	"context"
	"database/sql"
	"errors"
	"fmt"
	"strconv"
)

// migration is one schema step.
type migration struct {
	version int      // the schema version after this step
	stmts   []string // executed in order in one transaction
}

// migrations are the schema steps in ascending order without gaps; the last version is SchemaVersion.
var migrations = []migration{
	{version: 1, stmts: []string{
		"CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL) STRICT",
	}},
	{version: 2, stmts: []string{
		`CREATE TABLE records (
  id INTEGER PRIMARY KEY,
  kind TEXT NOT NULL,
  origin TEXT NOT NULL,
  source TEXT NOT NULL,
  agent_id TEXT,
  seq INTEGER,
  captured_at INTEGER NOT NULL,
  received_at INTEGER NOT NULL,
  boot_id TEXT,
  clock_offset_ns INTEGER,
  data TEXT
) STRICT`,
		"CREATE UNIQUE INDEX records_agent_seq ON records(agent_id, seq) WHERE origin = 'agent'",
		"CREATE INDEX records_kind_source_time ON records(kind, source, captured_at)",
		"CREATE INDEX records_kind_time ON records(kind, captured_at)",
		`CREATE TABLE metrics (
  record_id INTEGER PRIMARY KEY REFERENCES records(id),
  source TEXT NOT NULL,
  name TEXT NOT NULL,
  captured_at INTEGER NOT NULL,
  value REAL NOT NULL,
  unit TEXT NOT NULL,
  labels TEXT
) STRICT`,
		"CREATE INDEX metrics_source_name_time ON metrics(source, name, captured_at)",
		`CREATE TABLE log_lines (
  record_id INTEGER PRIMARY KEY REFERENCES records(id),
  log TEXT NOT NULL,
  program TEXT NOT NULL,
  pid INTEGER NOT NULL,
  priority INTEGER,
  message TEXT NOT NULL,
  truncated INTEGER NOT NULL
) STRICT`,
		`CREATE VIRTUAL TABLE log_fts USING fts5(message, content='log_lines', content_rowid='record_id',
  tokenize='unicode61 remove_diacritics 2')`,
	}},
	{version: 3, stmts: []string{
		`CREATE TABLE import_files (
  id INTEGER PRIMARY KEY,
  sha256 BLOB NOT NULL UNIQUE CHECK (length(sha256) = 32),
  size INTEGER NOT NULL CHECK (size >= 0),
  name TEXT NOT NULL,
  file_name BLOB NOT NULL CHECK (length(file_name) <= 1024),
  mod_time INTEGER,
  source_type TEXT NOT NULL,
  records INTEGER NOT NULL DEFAULT 0 CHECK (records >= 0),
  complete INTEGER NOT NULL DEFAULT 0 CHECK (complete IN (0, 1)),
  started_at INTEGER NOT NULL,
  completed_at INTEGER
) STRICT`,
	}},
}

// migrate applies every step of steps above the database's current version, each in one transaction.
func migrate(ctx context.Context, db *sql.DB, steps []migration) error {
	if len(steps) == 0 {
		return nil
	}
	latest := steps[len(steps)-1].version
	for _, step := range steps {
		if err := applyStep(ctx, db, step, latest); err != nil {
			return err
		}
	}
	return nil
}

// applyStep runs step in one transaction unless the database is already at or above its version. The
// version is read inside the transaction, which the writer pool starts as an immediate one, so concurrent
// openers apply a step once.
func applyStep(ctx context.Context, db *sql.DB, step migration, latest int) error {
	tx, err := db.BeginTx(ctx, nil)
	if err != nil {
		return fmt.Errorf("store: starting migration %d: %w", step.version, err)
	}
	defer func() { _ = tx.Rollback() }()
	current, err := currentVersion(ctx, tx)
	if err != nil {
		return err
	}
	if current > latest {
		return fmt.Errorf("store: schema version %d is not supported, this build uses %d", current, latest)
	}
	if current >= step.version {
		return nil
	}
	for _, stmt := range step.stmts {
		if _, err := tx.ExecContext(ctx, stmt); err != nil {
			return fmt.Errorf("store: migration %d: %w", step.version, err)
		}
	}
	const upsert = "INSERT INTO meta(key, value) VALUES ('schema_version', ?) ON CONFLICT(key) DO UPDATE SET value = excluded.value"
	if _, err := tx.ExecContext(ctx, upsert, strconv.Itoa(step.version)); err != nil {
		return fmt.Errorf("store: migration %d: writing schema version: %w", step.version, err)
	}
	if err := tx.Commit(); err != nil {
		return fmt.Errorf("store: migration %d: %w", step.version, err)
	}
	return nil
}

// currentVersion reads the schema version inside tx; it is 0 when meta or its row is missing.
func currentVersion(ctx context.Context, tx *sql.Tx) (int, error) {
	var tables int
	if err := tx.QueryRowContext(ctx, "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'meta'").Scan(&tables); err != nil {
		return 0, fmt.Errorf("store: reading schema version: %w", err)
	}
	if tables == 0 {
		return 0, nil
	}
	var text string
	err := tx.QueryRowContext(ctx, "SELECT value FROM meta WHERE key = 'schema_version'").Scan(&text)
	if errors.Is(err, sql.ErrNoRows) {
		return 0, nil
	}
	if err != nil {
		return 0, fmt.Errorf("store: reading schema version: %w", err)
	}
	v, err := strconv.ParseUint(text, 10, 31)
	if err != nil {
		return 0, fmt.Errorf("store: schema version %q is not a version number", text)
	}
	return int(v), nil
}
