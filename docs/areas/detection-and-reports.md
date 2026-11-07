# Detection, alerts and reports

## Scope

How the backend turns stored records into findings and which of them may ever raise an alert: detection is deterministic, every agent
record is classified as live or backfilled when it is stored (with the sequence and time gaps and the measured clock offset that
classification rests on), and the nightly report and the service inventory are held to fixed principles. The classification is
implemented (*Live data and backfill*); the alert rules and thresholds, the nightly report and the service inventory are **not
implemented yet** (they need the agent and the ingest API of v0.2.0), and this document fixes the principles they are held to. The
detectors and analyses that are implemented have documents of their own: [Log signatures](log-signatures.md), [Log
templates](log-templates.md), [Incidents](incidents.md), the [Forensics report](forensics-report.md) and its [Listener
changes](listener-changes.md). Operators and the later rule engine rely on it.

## Principles

- **Detection, incident reconstruction and alerting are deterministic**: rules, thresholds and log signatures. Every alert can be
  explained from rules and values and is covered by unit tests. A new failure pattern needs a new rule or signature.
- **AI is optional and only writes the nightly report.** It never decides whether an alert fires. Vandox works fully without any AI
  service.
- **Analysis comes before alerting.** The first release (v0.1.0) is the forensics release: log import, storage, outage incidents, the
  forensics report and the web views needed to reconstruct outages from imported logs. The agent with its spool and backfill follows in
  v0.2.0; alerting, the nightly report and remote actions build on them in later releases, with thresholds based on what the analysis
  showed. The data model must serve later rules without migration surprises.
- A recorded gap is *unknown*, never *normal*, in analysis, reports and the web UI.

## Live data and backfill

The backend classifies **every agent record as live or backfilled** when it stores it (record 0130, amending 0022). The decision is
final: a stored record is never updated, so a record that was backfilled never becomes live and can never alert later. No alert exists yet
that reads the classification; the rule engine (#56, #61) and the gap views (#54) are its readers, through the store ([Storage reading and log
exploration](storage-reading.md)). The header's `mode` and `clock_offset_ns` are not used; the agent sends no clock offset.

- **The rule.** A record is **live** exactly when its *lateness* lies within the allowance of its kind and no open sequence gap of its
  agent starts below its number; otherwise it is backfilled. Lateness is the batch's receive time minus the record's capture time
  corrected by the agent's measured offset (agent clock minus backend clock). The allowance is 10 minutes, early or late, for `log_line` and
  `kernel_event` (their time comes from a log, read in budgeted runs) and 90 seconds for every other kind. The open gaps are those that remain
  after the whole batch has been applied, so the batch that brings the last missing number is live by lateness alone. Fresh records in a batch
  marked `backfill` are live, old records in a batch marked `live` are not.
- **Alert rules are evaluated on live data only.** Backfilled data is stored and analyzed, appears in incidents and in the report, and
  never alerts, so there is no alert storm after the backend host was off. Imported data (origin `import`) and records of the backend are
  stored backfilled and take no part in gaps or the offset; so are the agent records stored before the upgrade to schema 12.
- **Fail-safe.** Every case the data cannot settle is backfilled: an open gap below, a capture time outside the allowance, an offset that is
  not yet measured where the clock is behind. The classification is no trust boundary: an agent that holds the token chooses its capture times
  and numbers.
- **Sequence gaps.** A *sequence gap* is a run of an agent's numbers the backend has not stored. A number above the highest stored one plus 1
  opens a gap (`sequence_missing`, state `open`) for the numbers between; numbers below the first number the backend received from the agent
  are never missing (the agent's first number is large, and an agent whose numbers jumped after a fresh start or an unclean stop leaves a gap
  that way). A record arriving into a gap removes its number: the gap shrinks, splits in two (the rest has the same cause and state) or is
  `closed` when nothing is left. A gap is **open** (its data may still arrive), **closed** (all of it arrived) or **final** (it will not
  arrive). A record that arrives into a final gap is stored backfilled and shrinks it like an open one. An agent `gap` record with `first_seq`
  and `last_seq` makes the numbers it names **below its own number** final at once, with its cause (`spool_dropped`, `unknown`, ...), so they
  never hold back later records; numbers at or above its own number are ignored.
