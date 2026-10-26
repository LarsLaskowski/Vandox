# Spec: Recognize backfilled data from the data itself

Source: Issue #41
Status: Draft

## Problem

The backend stores every agent record with its receive time but does not tell live from backfilled data: the header's `mode`
is only logged (`IngestEndpoint`, `ingest batch stored`), no sequence or time gap is detected, and no clock offset is measured
(the agent sends none: [Agent](../../docs/areas/agent.md), *Spool and sending*, *What is kept*). The rule engine (#56, #61) and
the gap views (#54) need, per record, whether it may alert, and per agent, which data is missing and whether it can still
arrive. Without that, a backend host that was off from 22:00 to 09:00 would make 11 hours of old data look current
(guarantee *Backfilled data never raises an alert by itself*, record 0022) and silent losses would look like calm periods
(record 0028).

## Scenarios

Terms: the **lateness** of an agent record is its batch's receive time minus its capture time corrected by the agent's
**measured offset** (agent clock minus backend clock). The **allowance** is 90 seconds. A **sequence gap** is a run of an
agent's numbers the backend has not stored; a **time gap** is a span of more than 2 minutes in which the backend holds no
`metric` record of the agent. A gap is **open** (its data may still arrive), **closed** (all of it arrived) or **final** (it
will not arrive).

- S1: Given an agent that sends live without backlog, when a batch arrives whose records have a lateness within the
  allowance (at most 90 seconds late or early) and no open sequence gap of the agent lies below their numbers, then every
  record is stored as `live`.
- S2: Given the backend host is off from 22:00 to 09:00 while the agent collects, when it starts at 09:00 and the agent's
  current batch arrives first, then a sequence gap opens for the numbers between the last stored one and the batch, a time
  gap for 22:00 to 09:00, and the current records are stored `backfilled` because an open gap lies below them; as the
  backfill arrives oldest first, its records are stored `backfilled` and the gaps shrink; when the last missing number
  arrives both gaps are `closed`, and current records from the next batch on are `live`.
- S3: Given a backfill in progress, when the agent restarts and resends records whose acknowledgement it lost, then each is
  stored once with the classification it got first, the gap stays open until its numbers arrive, and S2's closing applies.
- S4: Given the agent's spool dropped its oldest data, when its `gap` record with cause `spool_dropped` (or a given-up record's
  `unknown` gap with `first_seq` and `last_seq`) arrives, then the numbers it names are `final` at once with the agent's
  cause, so they never hold back the classification of later records.
- S5: Given an agent that starts after an unclean stop in an earlier boot, so its numbers jump, when the batch after the jump
  arrives, then a sequence gap with cause `sequence_missing` opens for the jump; when no record has arrived into any open
  sequence gap of that agent for 5 minutes, all its open sequence gaps become `final`, and records from then on are `live`
  again.
- S6: Given the agent was stopped cleanly for an hour in the same boot, so its numbers continue without a jump, when the next
  batch arrives, then a time gap with cause `no_data` is stored `final` for that hour and no sequence gap opens.
- S7: Given the server's clock is 2 hours behind (or ahead of) the backend host's, when the agent sends live, then once the
  backend has stored a `self` sample whose `agent_send_lag_seconds` is at most 10 seconds the measured offset is about −2 h
  (+2 h) and current records are `live`, while records captured more than 90 seconds before their receipt stay
  `backfilled` throughout.
- S8: Given batches whose header `mode` contradicts the data, when they arrive, then the classification follows lateness and
  gaps alone: fresh records in a `backfill` batch are `live`, old records in a `live` batch `backfilled`.
- S9: Given stored data, when the rule engine, a view or a test reads through the store, then every record carries its
  classification, and the agent's gaps (kind, cause, state, number range, time span, when detected and last changed) and its
  measured offset can be read.

## Errors and edge cases

- E1: The first batch the backend ever stores from an agent: numbers below its lowest number are never missing; older records
  the backfill brings later are stored by lateness, open no gap and are never held back by one.
- E2: A record arrives into the middle of an open gap: the gap keeps the lower missing run and a new gap of the same kind,
  cause and state takes the upper one; a gap that loses all its missing data is `closed`.
- E3: A record arrives into a `final` gap (a paused backfill resumed after the 5-minute wait): it is stored `backfilled` and
  the gap shrinks or splits like an open one; a final gap whose data all arrived is `closed`.
- E4: A record whose corrected capture time lies more than 90 seconds after its receipt (a clock stepped forward since the
  last measurement) is `backfilled`.
- E5: A record whose capture time is an earlier event, not the run that read it (a boot's `booted_at` when the agent starts
  later, a backup's start seen later), is `backfilled` when it is older than the allowance.
- E6: The agent's clock is stepped back by more than the allowance: current records are `backfilled` until the offset is
  measured again, at most 10 minutes; it never makes an old record `live`.
- E7: No `self` sample with a send lag of at most 10 seconds has arrived (a new agent, a sender that never caught up): the
  offset is unknown and taken as 0, so an agent clock off by more than the allowance yields only `backfilled` records.
- E8: The backend restarts with open gaps: their states survive, and the 5-minute wait of S5 counts again from the start.
- E9: A refused batch (`4xx`, `503`) or a rolled-back write changes no classification, gap or offset.
- E10: Two batches of one agent are written at once (live and backfill stream): the result equals writing them one after the
  other in commit order.
- E11: A hostile or broken agent skips numbers between every record: at most one gap opens per stored record; numbers up to
  2^63 − 1 are handled without overflow.
- E12: Records of origin `import` are stored `backfilled` and never take part in gaps or the offset.
- E13: Agent records stored before this version are `backfilled`, and numbers below the highest one stored before the
  upgrade are never reported missing.

## Non-functional requirements

- No new configuration: the allowance (90 s), the time-gap span (2 min), the final wait (5 min) and the send-lag bound
  (10 s) are tested constants (Detection area, *Live data and backfill*).
- No wire-format change; the header's `mode` and `clock_offset_ns` are stored as before and not used for the classification
  (record 0046 lets the backend prefer its own measurement).
- Schema: one new migration step; a record row is still never updated after its insert.
- Classification, gap upkeep and the offset update run in the batch's write transaction; the storage write-throughput
  criterion (Storage, *Write throughput*) still holds, and the backend keeps per agent only a fixed amount of memory for it.
- Fail-safe: when the data cannot settle a case, the record is `backfilled`, never `live`.
- Security: the classification is no trust boundary against an agent holding the token, which chooses its own capture
  times and numbers; it can open at most one gap per record it stores. Log lines carry the agent ID, counts, numbers,
  times and the offset, never record content.
- Logs: info `ingest gaps changed` (`agent_id`, `opened`, `closed`, `final`, `open`) once per batch that changed a gap;
  warning `agent clock off` (`agent_id`, `offset_seconds`) when the measured offset first exceeds the allowance, info
  `agent clock in step` when it returns.

## Acceptance criteria

- [ ] AC1: An agent record is stored `live` exactly when its lateness lies within ±90 seconds and no open sequence gap of
  its agent lies below its number, else `backfilled`, whatever the header's `mode` says (S1, S8, E4, E5).
- [ ] AC2: The 13-hour outage (22:00–09:00) of S2, replayed as the agent sends it, opens one sequence gap and one time gap,
  stores every record captured during the outage and every current record of the backfill period `backfilled`, closes both
  gaps with the last missing number, and stores current records of the following batch `live` (S2).
- [ ] AC3: An agent restart during that backfill, with resent records and one numbering jump, stores each record once with
  its first classification, keeps the outage gap open until filled, and makes the jump's gap `final` 5 minutes after the last
  record that arrived into an open sequence gap of the agent, after which current records are `live` (S3, S5, E8).
- [ ] AC4: Numbers named by an agent `gap` record with `first_seq` and `last_seq` are `final` with that record's cause as soon
  as it is stored, whether they were already missing or not yet reached (S4).
- [ ] AC5: A span of more than 2 minutes without an agent `metric` record is a time gap with cause `no_data`: `final` when no
  open sequence gap lies between its neighbouring records' numbers, `open` otherwise, and narrowed, split or `closed` by
  metrics that arrive inside it (S6, S2).
- [ ] AC6: With the server's clock 2 hours behind and, separately, 2 hours ahead, records sent live are `live` from the first
  stored `self` sample with a send lag of at most 10 seconds on, no record captured more than 90 seconds before its receipt
  is ever `live` (before, during and after the measurement and after a clock step), and after a backward step of 5 minutes
  current records are `live` again within 10 minutes (S7, E6, E7).
- [ ] AC7: Gap upkeep follows E1 to E3: no gap below an agent's first stored number, a middle fill splits a gap, a final gap
  shrinks and closes like an open one, and states survive a backend restart (E1, E2, E3, E8).
- [ ] AC8: A refused batch changes nothing, two concurrent batches give the result of their commit order, a batch with a jump
  between every record opens at most one gap per record, and numbers near 2^63 − 1 work (E9, E10, E11).
- [ ] AC9: Import records and agent records stored before the upgrade read `backfilled`, and no gap is reported below the
  highest number stored before the upgrade (E12, E13).
- [ ] AC10: Every record read through the store carries its classification, the agent's gaps and measured offset can be read
  with the fields of S9, and the log lines of *Non-functional requirements* are written as stated (S9).

## Out of scope

- Showing gaps or classifications in the web UI, charts breaking at gaps (#54); alert rules and the after-backfill summary
  (#56, #61); silence of an agent that sends nothing at all (#58); recomputing rollups after a backfill (#46).
- Measuring the offset in the agent or sending `clock_offset_ns`; a clock offset per capture context for old records.
- Detecting gaps inside data stored before the upgrade; gaps as rows of the `records` table.

## Open questions

- What is "its interval"? Answered: one allowance of 90 seconds for every record: a fresh record's delay is its run's timeout
  (at most 10 s), the live stream's 4-second send period and the request, and the longest collector interval is 60 s
  (Agent, *Scheduling*, *Sending*); 90 s covers that interval plus 30 s tolerance.
- How does a gap that never fills (a jump after a fresh start or a crash, issue comment) stop holding back live data?
  Answered: the 5-minute final wait of S5; the backfill sends oldest first without skipping an unacknowledged record (Agent,
  *Sending*), so a pass that brings nothing into any open gap for 5 minutes has nothing left for them.
- How can the offset between server and backend host be measured, when the agent sends none? Answered: from batches the agent
  sent while caught up, which its own `agent_send_lag_seconds` shows on its own clock; one-way timing alone cannot tell a
  clock that is behind from a delay. The plan fixes the estimator under AC6.
- What is a time gap? Answered: the agent area names "intervals with no batch" and the spool leaves a stopped agent's last
  gap to "the backend's time-gap detection" (Agent, *Gaps*, *Write failures*); `metric` records come at least every 60 s.
- Are imported records classified? Answered: as `backfilled`; imported data is historical and never alerts (Detection,
  *Live data and backfill*).
- Where are gaps stored, when records are never updated? Answered: as gap objects of their own with a state (Storage,
  *Writing*); the causes `sequence_missing` and `no_data` are the backend's (Wire format, *`gap`*).
- Do the issue's claims about today's behavior hold? Answered: the hint is only logged (confirmed, `IngestEndpoint`); #40 is
  merged (confirmed); the first number is large and a fresh start jumps (confirmed, Agent, *Sequence numbers*); "the measured
  agent clock offset" exists nowhere yet (refuted: the agent sends none).

## Area documents

- [Detection, alerts and reports](../../docs/areas/detection-and-reports.md), *Live data and backfill*: the rule, the
  constants, the gap kinds and states, the offset measurement and its fail-safe; no longer "Planned".
- [Storage](../../docs/areas/storage.md): the migration step, the classification column, the gap objects and the offset, the
  readers.
- [Backend host](../../docs/areas/backend-host.md), *Ingest endpoint*: the new log lines.
- [Wire format](../../docs/areas/wire-format.md), consumer duties and *Batch identity*: how the backend detects gaps and that
  `mode` and `clock_offset_ns` are not used for the classification.
- [Agent](../../docs/areas/agent.md), *Gaps*: the backend's gaps, linked; `docs/ARCHITECTURE.md`, *Offline behavior and
  backfill*: the measured offset and the gap states.
