# Retention and rollups

## Scope

How `vandoxd` keeps the database bounded: the periods after which each kind of data is removed, what outlives them (imported data,
events, the windows of the agent's OOM kills), the 5-minute and hourly rollups that remain of the raw metric values, the background job
that folds and removes in bounded steps, and what it logs. The operator who sets the periods and the readers of the rollups rely on it.
The options are in [Configuration options](configuration-options.md), the tables in [Storage](storage.md), the resend rule that removal
needs in [Storage writing](storage-writing.md).

## Periods

- Seven options set how long data is kept, each `1d` to `9999d` or `unlimited`: `retention.raw` (30d), `retention.rollups_5m` (365d),
  `retention.rollups_1h` (1095d), `retention.logs` (90d), `retention.snapshots` (30d), `retention.events` (1095d) and
  `retention.incidents` (unlimited). A record is past its period when its capture time is earlier than the backend's current UTC time
  minus the period; one second before stays, one second after goes. A record whose capture time lies in the future is kept until its
  capture time is past the period. With `unlimited` that data is kept forever.
- Which period a record follows: `metric` values `raw`; `log_line` `logs`; `process_snapshot`, `connection_snapshot` and
  `mariadb_status` `snapshots`; `kernel_event`, `service_state`, `backup_run` `events`. The 5-minute and hourly rollups follow
  `rollups_5m` and `rollups_1h`; the template statistics count as hourly rollups ([Log templates](log-templates.md)); incidents follow
  `incidents`.
- Only records of origin `agent` and `backend` are removed. **Records of origin `import` are never removed**, whatever their age
  ([Log import](log-import.md)).
- A record of the agent already past its period when it arrives (a long backfill) is stored, folded into its buckets and removed by a
  later pass.
- An incident is removed only when its window ended before `retention.incidents` and it has no note; an incident with a note is kept,
  and an incident number is never given again.
- A connection snapshot that is the newest mark of its agent, or that carries a listener finding (new or changed listeners, or a
  comparison with a finding), follows `retention.events` instead of `snapshots` ([Listener changes](listener-changes.md)). The rows of
  `connection_marks` and `listener_comparisons` go with their record.
- The record with the highest ID is never removed, so record IDs keep increasing. A record above the position of the kill scan is not
  removed yet, and neither is a metric value above the rollup watermark (both below).
- `agent_streams` and `agent_clock_samples` are never removed.

## Events and gaps

- The kinds of `retention.events` are kept for it (1,095 days by default): OOM kills, boots, service states, backup runs, listener
  findings.
- A gap record and a gap row (`gaps`) are kept until the **gap horizon**: the current time minus the longest of `events`, `raw`, `logs`,
  `snapshots`, `rollups_5m` and `rollups_1h`; there is none while one of them is `unlimited`. A gap row is removed only when its span
  ends before the horizon and lies inside no kept window; a sequence gap that is `open` or `final` is never removed, a time gap only
  when its state reads `closed` or `final`. So a span that still has data or rollups keeps its gaps, and no removed number reads as
  missing.
- A removed record never reads as a gap and is never reported as a new start: the stream row of the agent stays.
- The sample times of an agent (`agent_sample_times`) below the raw horizon are removed, except the newest row of each agent, and the
  stored **sample-time floor** is raised to that horizon first, before the first delete of the step (never lowered). A sample time below the floor only narrows, splits
  or closes the time gap that contains it; it adds no row and opens no time gap ([Storage writing](storage-writing.md), *Time gaps*).
  With `retention.raw` `unlimited` nothing is removed and no floor is stored.

## Windows of OOM kills

- Before it removes anything, the job reads the `kernel_event` records of origin `agent` stored since its last scan. Each of type
  `oom_kill` becomes a **hold**: the window from `incidents.window_before` before the kill to `incidents.window_after` after it, each end widened by a margin of
  5 minutes (the lines an incident reads around its kill stay inside the hold, clamped to the storable range).
  Holds that overlap are merged into one. The scan runs in pages of 1,000 records on a reader connection and stores the holds of a
  page with its progress in one write; a record stored after a scan, whatever its capture time, is caught by the next one.
- No record captured inside a hold is removed while the hold's end (margin included) is within `retention.incidents`, so an incident of the kill can
  still be built in full by `vandoxd incidents`. A hold is removed with `retention.incidents` like an incident, except that a stored
  incident with a note that overlaps it keeps it.
- Only the agent's `kernel_event` kills make a hold: the kernel writes them, and a journal line that merely reads as an OOM kill
  (any local process can write one) keeps nothing. The residual is a holder of the agent token, who can forge a kill and so pin the
  data of its window; the free-space floor `ingest.min_free` bounds the growth.
- A stored incident's own window keeps nothing. The window data of an incident without a `kernel_event` kill is removed by the periods
  while the incident stays; how a later `vandoxd incidents` run treats such incidents is in [Incidents](incidents.md), *Re-running*.

## Rollups

- The job folds the `metric` rows with a record ID above a stored watermark into one 5-minute and one hourly bucket of their series
  (`source`, `name`, `labels`; no labels is the empty text), and advances the watermark in the same transaction. Every origin is
  folded. A value is folded exactly once.
- A bucket holds the minimum, maximum, sum and count of its values and the last value with its capture time (the latest capture
  time, ties by the higher record ID). The average a reader sees is the sum over the count, a floating-point sum in arrival order, so
  it equals a recomputation within rounding. A sum is clamped to the range of a double (plus or minus `double.MaxValue`), in the fold
  and in the merge into a stored bucket, so it never becomes infinite.
- A bucket is available at most 5 minutes after it ends. The newest bucket grows until its values are all folded.
- A late value (an agent backfill, an import) whose bucket exists is merged into it, whether the bucket's raw values are still stored
  or already removed: minimum, maximum, count and average become exact for all values seen, and the last value changes only if the
  late value is later. A bucket past its tier's period is not created.
- A raw value is never removed before it is folded: removal skips every metric above the watermark. A database of an earlier version
  folds all its stored values first, in steps, and removes after.
- Readers take buckets of one series in a range, oldest first, with `IRollupReader` ([Storage reading and log exploration](storage-reading.md)).

## The job

- The job runs only inside the service (`vandoxd` without a sub-command). `import`, `incidents`, `templates` and `report` remove
  nothing.
- It folds at start-up and then every 5 minutes. When none ran yet or an hour has passed since the last completed one, it then runs a
  removal pass: the kill scan, then removal steps until nothing is left. Removal therefore starts at start-up and runs at least
  hourly; once caught up, data goes within a day after its period ends.
- **Steps are bounded.** One fold step reads at most 10,000 metric values and stops after the row that takes the labels it has read
  past 2 MiB (the first row is always taken). One removal step removes at most 5,000 records of one kind, or as many rows of one
  derived table, and examines at most 20,000 index entries and 1,000 windows, in one transaction. A step is meant to stay within the work of one
  ingest batch of 10,000 records; the storage benchmark measures it (`docs/BENCHMARKS.md`, *Retention steps*).
- A removal pass walks the kinds in this order, each until it is empty: raw metric values, log lines, snapshots, events, gap rows,
  5-minute rollups, hourly rollups, template hours, incidents, holds. A cursor of kind, capture time and ID carries the pass from step
  to step, so a record that is kept is examined once per pass. Imported records are not in the index the walk uses, and the walk
  steps over a hold in one jump.
- For each record the step removes, in this order: its full-text entry (`log_fts`), its typed row, its connection mark, its listener
  comparison, then the record. A step that removed log lines ends with an incremental merge of at most 64 pages of the full-text
  index, so the entries of removed lines free their pages for reuse instead of growing the index (record 0212).
- Template hours and source hours before `now − retention.rollups_1h` are removed where no stored log line of that source lies in the
  hour; a template with no hour left is removed. Counts, first and last seen of the others stay.
- An ingest batch waits for at most one step: the job takes the writer's lock for one step at a time and the lock is granted in turn.
  The job holds at most one step's data in memory.
- **Busy.** If another process holds the database lock (`vandoxd import`, a backup) past the busy timeout, the step fails with a
  warning, nothing is half-removed, and the next 5-minute tick continues.
- **Failure.** A step that fails (an I/O error, a full disk) rolls back completely and logs an error; the next tick retries it. The
  job never ends because of it.
- **Shutdown.** The job checks for the stop between steps. A step that has begun commits or rolls back; shutdown waits for at most
  that one step, and the store is closed after it.
- Retention frees space **inside** `vandox.db`; the file does not shrink and nothing runs `VACUUM`. New data reuses the freed pages, so
  the file stops growing once the periods are full. Ingest stopped at the free-space floor `ingest.min_free` resumes only when space
  on the volume is freed ([Ingest endpoint](ingest-endpoint.md)); shrinking the file is a separate feature (#55).

## Log lines

The job writes these lines of the service log ([Backend host](backend-host.md), *Logging*), each with the attributes named; counts only, never stored text:

| Event | Level | `msg` and attributes | When |
| ----- | ----- | -------------------- | ---- |
| 69 | information | `retention rollup`: `Values`, `Buckets` | after a fold pass that folded at least one value |
| 70 | information | `retention removal records`: `MetricValues`, `LogLines`, `Snapshots`, `Events`, `Gaps` | after a removal pass that removed at least one of these |
| 73 | information | `retention removal derived`: `FiveMinuteValues`, `HourlyValues`, `TemplateHours`, `Incidents`, `Holds` | after a removal pass that removed at least one of these |
| 71 | warning | `retention step busy`: `Operation` | a step found the database busy |
| 72 | error | `retention step failed`: `Operation`, `Error` | a step failed |

The counts are the sums of the pass's steps. `{Operation}` is `fold`, `scan` or `remove`; `{Error}` is the store's error text (the
operation and SQLite's message). A pass that changed nothing writes nothing.

## Limits and errors

- An invalid period stops the start of `vandoxd`: `retention.<key>: must be a whole number of days from 1d to 9999d, such as 30d, or
  unlimited` ([Configuration options](configuration-options.md)).
- The records of a kept window and the imported records are not bounded by the periods; the free-space floor is the bound of last
  resort.
- A resend of a record whose stored copy was removed is acknowledged and not stored again ([Storage writing](storage-writing.md),
  *Deduplication*); its residual is stated there.
- A record is removed for good: there is no undo and no backup function.

## Related decisions

- [`decisions/0208-metric-rollups-are-folded-from-new-rows-by-record-id-never-recomputed-from-raw.md`](../decisions/0208-metric-rollups-are-folded-from-new-rows-by-record-id-never-recomputed-from-raw.md) — why a watermark folds each value once and raw values are removed only below it.
- [`decisions/0209-retention-removes-by-capture-time-per-kind-imports-kept-events-kept-three-years.md`](../decisions/0209-retention-removes-by-capture-time-per-kind-imports-kept-events-kept-three-years.md) — why the periods are per kind, imports are kept and events follow their own period.
- [`decisions/0210-data-in-the-windows-of-the-agents-kernel-oom-kills-is-kept-as-long-as-their-incident.md`](../decisions/0210-data-in-the-windows-of-the-agents-kernel-oom-kills-is-kept-as-long-as-their-incident.md) — why only the windows of the agent's kernel OOM kills are held.
- [`decisions/0211-a-resent-record-whose-stored-copy-was-removed-is-not-stored-again.md`](../decisions/0211-a-resent-record-whose-stored-copy-was-removed-is-not-stored-again.md) — why a resend after removal is skipped.
- [`decisions/0212-retention-frees-space-inside-the-database-file-not-on-the-volume.md`](../decisions/0212-retention-frees-space-inside-the-database-file-not-on-the-volume.md) — why the file does not shrink and each log step merges the full-text index.
- [`decisions/0213-agent-sample-times-follow-the-raw-period-below-a-floor-that-opens-no-time-gap.md`](../decisions/0213-agent-sample-times-follow-the-raw-period-below-a-floor-that-opens-no-time-gap.md) — why sample times are pruned below a floor that opens no time gap.

## Not here

- The tables and their migration step: [Storage](storage.md). How a batch is written and deduplicated: [Storage writing](storage-writing.md).
- Reading rollups: [Storage reading and log exploration](storage-reading.md). Charts of rollups are a later feature.
- What the incident reconstruction and the report do with removed data: [Incidents](incidents.md), [Forensics report](forensics-report.md).
- The options and their error text: [Configuration options](configuration-options.md).

## Implementation

`Vandox.Backend`: `RetentionService` (the job and its log lines), `KillHoldScanner`. `Vandox.Storage`: `RollupFolder`, `RetentionRemover`,
`RetentionQueries`, `SqliteStore` (`IRetentionStore`, `IRollupReader`). The step times are measured with `tools/Vandox.StorageBenchmark`
(`docs/BENCHMARKS.md`, *Retention steps*).