- **The final wait.** When no record has arrived into any open sequence gap of an agent for **5 minutes** (counted from the last such record,
  the opening of a gap, or the first batch stored after the backend started, whichever is latest), the next batch stored from that agent makes
  all its open sequence gaps final, with that batch's commit as `changed_at`, and its records and later ones are live again. The agent's
  backfill sends oldest first and does not skip an unacknowledged record, so a pass that fills nothing for that long has nothing left for the
  gaps. After an unclean stop, current data stays backfilled for the backfill's duration plus 5 minutes. While the agent sends nothing,
  nothing waits for the classification.
- **Time gaps.** A *time gap* is a span of more than **2 minutes**, in the agent's raw capture times, in which the backend holds no `metric`
  record of the agent (cause `no_data`, the backend's own: the data cannot tell a stopped agent from one that ran without recording a metric).
  It is detected also below the first number and beside the agent's own gaps; readers see both, the agent's cause being the more specific.
  Metrics that arrive inside it narrow, split or close it. A time gap reads `final` when no open sequence gap lies between the numbers of its
  neighbouring samples and `open` while one does; the state is derived when it is read, so no batch re-evaluates the time gaps. Open time gaps
  never affect the classification. A clock step of more than 2 minutes shows as a time gap forward and hides one backward (residual, because
  raw times are used).
- **The offset.** The agent's clock offset is *measured* by the backend. A **bound** is a record's capture time minus its receive time, a
  lower bound of agent clock minus backend clock (the delay is never negative); only records timed by the agent's run give bounds, so a
  future-stamped `log_line` or `kernel_event` never raises it. A **counted sample** is a `self` metric `agent_send_lag_seconds` of at most
  **60 seconds** (one `system` interval plus the send period and timeout), which is the highest number stored from the agent and has no open
  sequence gap below it; so a sample sent late by a backfill or a restart, or taken while a gap is open, is never counted and the offset stays
  unchanged during a backfill. A counted sample **measures** when its bound is at most 90 seconds below the offset in effect (0 before the
  first measurement), so a raised bound measures at once; a bound further below measures only when the agent's **previous counted sample**,
  captured and received **30 seconds to 5 minutes** earlier, lies within 90 seconds of it (the confirmation spacing; its lower bound is half
  the 60-second self interval, so timer jitter and delivery delay never stop a pair from confirming). Every counted sample becomes the
  previous one, measuring or not; a sample that does not count never replaces it. So a late counted sample that a full-holes live stream
  brings as the head, whose bound is far below the offset and which no earlier sample confirms, never measures, and a clock that is behind,
  or stepped back, first measures with its **second** counted sample, about a minute after the first (a stepped-back clock's offset follows
  once the older bounds have left the window, below). From the first measuring sample on, the offset is the largest bound of the measuring
  samples in the 5 minutes before the newest of them; a measuring sample counts for the records of its own batch. Before the first one it is
  the largest bound of the run-timed records of **earlier batches** within the last 5 minutes before the newest of them, and 0 when that is
  below 0 or there is none (a batch's own bounds are added after its records are classified, so a record cannot prove itself current). The
  windows are kept as bounded rows of 10-second buckets, so an expired bound falls back to the true maximum of the window. Consequences: an
  agent clock that is behind by more than the allowance yields only backfilled records until its second counted sample; an old record is
  never made live by a clock that is ahead; after a clock step back by more than the allowance, current records are backfilled until the
  bounds from before the step have left the 5-minute window, about 6 minutes after the step (the second counted sample after the step
  measures, but the window's maximum keeps the larger bound until then), and a step forward makes fresh records backfilled until the next
  counted sample (before the first one, at most 5 minutes). The offset can be short by the delivery delay of the freshest counted sample,
  up to the agent's 30-second request timeout, so a record can be up to that much older than its allowance and still be live. The offset
  exceeding 90 seconds is logged ([Ingest endpoint](ingest-endpoint.md)).
- **Residuals.** Two late counted samples that agree (an agent started twice during an outage), 30 seconds to 5 minutes apart in capture
  and in receive time, confirm each other and can move the offset to a value that is too low for fresh records; they are backfilled until a
  later sample raises it. Log lines of a flood that the agent reads more than 10 minutes after they were written, and a backup start seen
  more than 90 seconds after it began, are backfilled. The time gaps use raw capture times. A stored record adds at most three gap rows and
  every lookup is an index search, so a hostile agent cannot make the work per record grow.

## Nightly report

Planned, not implemented yet.

- The report is to be sent at **06:00**. If the backend host was off at that time, it is sent as soon as the backfill has completed, so it
  is always based on complete data. The backend therefore needs to know when a backfill is complete.
- The backend sends the report through the Telegram bot ([Notification and remote actions](notification-and-remote-actions.md)); writing it
  may use the optional AI.

## Service inventory

Planned, not implemented yet.

The inventory of running services on the monitored server may suggest services that are not needed. A service is only ever **disabled
in a reversible, documented way** (for example `systemctl disable` or `mask`; packages stay installed). Agents of the hosting provider
are **never** disabled or changed, and the inventory excludes them from every action, including remote actions. Mail is monitored at the
level of the mail services (process state, ports, logs), never individual mail accounts, so no mailbox credentials exist anywhere.

## Related decisions

- [0008](../decisions/0008-deterministic-detection-and-alerting.md) — why detection is deterministic and AI only writes the report.
- [0020](../decisions/0020-analysis-before-alerting-forensics-release.md) — why the first release is the forensics release.
- [0022](../decisions/0022-backfill-detection-and-live-only-alerts.md) — why the backend classifies live and backfilled data itself.
- [0130](../decisions/0130-live-or-backfilled-is-decided-at-write-time.md) — why the classification is made at write time (amends 0022).
- [0180](../decisions/0180-sequence-and-time-gaps-are-stateful-rows-that-become-final-after-a-quiet-wait.md) — why gaps are stateful rows
  that become final after a quiet wait.
- [0181](../decisions/0181-the-agents-clock-offset-is-measured-from-caught-up-self-samples.md) — why the offset comes from caught-up self
  samples.
- [0024](../decisions/0024-nightly-report-timing.md) — why 06:00 or after the backfill.
- [0026](../decisions/0026-services-disabled-reversibly-only.md) — why services are only disabled reversibly.
- [0015](../decisions/0015-mail-services-checked-not-mail-accounts.md) — why mail services are checked, not mail accounts.

## Not here

- The known kinds of log events and their detector: [Log signatures](log-signatures.md).
- Log templates, their statistics and queries: [Log templates](log-templates.md).
- Outage incidents rebuilt from the stored data: [Incidents](incidents.md).
- The forensics report: [Forensics report](forensics-report.md); its new listeners across agent starts: [Listener changes](listener-changes.md).
- How the gap and offset rows are written in the batch's transaction: [Storage writing](storage-writing.md), *Agent timeline*; where records
  are stored: [Storage](storage.md).
- How the agent records its own gaps and numbers its records: [Agent](agent.md), *Gaps*; how batches are identified: [Wire format](wire-format.md).
- The log lines of the ingest endpoint about gaps and the clock: [Ingest endpoint](ingest-endpoint.md).
- Telegram delivery and who may receive messages: [Notification and remote actions](notification-and-remote-actions.md).
- Storing events: a later change.

## Implementation

The classification, the gaps and the offset are computed by the storage write path (`Vandox.Storage`, see the *Implementation* of
[Storage writing](storage-writing.md)). The alert rules, the nightly report and the service inventory have no code yet.
