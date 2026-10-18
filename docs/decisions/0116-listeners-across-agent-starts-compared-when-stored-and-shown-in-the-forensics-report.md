# 0116: New listeners across an agent start: the agent marks its first snapshot and what its inventory holds, the backend compares when it stores the snapshots, the forensics report shows them with the agent's own findings

- **Status:** Proposed
- **Date:** 2026-10-18
- **Area:** Detection, alerts and reports
- **Source:** Issue #278
- **Supersedes:** —

## Context

The connection collector compares each run's listener inventory with the previous run's, held in memory (0115), so the first run after an
agent start is a baseline and a listener that opened while the agent was stopped is never reported. The backend stores every snapshot (once
the ingest exists, #40), but the stored data cannot tell which snapshot is the first after a start (`listener_check` `none` has three other
causes, and the boot ID changes only on a reboot), nor whether a list that was cut still holds every listener on ports 1 to 1023 (the
record's size budget and the inventory cap both cut it, and `aggregates_incomplete` has other causes too). Nothing in the backend showed new
listeners at all, not even the agent's. The Product Manager chose a section of the forensics report for both. The report already reads every
log line and process snapshot and can pass the page's deadline of 2 minutes on a slow host (0108), and a server with passive FTP produces an
agent finding about once a minute (0115). Wire format 1.1 is unreleased. The behavior is in [Detection, alerts and
reports](../areas/detection-and-reports.md) (*Forensics report*, *New listeners*), the fields in [Wire format](../areas/wire-format.md) and the
tables in [Storage](../areas/storage.md).

## Options considered

1. **The backend infers a start from the stored data** (`listener_check` `none` on a full snapshot, a new boot ID, the agent's CPU time
   falling) — no agent change, but `none` also follows a skipped line, a changed set of tables or a cut privileged part, a restart within one
   boot keeps the boot ID, and the self-metrics are another record kind. Rejected: it would compare snapshots the agent itself refused to.
2. **The gap cause `agent_not_running`**, already in the model and accepted from the agent, emitted at a start — no new field, but the agent
   would need the time it stopped, which only persistent state (the spool, #38) can give, the gap is another record that must be matched to
   the snapshot, and it says nothing about what an inventory holds. Rejected.
3. **The agent keeps its last inventory on disk** and compares across a start itself — exact and no backend work, but a new file-write area
   of the agent (security area 9) before the spool exists, state that a reinstall loses, and a reboot that loses the file still reports
   nothing. Rejected.
4. **Fields of `connection_snapshot` (chosen):** `listener_check` gains the value `start` for a run that had no previous inventory, and two
   fields say what the record's list holds: `inventory` (`all`, `privileged`, `cut`, `skipped`) and `tables` (the tables read). They are
   local to the record the comparison reads, cost a few bytes, and 1.1 is unreleased, so they extend it in place.
5. **The report compares at run time**, decoding the snapshot pair around each start — no stored derived data, but a crash-looping agent
   makes thousands of starts, each pair up to 2 MiB of JSON, and the report cannot know which pairs hold a finding without decoding them;
   capping the pairs examined would hide older findings behind restarts without a change. Rejected.
6. **The storage layer compares when it writes (chosen):** each usable agent snapshot gets a narrow row (`connection_marks`), each start
   a comparison row (`listener_comparisons`) computed from its predecessor by `seq` when either is written, recomputed when a snapshot is
   stored between them later (backfill sends current data first), so the result does not depend on the order of arrival; the report reads
   only these rows. Costs two tables, one index lookup per stored snapshot and, per batch, at most two payload decodes for each start the batch affects (computed once before the commit, so a backfill below a stored start does not decode it per record).
   Template statistics are kept the same way (0095).
7. **One cap for all findings** (100, then the latest) — simpler, but agent findings of passive FTP would push the restart findings out
   within hours (spec challenge). Rejected for separate caps per kind; the caps are 20 each, because a finding can hold 64 commands of up to
   1,024 bytes and the page keeps the report's document per circuit: about 5.5 MB of stored text in the run and, with the runs every
   external text carries on the page, up to about 16 MB per circuit. Lower caps (7 to 10 per kind, to stay near 5.5 MB on the page) were
   weighed after review and rejected: they shorten the forensic window of restart findings, the main value of the section, to defend a
   worst case that needs 2,560 commands of 1,024 bytes; the bound stays fixed, only a signed-in client opens a circuit, and the
   incidents page already holds about 10 MB per circuit.
8. **The pair picked by capture time** — what the reader sees, but the clock can step after a reboot (0046). Rejected for the agent's `seq`,
   which increases across restarts; capture time is shown only.

## Decision

Options 4, 6 and 8: the agent marks its first snapshot after a start with `listener_check` `start` and states in every snapshot what its
listener list holds and which tables it read; the storage layer compares each start's snapshot with the agent's previous one by `seq` when
either is stored, with the agent's own rules, and keeps the outcome; the forensics report lists these findings and the agent's own under
separate caps (option 7) in a section *New listeners*.

## Consequences

- The agent's first snapshot after a start no longer reads `none` but `start`; readers that treated `none` as "not compared" treat `start`
  the same way.
- Derived rows exist besides the records: retention (#46) must delete them with their records, and a change of the comparison rules needs a
  rebuild of `listener_comparisons` (not built; the rules are the agent's and change with them).
- Agent snapshots stored before the schema step that adds the tables get no rows; no product path stores agent records before the ingest
  (#40), which comes later.
- A batch decodes at most two stored snapshots per start it affects, inside the write transaction; a stored snapshot that cannot be
  decoded makes that start `not comparable` instead of failing the batch, so a resend is never refused for it.
- The stored copy of an agent's `new_listeners` keeps the first 64 entries and counts the rest, whatever the wire allows (4,096).
- The derived rows are written only after the record insert returned an ID; only `listener_comparisons` rows are ever updated, never a
  record.
- Findings are "latest" by capture time (then agent ID and `seq`) for the caps, so a clock that steps back may keep an older finding instead of a newer one; the
  order shown within an agent is by `seq`.
- Residuals: a listener that opened and closed while the agent was stopped is never seen; a start whose first snapshot is lost (a gap, a
  record the scheduler dropped) is not seen as a start, and its successor was compared by the agent with that lost inventory; snapshots of
  a 1.0 agent and imported ones take no part.
