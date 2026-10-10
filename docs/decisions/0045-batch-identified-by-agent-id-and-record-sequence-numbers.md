# 0045: The agent spools at least 7 days and backfills; a batch is identified by the agent ID and its records' sequence numbers

- **Status:** Accepted
- **Date:** 2026-10-04
- **Area:** Wire format
- **Source:** Issue #10
- **Supersedes:** —

## Context

The backend host usually runs 24/7 but is switched off at night a few times a year and may be unavailable longer. Outages often happen when
nobody is watching, so losing that data defeats forensics. The agent therefore keeps collecting while the backend is unreachable and must send
every record exactly once in effect: a resend must be idempotent and the backend must detect gaps. The identity of what is sent has to be fixed
now, because the wire format is a compatibility concern from v0.1.0 on. A batch is not a stable unit: the agent sends current data first and
backfills in parallel, and after a restart or a changed batch size the same spooled records can be cut into batches differently (0028 and
`.squad/project.md`, *Security areas* 1, refer to this mechanism).

## Options considered

Spooling:

- **Drop data while the backend is unreachable** — gaps exactly when the data is needed.
- **Small in-memory buffer** — covers short blips; lost on a restart or after hours.
- **On-disk spool of at least 7 days with chronological, throttled, idempotent backfill** (chosen) — gapless; needs disk space and a careful resend protocol.

Identity:

1. **A batch ID and batch sequence number in the header** — a resend is recognized only if the agent cuts the spool into exactly the same batches, which it cannot promise; a gap inside a batch is invisible.
2. **A sequence number on every agent record; the batch is identified by the agent ID and the sequence numbers of its records** (chosen) — deduplication and gap detection per record, independent of batching.
3. **Both** — two sources of truth that can disagree.

## Decision

The agent spools at least 7 days and backfills current data first, then the spool chronologically and throttled. Identity is option 2. The
format rules (counter, header, deduplication key) are in the [Wire format](../areas/wire-format.md) area (*Batch identity*) and in [Storage](../areas/storage.md) (*Writing*).

## Consequences

- Gapless history across backend host downtime, without loading the 2 GB server or the link during backfill.
- The spool is a file-write area (security area) and needs a size bound and handling when full.
- Batching is free for the agent: it may cut, merge or re-cut spooled records without affecting deduplication.
- The sequence counter is part of the agent's persistent state; losing it makes new records collide with stored ones, so the counter must stay persistent or the agent gets a new ID. An optional counter epoch can be added to the header in a minor version (0043).
- Adding a batch ID later is an additive minor change; replacing per-record sequence numbers is a new major version.
