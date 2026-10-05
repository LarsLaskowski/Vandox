// Package store is the backend's SQLite database.
package store

import (
	"context"
	"database/sql"
	"errors"

	// Register the pure-Go SQLite driver under the name "sqlite".
	_ "modernc.org/sqlite"
)

// FileName is the name of the database file in the storage directory.
const FileName = "vandox.db"

// SchemaVersion is the database schema version this build reads and writes.
const SchemaVersion = 1

var errNotImplemented = errors.New("not implemented")

// Store is the backend's SQLite database.
type Store struct {
	db      *sql.DB
	created bool
}

// Open opens the database FileName in the existing directory dir, creating the file (mode 0600) if it does
// not exist, in WAL mode, and checks its schema version.
func Open(ctx context.Context, dir string) (*Store, error) {
	return nil, errNotImplemented
}

// Created reports whether Open created the database file.
func (s *Store) Created() bool {
	return false
}

// Ping reads the schema version and returns an error when the database cannot be read.
func (s *Store) Ping(ctx context.Context) error {
	return errNotImplemented
}

// Close closes the database.
func (s *Store) Close() error {
	return errNotImplemented
}
