# 0116: New listeners across an agent start are compared by the storage layer when the snapshots are stored

- **Status:** Accepted
- **Date:** 2026-10-18
- **Area:** Listener changes
- **Source:** Issue #278, Issue #305 (changed socket UIDs)
- **Supersedes:** —

## Context

The connection collector compares each run's listener inventory with the previous run's, held in memory
([0143](0143-new-listener-check-against-the-last-inventory-bounded-privileged-ports-first.md)), so the first run after an agent start
is a baseline and a listener that opened while the agent was stopped is never reported. The backend stores every snapshot, and the
agent marks its first snapshot after a start and what each snapshot's listener list holds
([0148](0148-agent-marks-its-first-snapshot-after-a-start-and-what-its-listener-list-holds.md)), so the backend can compare a start's
snapshot with the agent's last one before it. Nothing in the backend showed new listeners at all; the Product Manager chose a section
of the forensics report for them ([0149](0149-new-listeners-section-capped-per-finding-kind-changed-endpoints-without-command.md)).
The report already reads every log line and process snapshot and can pass the page's deadline on a slow host (0108). Backfill sends
the current data first, so the snapshots of a start and of its predecessor can arrive in either order and in different batches.

## Options considered

1. **The agent keeps its last inventory on disk** and compares across a start itself — exact and no backend work, but a new
   file-write area of the agent (security area 9) before the spool existed, state that a reinstall loses, and a reboot that loses the
   file still reports nothing. Rejected.
2. **The report compares at run time**, decoding the snapshot pair around each start — no stored derived data, but a crash-looping
   agent makes thousands of starts, each pair up to 2 MiB of JSON, and the report cannot know which pairs hold a finding without
   decoding them; capping the pairs examined would hide older findings behind restarts without a change. Rejected.
3. **The storage layer compares when it writes** (chosen) — each usable agent snapshot gets a narrow derived row, each start a
   comparison row computed from its predecessor when either is written and recomputed when a snapshot is stored between them later,
   so the result does not depend on the order of arrival; the report reads only these rows. Costs two tables, one index lookup per
   stored snapshot and, per batch, at most two payload decodes for each start the batch affects. Template statistics are kept the same
   way (0095).
4. **The pair picked by capture time** — what the reader sees, but the clock can step after a reboot (0046). Rejected for the agent's
   `seq`, which increases across restarts; capture time is shown only.
5. **Recomputing every stored comparison in the schema step that added changed socket UIDs (#305)** — the outcome of old starts
   would show changes at once, but the step would decode two payloads per stored start inside the migration. Rejected: a start
   compared before the step keeps its outcome until a snapshot stored below it makes it compared again.

## Decision

The storage layer compares each start's snapshot with the agent's previous one by `seq` when either is stored, with the agent's own
rules, the changed socket UIDs included, and keeps the outcome as derived rows that the forensics report reads. The rules are in
[Listener changes](../areas/listener-changes.md), the write path in [Storage writing](../areas/storage-writing.md) and the tables in
[Storage](../areas/storage.md) (*Schema*).

## Consequences

- Derived rows exist besides the records: retention must delete them with their records, and a change of the comparison rules needs a
  rebuild of the comparison rows (not built; the rules are the agent's and change with them).
- Agent snapshots stored before the schema step that adds the tables get no rows; no product path stored agent records before the
  ingest, which came later.
- A batch decodes at most two stored snapshots per start it affects, inside the write transaction; a stored snapshot that cannot be
  decoded makes that start not comparable instead of failing the batch, so a resend is never refused for it.
- The derived rows are written only after the record insert returned an ID; only comparison rows are ever updated, never a record.
- Residuals: a listener that opened and closed while the agent was stopped is never seen; a start whose first snapshot is lost (a gap,
  a record the scheduler dropped) is not seen as a start, and its successor was compared by the agent with that lost inventory;
  snapshots of a 1.0 agent and imported ones take no part.
