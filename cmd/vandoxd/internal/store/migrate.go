package store

import (
	"context"
	"database/sql"
	"errors"
)

// migration is one schema step.
type migration struct {
	version int      // the schema version after this step
	stmts   []string // executed in order in one transaction
}

// migrations are the schema steps in ascending order without gaps; the last version is SchemaVersion.
var migrations = []migration{}

// migrate applies every step of steps above the database's current version, each in one transaction.
func migrate(ctx context.Context, db *sql.DB, steps []migration) error {
	return errors.New("not implemented")
}

// currentVersion reads the schema version inside tx; it is 0 when meta or its row is missing.
func currentVersion(ctx context.Context, tx *sql.Tx) (int, error) {
	return 0, errors.New("not implemented")
}
