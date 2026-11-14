# 0212: Retention frees space inside the database file, not on the volume

- **Status:** Accepted
- **Date:** 2026-11-13
- **Area:** Storage
- **Source:** Issue #46
- **Supersedes:** 0177 — amends its consequence that ingest resumes at the free-space floor once retention removes data: removal alone
  does not free space on the volume.

## Context

Record 0177 expects ingest that stopped at `ingest.min_free` to resume when "retention (#46) removes data". SQLite keeps the pages of
deleted rows in the file and reuses them for later writes; the file shrinks only through `VACUUM` or incremental vacuum, which issue
#55 (database maintenance and backup) owns. The database runs without `auto_vacuum`.

## Options considered

1. **Vacuum after each removal pass** — returns space, but a full `VACUUM` rewrites the whole file under the writer lock (minutes on
   the reference host), against "retention never blocks ingest", and needs free space of the file's size.
2. **Switch to incremental `auto_vacuum` in this change** — needs a full `VACUUM` once to switch, the same cost, and is the subject of #55.
3. **Remove rows only; the file stops growing but does not shrink** (chosen) — bounded size in steady state; returning space stays
   with #55.

## Decision

Retention deletes rows in bounded steps and never vacuums; a database at the free-space floor resumes ingest only when space on the
volume is freed (or #55 shrinks the file). The behavior is stated in [Retention and rollups](../areas/retention.md) and [Ingest
endpoint](../areas/ingest-endpoint.md).

## Consequences

- The size criterion of #46 is about growth: after the periods are full, new data reuses the freed pages.
- Until #55, an operator at the floor frees space on the volume by hand.
- A removed log line leaves a `delete` entry in the FTS5 index, which frees no page until its segments merge; each removal step that
  removed log lines therefore ends with an incremental merge of at most 64 pages, bounded like the step itself, so the index reuses its
  pages instead of growing.
