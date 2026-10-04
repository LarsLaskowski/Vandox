# 0018: The agent spools at least 7 days and backfills gaplessly and idempotently

- **Status:** Superseded by [0045](0045-batch-identified-by-agent-id-and-record-sequence-numbers.md)
- **Date:** 2026-10-03
- **Source:** Issue #6
- **Supersedes:** —

## Context

The backend host usually runs 24/7 but is switched off at night (typically 22:00–09:00) a few times a year, and
it may be unavailable longer (maintenance, holidays). Outages often happen exactly when nobody is
watching; losing that data defeats the purpose of forensics.

## Options considered

1. **Drop data while the backend is unreachable** — simple; gaps exactly when needed.
2. **Small in-memory buffer** — covers short blips; lost on agent restart or after hours.
3. **On-disk spool of at least 7 days with chronological, throttled, idempotent backfill** — gapless;
   needs disk space and a careful resend protocol.

## Decision

Option 3: the agent keeps collecting while the backend is unreachable and spools at least 7 days on disk.
When the backend is reachable again it sends current data first, then backfills the spool chronologically
and throttled. Every batch carries an identity and sequence number so a resend is idempotent and the
backend can detect gaps.

## Consequences

- Gapless history across backend host downtime, without loading the 2 GB server or the link during backfill.
- The spool is a file-write area (security area) and needs a size bound and handling when full.
- The wire format needs batch identity and sequence numbers from v0.1.0 on; changing it later is a
  compatibility concern.
