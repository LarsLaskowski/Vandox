// Package store is the backend's SQLite database.
package store

import (
	"context"
	"database/sql"
	"errors"
	"fmt"
	"io/fs"
	"net/url"
	"os"
	"path/filepath"
	"strconv"

	// Register the pure-Go SQLite driver under the name "sqlite".
	_ "modernc.org/sqlite"
)

// FileName is the name of the database file in the storage directory.
const FileName = "vandox.db"

// SchemaVersion is the database schema version this build reads and writes.
const SchemaVersion = 1

// filePerm is the mode of the database file; SQLite gives its -wal and -shm files the same mode.
const filePerm = 0o600

// dsnQuery sets, on every connection, WAL mode, a busy timeout and foreign key enforcement.
const dsnQuery = "_pragma=journal_mode(WAL)&_pragma=busy_timeout(5000)&_pragma=foreign_keys(1)"

// Store is the backend's SQLite database.
type Store struct {
	db      *sql.DB
	created bool
}

// Open opens the database FileName in the existing directory dir, creating the file (mode 0600) if it does
// not exist, in WAL mode, and checks its schema version.
func Open(ctx context.Context, dir string) (*Store, error) {
	info, err := os.Stat(dir)
	if err != nil {
		return nil, fmt.Errorf("store: checking storage directory: %w", err)
	}
	if !info.IsDir() {
		return nil, fmt.Errorf("store: checking storage directory: %s is not a directory", dir)
	}
	path := filepath.Join(dir, FileName)
	created, err := prepareFile(path)
	if err != nil {
		return nil, err
	}
	for _, suffix := range []string{"-wal", "-shm"} {
		if err := requireRegularIfPresent(path + suffix); err != nil {
			return nil, err
		}
	}
	db, err := sql.Open("sqlite", dsn(path))
	if err != nil {
		return nil, fmt.Errorf("store: opening database: %w", err)
	}
	if err := initialize(ctx, db); err != nil {
		_ = db.Close()
		return nil, err
	}
	return &Store{db: db, created: created}, nil
}

// dsn returns the SQLite URI of the database file at path. The path goes in only through url.URL.Path, so
// characters such as ?, # and % cannot add parameters or cut the path.
func dsn(path string) string {
	return (&url.URL{Scheme: "file", Path: path, RawQuery: dsnQuery}).String()
}

// prepareFile creates the database file exclusively with mode 0600, or checks that an existing entry is a
// regular file. SQLite follows a symbolic link of the main file and would place the database and its
// -wal/-shm files at the link target, so links are refused (os.Lstat, never os.Stat).
func prepareFile(path string) (created bool, err error) {
	f, err := os.OpenFile(path, os.O_RDWR|os.O_CREATE|os.O_EXCL, filePerm)
	if err == nil {
		if err := f.Close(); err != nil {
			return false, fmt.Errorf("store: creating %s: %w", path, err)
		}
		return true, nil
	}
	if !errors.Is(err, fs.ErrExist) {
		return false, fmt.Errorf("store: creating %s: %w", path, err)
	}
	if err := requireRegular(path); err != nil {
		return false, err
	}
	return false, nil
}

// requireRegularIfPresent refuses path when it exists and is not a regular file.
func requireRegularIfPresent(path string) error {
	err := requireRegular(path)
	if errors.Is(err, fs.ErrNotExist) {
		return nil
	}
	return err
}

// requireRegular returns an error unless path is a regular file (not a symbolic link, directory or device).
func requireRegular(path string) error {
	info, err := os.Lstat(path)
	if err != nil {
		return fmt.Errorf("store: checking %s: %w", path, err)
	}
	if !info.Mode().IsRegular() {
		return fmt.Errorf("store: checking %s: not a regular file (%s)", path, info.Mode().Type())
	}
	return nil
}

// initialize verifies WAL mode and creates or checks the meta table.
func initialize(ctx context.Context, db *sql.DB) error {
	var mode string
	if err := db.QueryRowContext(ctx, "PRAGMA journal_mode").Scan(&mode); err != nil {
		return fmt.Errorf("store: reading journal mode: %w", err)
	}
	if mode != "wal" {
		return fmt.Errorf("store: journal mode is %q, want wal (the file system must support shared memory)", mode)
	}
	if _, err := db.ExecContext(ctx, "CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL) STRICT"); err != nil {
		return fmt.Errorf("store: creating meta table: %w", err)
	}
	if _, err := db.ExecContext(ctx, "INSERT OR IGNORE INTO meta(key, value) VALUES ('schema_version', ?)", strconv.Itoa(SchemaVersion)); err != nil {
		return fmt.Errorf("store: writing schema version: %w", err)
	}
	version, err := schemaVersion(ctx, db)
	if err != nil {
		return err
	}
	if version != strconv.Itoa(SchemaVersion) {
		return fmt.Errorf("store: schema version %q is not supported, this build uses %d", version, SchemaVersion)
	}
	return nil
}

// schemaVersion reads the schema version stored in the meta table.
func schemaVersion(ctx context.Context, db *sql.DB) (string, error) {
	var version string
	if err := db.QueryRowContext(ctx, "SELECT value FROM meta WHERE key = 'schema_version'").Scan(&version); err != nil {
		return "", fmt.Errorf("store: reading schema version: %w", err)
	}
	return version, nil
}

// Created reports whether Open created the database file.
func (s *Store) Created() bool {
	return s.created
}

// Ping reads the schema version and returns an error when the database cannot be read.
func (s *Store) Ping(ctx context.Context) error {
	_, err := schemaVersion(ctx, s.db)
	return err
}

// Close closes the database.
func (s *Store) Close() error {
	if err := s.db.Close(); err != nil {
		return fmt.Errorf("store: closing database: %w", err)
	}
	return nil
}
