# Spec: Recognize backfilled data from the data itself

Source: Issue #41
Status: Approved by Lead

## Problem

The backend stores every agent record with its receive time but does not tell live from backfilled data: the header's `mode`
is only logged (`IngestEndpoint`, `ingest batch stored`), no sequence or time gap is detected, and no clock offset is measured
(the agent sends none: [Agent](../../docs/areas/agent.md), *Spool and sending*, *What is kept*). The rule engine (#56, #61) and
the gap views (#54) need, per record, whether it may alert, and per agent, which data is missing and whether it can still
arrive. Without that, a backend host that was off from 22:00 to 11:00 would make 13 hours of old data look current
(guarantee *Backfilled data never raises an alert by itself*, record 0022) and silent losses would look like calm periods
(record 0028).

## Scenarios

Terms: the **lateness** of an agent record is its batch's receive time minus its capture time corrected by the agent's
**measured offset** (agent clock minus backend clock). The **allowance** is 10 minutes for `log_line` and `kernel_event`
records, whose capture time comes from a log, and 90 seconds for every other kind. A **sequence gap** is a run of an agent's
numbers the backend has not stored; a **time gap** is a span of more than 2 minutes, in the agent's own capture times, in
which the backend holds no `metric` record of the agent. A gap is **open** (its data may still arrive), **closed** (all of it
arrived) or **final** (it will not arrive). A **counted** `self` sample is one whose `agent_send_lag_seconds` is at most
60 seconds (an acknowledgement within one `system` interval plus the send period) and which, when stored, is the agent's highest stored number with no open sequence gap below it. A counted sample whose
bound (capture minus receive time) lies more than 90 seconds below the offset in effect **measures** only when the agent's
previous counted sample, captured and received 1 to 5 minutes earlier, lies within 90 seconds of it; every other counted
sample measures at once.

- S1: Given an agent that sends live without backlog, when a batch arrives whose records' lateness lies within the allowance
  (late or early) and no open sequence gap of the agent lies below their numbers, then every record is stored as `live`.
- S2: Given the backend host is off from 22:00 to 11:00 while the agent collects, when it starts at 11:00 and the agent's
  current batch arrives first, then a sequence gap opens for the numbers between the last stored one and the batch, a time
  gap for 22:00 to 11:00, and the current records are stored `backfilled` because an open gap lies below them; as the
  backfill arrives oldest first, its records (`self` samples included) are stored `backfilled`, never move the offset, and
  the gaps shrink; when the last missing number arrives both gaps are `closed`, the records of that batch whose lateness lies
  within the allowance (the last seconds before the start) are `live`, and current records from the next batch on are `live`.
- S3: Given a backfill in progress, when the agent restarts in the same boot and resends records whose acknowledgement it
  lost, then its numbers continue without a jump, each record is stored once with the classification it got first, and the
  gap stays open until its numbers arrive, as in S2.
- S4: Given the agent's spool dropped its oldest data, when its `gap` record with cause `spool_dropped` (or a given-up record's
  `unknown` gap with `first_seq` and `last_seq`) arrives, then the numbers it names below its own number are `final` at once
  with the agent's cause, so they never hold back the classification of later records.
- S5: Given the server rebooted after an unclean stop, so the agent's numbers jump under a new boot ID, when the batch after
  the jump arrives, then a sequence gap with cause `sequence_missing` opens for the jump; once no record has arrived into any
  open sequence gap of that agent for 5 minutes (counted from the last such record, the opening of a gap or the first batch
  after a backend start, whichever is latest), the next batch stored from the agent makes all its open sequence gaps `final`
  (last changed: that batch's commit), and its records and later ones are `live` again.
- S6: Given the agent was stopped cleanly for an hour in the same boot, so its numbers continue without a jump, when the next
  batch arrives, then a time gap with cause `no_data` is stored `final` for that hour and no sequence gap opens.
- S7: Given the server's clock is 2 hours behind (or ahead of) the backend host's, when the agent sends live, then from the
  first measuring `self` sample on (behind: the second counted one) the measured offset is about −2 h (+2 h) and current records are `live`, while records
  captured more than the allowance before their receipt stay `backfilled` throughout.
- S8: Given batches whose header `mode` contradicts the data, when they arrive, then the classification follows lateness and
  gaps alone: fresh records in a `backfill` batch are `live`, old records in a `live` batch `backfilled`.
- S9: Given stored data, when the rule engine, a view or a test reads through the store, then every record carries its
  classification, and the agent's gaps (kind, cause, state, number range, time span, when detected and last changed) and its
  measured offset can be read.

## Errors and edge cases

- E1: The first batch the backend ever stores from an agent: numbers below its lowest number are never missing; older records
  the backfill brings later are stored by lateness and open no sequence gap, but time gaps among their metrics are detected.
- E2: A record arrives into the middle of an open gap: the gap keeps the lower missing part and a new gap of the same kind,
  cause and state takes the upper one; a gap that loses all its missing data is `closed`.
- E3: A record arrives into a `final` gap (a paused backfill resumed after the 5-minute wait): it is stored `backfilled` and
  the gap shrinks or splits like an open one; a final gap whose data all arrived is `closed`.
- E4: A record whose corrected capture time lies more than the allowance after its receipt (a clock stepped forward since the
  last measurement) is `backfilled`.
- E5: Accepted consequences of the allowance: log lines of a flood that the agent reads more than 10 minutes after they were
  written (a run reads at most 1,000 records or one second, every 2 seconds) and a backup start seen more than 90 seconds
  after it began are `backfilled`.
- E6: The agent's clock is stepped back by more than the allowance: current records are `backfilled` until the offset is
  measured again, at most 10 minutes; it never makes an old record `live`.
- E7: No `self` sample has measured yet (a new agent, a sender that never caught up): the offset is the largest proof, among
  the run-timed records (not `log_line` or `kernel_event`) of earlier batches received in the last 5 minutes, that the agent
  clock is ahead, and 0 when there is none — a record never proves its own batch current; so an agent clock behind by more than the allowance yields only `backfilled` records, and an old record
  is never made `live` by a clock that is ahead.
- E8: A `self` sample with a small send lag that arrives late (the first sample of an agent started while the backend was off,
  whose lag counts from the start; a sample taken just before the backend went off and sent later by the backfill or into a
  hole the live stream left) is not counted and never moves the offset. When the agent's 32 holes are full its live stream
  sends the outage's records with consecutive numbers ([Agent](../../docs/areas/agent.md), *Sending*), so such a sample can
  be counted; its bound lies far below the offset and no earlier counted sample confirms it, so it does not measure.
- E9: The backend restarts with open gaps: their states survive, and the 5-minute wait of S5 counts again from the agent's
  first batch stored after the start.
- E10: A refused batch (`4xx`, `503`) or a rolled-back write changes no classification, gap or offset.
- E11: Two batches of one agent are written at once (live and backfill stream): the result equals writing them one after the
  other in commit order.
- E12: A hostile or broken agent skips numbers between every record, or sends a `gap` naming numbers at or above its own:
  a stored record adds at most three gap rows (two unless it is an agent `gap` record), numbers at or above the gap record's
  own number are not made `final`, numbers up to 2^63 − 1 (and gap ranges above it) and capture times anywhere in the
  storable range are handled without overflow or refusal, and the work per record does not grow with the number of open gaps.
- E13: Records of origin `import` or `backend` are stored `backfilled` and never take part in gaps or the offset.
- E14: Agent records stored before this version are `backfilled`, and numbers below the highest one stored before the
  upgrade are never reported missing.
- E15: Time gaps use the agent's raw capture times: a new offset measurement moves none, and a clock step of more than
  2 minutes (stated residual) shows as a time gap forward or hides one backward.
- E16: A time gap is stored even where the agent's own gaps (`collector_timeout`, a late start's `unknown`) cover the same span;
  readers see both, the agent's cause being the more specific.

## Non-functional requirements

- No new configuration: the allowances (10 min, 90 s), the time-gap span (2 min), the final wait (5 min), the send-lag bound
  (60 s) and the confirmation spacing (1 to 5 min) are tested constants (Detection area, *Live data and backfill*).
- No wire-format change; the header's `mode` is only logged, never stored, and its `clock_offset_ns` is stored as before;
  neither is used for the classification (record 0046 lets the backend prefer its own measurement).
- Schema: one new migration step; a record row is still never updated after its insert.
- Classification, gap upkeep and the offset update run in the batch's write transaction; the storage write-throughput
  criterion (Storage, *Write throughput*) still holds, and the backend keeps per agent only a fixed amount of memory for it.
- Fail-safe: when the data cannot settle a case, the record is `backfilled`, never `live`.
- Security: the classification is no trust boundary against an agent holding the token, which chooses its own capture
  times and numbers; it can add at most three gap rows per record it stores. Log lines carry the agent ID, counts, numbers,
  times and the offset, never record content.
- Logs: info `ingest gaps changed` (`agent_id`, `opened`, `closed`, `final`, `open`) once per batch that opened, closed or made final a gap (a batch that only
  shrinks or splits a gap writes none);
  warning `agent clock off` (`agent_id`, `offset_seconds`) when the measured offset first exceeds 90 seconds, info
  `agent clock in step` when it returns.

## Acceptance criteria

- [ ] AC1: An agent record is stored `live` exactly when its lateness lies within ± its kind's allowance and no open sequence
  gap of its agent lies below its number once its whole batch is applied (its own fills and agent `gap` records included),
  else `backfilled`, whatever the header's `mode` says (S1, S8, E4, E5).
- [ ] AC2: The 13-hour outage (22:00–11:00) of S2, replayed as the agent sends it with its `self` samples (the agent also
  started during the outage, E8), opens one sequence gap and one time gap, stores every record of the batches before the one
  that brings the last missing number `backfilled` (current records included), never stores a record captured during the
  outage `live` unless it comes with that closing batch within its allowance, leaves the offset unchanged until the gaps
  close, closes both gaps with the last missing number, and stores current records of the following batch `live`; replayed
  again with the agent's holes full, so that the outage's records arrive first with consecutive numbers and a late `self`
  sample is counted, it stores no record captured during the outage more than its allowance before its receipt `live` and
  leaves the offset unchanged (S2, E8).
- [ ] AC3: A same-boot agent restart during that backfill, with resent records, stores each record once with its first
  classification and keeps the outage gap open until filled; separately, a reboot with a numbering jump and a new boot ID
  makes the jump's gap `final` with the first batch stored 5 minutes after the wait's start of S5, and that batch's current records are `live` (S3, S5, E9).
- [ ] AC4: Numbers below its own number that an agent `gap` record names with `first_seq` and `last_seq` are `final` with that
  record's cause as soon as it is stored (S4, E12).
- [ ] AC5: A span of more than 2 minutes of raw capture time without an agent `metric` record is a time gap with cause
  `no_data`, also below the agent's first number and beside the agent's own gaps: `final` when no open sequence gap lies
  between its neighbouring records' numbers, `open` otherwise, and narrowed, split or `closed` by metrics that arrive inside
  it (S2, S6, E1, E15, E16).
- [ ] AC6: With the server's clock 2 hours behind and, separately, 2 hours ahead, records sent live are `live` from the first
  measuring `self` sample on (ahead: the first counted one; behind: the second, which confirms the first), no record captured more than its allowance (plus the delivery delay of the
  freshest counted sample, none in the test) before its receipt is ever `live` (before, during and
  after the measurement and after a clock step), and after a backward step of 5 minutes current records are `live` again
  within 10 minutes; this holds for an agent without log traffic, whose send lag is set by its `system` samples (S7, E4, E6,
  E7).
- [ ] AC7: Gap upkeep follows E1 to E3: no sequence gap below an agent's first stored number, a middle fill splits a gap, a
  final gap shrinks and closes like an open one, and states survive a backend restart (E1, E2, E3, E9).
- [ ] AC8: A refused batch changes nothing, two concurrent batches give the result of their commit order, a batch of metrics jumping
  in number and time between every record adds at most two gap rows per record, numbers near 2^63 − 1, gap ranges above it
  and capture times at both ends of the storable range are stored (`204`) and `backfilled`, and a fill into one of 20,000
  open gaps costs indexed lookups only (E10, E11, E12).
- [ ] AC9: Import and backend records and agent records stored before the upgrade read `backfilled`, and no gap is reported
  below the highest number stored before the upgrade (E13, E14).
- [ ] AC10: Every record read through the store carries its classification, the agent's gaps and measured offset can be read
  with the fields of S9, and the log lines of *Non-functional requirements* are written as stated (S9).

## Out of scope

- Showing gaps or classifications in the web UI, charts breaking at gaps (#54); alert rules and the after-backfill summary
  (#56, #61); silence of an agent that sends nothing at all (#58); recomputing rollups after a backfill (#46).
- Measuring the offset in the agent or sending `clock_offset_ns`; a clock offset per capture context for old records.
- Detecting gaps inside data stored before the upgrade; gaps as rows of the `records` table.

## Open questions

- What is "its interval"? Answered: one allowance per capture-time source. A record timed by the agent's run is delayed by its
  run's timeout (at most 10 s), the live stream's 4-second send period and the request, and the longest collector interval is
  60 s (Agent, *Scheduling*, *Sending*): 90 s. Log lines and kernel events carry the log's time and are read in budgeted runs
  (Agent, *Log shipping*, *Budgets of a run*), so a flood delays them by minutes: 10 minutes, with E5's residual.
- How does a gap that never fills (a jump after a fresh start or a crash, issue comment) stop holding back live data?
  Answered: the 5-minute final wait of S5; the backfill sends oldest first without skipping an unacknowledged record (Agent,
  *Sending*), so a pass that brings nothing into any open gap for 5 minutes has nothing left for them.
- How can the offset between server and backend host be measured, when the agent sends none? Answered: from counted `self`
  samples, whose send lag the agent measures on its own clock; one-way timing alone cannot tell a clock that is behind from a
  delay, and a sample counts only at the head of the agent's numbers with no open gap below (E8), because the lag is measured
  at capture and counts from the agent's start before its first acknowledgement (Agent, *Sending*, *Send lag*). The plan fixes
  the estimator under AC2 and AC6 (for example the tightest bound over the counted samples of a window).
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
  constants, the gap kinds and states, the offset measurement and its fail-safe, the residuals E5 and E15; no longer "Planned".
- [Storage](../../docs/areas/storage.md): the migration step, the classification column, the gap objects and the offset, the
  readers.
- [Backend host](../../docs/areas/backend-host.md), *Ingest endpoint*: the new log lines.
- [Wire format](../../docs/areas/wire-format.md), consumer duties and *Batch identity*: how the backend detects gaps and that
  `mode` and `clock_offset_ns` are not used for the classification.
- [Agent](../../docs/areas/agent.md), *Gaps*: the backend's gaps, linked; `docs/ARCHITECTURE.md`, *Offline behavior and
  backfill*: the measured offset and the gap states.

## Challenge

- M1 (late `self` sample sets a wrong offset): accepted. A sample is counted only when it is the agent's highest stored number
  with no open sequence gap below it (Terms, S2, E8); AC2 replays the `self` samples, includes an agent started during the
  outage and requires that no outage record is ever `live` and the offset stays unchanged during the backfill. The estimator
  (e.g. the tightest bound) stays the plan's choice under AC2 and AC6.
- M2 (`log_line` capture times and floods): accepted. Records whose capture time comes from a log (`log_line`,
  `kernel_event`) get an allowance of 10 minutes; a flood read later than that and a backup start seen late are stated
  consequences (E5), which also go into the Detection area; the Answered text is corrected.
- Minor 1 (gap naming numbers at or above its own): accepted; S4, E12 and AC4 make only numbers below the record's own number
  `final`.
- Minor 2 (time gaps below the first number): accepted; E1 limits itself to sequence gaps, AC5 detects time gaps there.
- Minor 3 (`no_data` versus `agent_not_running`): rejected for S6. `agent_not_running` is a cause the agent reports; the backend
  cannot tell from the data whether the agent was stopped or ran without recording a metric, so its own cause `no_data` (wire
  format, *`gap`*) is the honest one; #54 shows it as "no data".
- Minor 4 (when `final` takes effect): accepted; S5 and AC3 say the next batch stored from the agent applies it and "last
  changed" is that batch's commit. While the agent sends nothing, no record waits for that classification.
- Minor 5 (raw or corrected times for time gaps): accepted; raw capture times, with the clock-step residual (E15, AC5).
- Minor 6 (overlap with the agent's gaps): accepted; the time gap is still stored and readers see both (E16, AC5).
- Minor 7 (origin `backend`): accepted; E13 and AC9 include it.
- Minor 8 (AC3 test data): accepted; AC3 separates a same-boot restart without a jump from a reboot with a jump and a new
  boot ID (S3, S5).
