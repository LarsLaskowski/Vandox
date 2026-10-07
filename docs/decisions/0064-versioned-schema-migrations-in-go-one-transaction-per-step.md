# 0064: Versioned schema migrations in Go, applied at start-up in one transaction per step; a newer schema is refused

- **Status:** Superseded by [0077](0077-storage-on-microsoft-data-sqlite-same-schema-and-rules.md)
- **Date:** 2026-10-06
- **Source:** Issue #14
- **Supersedes:** —

## Context

Issue #14 asks for versioned migrations applied idempotently at start-up. #13 created the `meta` table with
`schema_version = '1'` and refuses any other value; `/healthz` reads that value to check the database (0059),
so the version must stay in `meta`. Databases at version 1 exist wherever the #13 image ran. `vandoxd` and a
second process (the `vandoxd import` sub-command of #15) may open the same file at the same time. #85 later
requires upgrades without data loss.

## Options considered

1. **A migration library** (e.g. golang-migrate, goose) — feature-rich; a new dependency (tier `security`
   on every update), its own version table next to `meta`, and SQL files to embed.
2. **`PRAGMA user_version`** as the version — built into SQLite; but `/healthz` and the #13 databases use
   `meta.schema_version`, and a pragma cannot be read and written in the same statement as the data change.
3. **An ordered list of migrations in Go, version in `meta.schema_version`** — no dependency, the existing
   version row stays the single source, each step is one SQL script.

## Decision

Option 3. `migrations` is an ordered list of steps `{version, SQL}`; step 1 is the #13 layout (`meta`),
step 2 the record schema (0063); `SchemaVersion` equals the last step's version. `Open` runs `migrate`:

- Each step runs in its own write transaction (`BEGIN IMMEDIATE`, 0065), which first re-reads the version,
  skips the step when the database is already at or past it, otherwise executes the step and writes the new
  version in the same transaction. A failing step rolls back completely; earlier steps stay committed.
- A missing `meta` table or version row is version 0 (new database).
- A version above `SchemaVersion` (written by a newer build) or one that is not a decimal integer is refused
  with an error before anything is written; the database is left unchanged.
- Opening a database that is already at `SchemaVersion` executes no step.

## Consequences

- Start-up is idempotent, and two processes opening the database at once apply each step once: the second
  waits on the write lock (`busy_timeout`, 0065) and then sees the new version.
- A downgrade is refused rather than risking a newer schema being written by an older build; going back
  needs a backup (#55).
- Every schema change from now on is a new step with a test that migrates from the previous version; steps
  are never edited once released.
- SQLite DDL is transactional, so this also holds for `CREATE VIRTUAL TABLE ... USING fts5`.
