# 0045: The agent spools at least 7 days and backfills; a batch is identified by the agent ID and its records' sequence numbers

- **Status:** Accepted
- **Date:** 2026-10-04
- **Area:** —
- **Source:** Issue #10
- **Supersedes:** —

## Context

The backend host usually runs 24/7 but is switched off at night (typically 22:00–09:00) a few times a year, and
it may be unavailable longer (maintenance, holidays). Outages often happen exactly when nobody is watching;
losing that data defeats the purpose of forensics. The agent therefore keeps collecting while the backend is
unreachable and must send every record exactly once in effect: a resend must be idempotent and the backend must
be able to detect gaps. That needs an identity for what is sent, and the wire format (0042, issue #10) has to
fix it now, because it is a compatibility concern from v0.1.0 on. The follow-up issues already assume a
per-record mechanism: #39 sends "batches with strictly increasing sequence numbers", #40 deduplicates "via
(agent ID, sequence number)", #41 detects "gaps in sequence numbers". A batch is not a stable unit: the agent
sends current data first and backfills in parallel (#39), and after an agent restart or a changed batch size
the same spooled records can be cut into batches differently. The same wording appears in the context of 0028
and in `.squad/project.md` (*Security areas* 1); both refer to this mechanism.

## Options considered

Spooling:

- **Drop data while the backend is unreachable** — simple, but gaps exactly when the data is needed.
- **Small in-memory buffer** — covers short blips; lost on an agent restart or after hours.
- **On-disk spool of at least 7 days with chronological, throttled, idempotent backfill** — gapless; needs disk
  space and a careful resend protocol. Chosen.

Identity:

1. **A batch ID and a batch sequence number in the header** — one value to
   deduplicate. Cons: a resend is only recognized if the agent cuts the spool into exactly the same batches
   again, which it cannot promise across a restart, a changed batch size or the live/backfill split;
   the backend can tell that a batch is missing but not which records; a gap inside a batch is invisible.
2. **A sequence number on every agent record; the batch is identified by the agent ID in its header and
   the sequence numbers of its records** — deduplication and gap detection work per record, independent of
   how records were batched. Cons: the backend checks one key per record instead of one per batch.
3. **Both** — two sources of truth that can disagree (same batch ID, different records), with no rule for
   which one wins.

## Decision

The agent keeps collecting while the backend is unreachable and spools at least 7 days on disk; when the backend is reachable again it sends current data first, then backfills
the spool chronologically and throttled.

Identity and sequence (option 2): every agent record carries `seq`, taken from one counter per agent that
increases strictly, survives agent restarts and reboots, and is never reused (#38 persists it). Within a
batch the sequence numbers are strictly increasing; gaps are allowed. The batch header carries the
`agent_id`. A batch has no ID of its own: it is identified by its `agent_id` and the sequence numbers of
its records (first to last). A resend is idempotent because the backend stores each (agent ID, sequence
number) once and never overwrites a stored record (#40); a gap is a range of missing sequence numbers
(#41). The format is described in `docs/WIRE_FORMAT.md`.

## Consequences

- Gapless history across backend host downtime, without loading the 2 GB server or the link during backfill.
- The spool is a file-write area (security area) and needs a size bound and handling when full.
- Batching is free for the agent (#39): it may cut, merge or re-cut spooled records into batches without
  affecting deduplication.
- The sequence counter is part of the agent's persistent state; losing it (reinstall, wiped spool) makes
  new records collide with stored ones. #38 and #40 must keep the counter persistent or give the agent a
  new ID; an optional counter epoch can be added to the header in a minor version (0043) if needed.
- Adding a batch ID later is an additive minor change; replacing per-record sequence numbers is a new major
  version.
