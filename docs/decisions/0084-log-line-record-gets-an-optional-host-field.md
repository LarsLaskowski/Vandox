# 0084: The log_line record gets an optional host field in both languages and in storage

- **Status:** Proposed
- **Date:** 2026-10-09
- **Area:** Wire format
- **Source:** Issue #16
- **Supersedes:** —

## Context

Issue #16 asks the system log parsers for timestamp, **host**, program, PID, severity and message, and requires that
the backend's parsers and the agent's own line parsing (#37) "agree on the fields of the `log_line` record" through the
golden wire fixture ([0075](0075-wire-contract-pinned-by-golden-fixtures.md)). The record had no host field in Go or
C#, and the `log_lines` table had no host column ([0063](0063-storage-schema-records-table-typed-metric-and-log-tables-json-payloads.md)).
Imported records have no agent ID, so the host named in a syslog line or the journal's `_HOSTNAME` is the only
information about which machine wrote them. No version of the wire format or the schema has been released (no `v*` tag).

## Options considered

1. **Drop the host** — nothing to change outside the parsers; the requirement is not met, and imports from more than
   one machine (a relay, a second server, #89) cannot be told apart.
2. **Put the host into the record's `source`** — no model change; but `source` names the producer (collector or parser)
   and is restricted to the name pattern, which host fields in syslog lines need not match.
3. **An optional `host` field on `log_line`** (chosen) — one additive field in the Go and C# models, the golden batch, the
   decoder and a new `log_lines.host` column; the agent may leave it empty.
4. **Option 3 with a minor version bump to 1.1** — the formal way for an additive field (0043), but no producer or
   consumer of 1.0 exists outside this repository, so the bump would only change tests and constants.

## Decision

Option 3: `log_line` gets an optional short-text `host`, written and read in both languages, pinned by the golden batch
and stored in `log_lines.host` (schema step 4, existing rows empty). The wire version stays 1.0 because it has not been
released. The field rules are in [Wire format](../areas/wire-format.md) (*`log_line`*) and
[Storage](../areas/storage.md).

## Consequences

- The agent's log shipping (#37) can fill `host` or leave it out; the backend accepts both.
- Once a version is released, the next additive field raises the minor version as 0043 describes.
- The host is stored, not indexed for search; a host filter in queries needs its own change.
