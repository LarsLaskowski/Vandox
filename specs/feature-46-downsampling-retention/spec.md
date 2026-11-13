# Spec: Downsampling and retention

Source: Issue #46
Status: Draft

## Problem

`vandoxd` keeps every stored record forever ([Storage](../../docs/areas/storage.md), *Retention (planned)*), so the database grows
without bound until ingest stops at the free-space floor `ingest.min_free` ([Ingest endpoint](../../docs/areas/ingest-endpoint.md)).
The operator wants years of history at a bounded database size: old raw values summarized into 5-minute and hourly values, old data
removed by configurable periods, without ingest ever waiting on that work. The history views of v0.3.0 (#47, #49) read those
summaries; this feature produces them.

## Scenarios

- S1: Given the default periods and a running service, when data passes its period, then it is removed: metric values after 30 days,
  log lines after 90 days, process, connection and MariaDB status snapshots after 30 days, 5-minute values after 365 days, hourly
  values after 1,095 days; incidents are kept.
- S2: Given stored metric values, when a 5-minute or hourly bucket of a series (source, name and labels) has ended, then within 10
  minutes the store holds that bucket's minimum, average, maximum, last value and count, and keeps them after the raw values are removed.
- S3: Given buckets already summarized, when the agent backfills records or `vandoxd import` stores records whose capture times fall
  into them, then those buckets are recomputed and read as if the late values had been there from the start.
- S4: Given a `retention:` section in `vandoxd.yaml`, when the service starts, then it applies those periods; `unlimited` keeps that
  data forever.
- S5: Given a pass of the retention job is running, when the agent posts a batch, then the batch is stored and acknowledged with a delay
  of at most one step of the job.
- S6: Given a database of an earlier version holding months of data, when the new version starts, then it summarizes the stored metric
  values first and then removes the expired data step by step, while the web UI and ingest keep working.
- S7: Given log lines older than 90 days, when they are removed, then the template statistics of their hours stay (as hourly values,
  1,095 days), and `vandoxd templates` keeps the statistics of hours whose lines are gone.

## Errors and edge cases

- E1: An invalid period stops the start with `retention.<key>: must be a whole number of days from 1d to 9999d, such as 30d, or
  unlimited` (the configuration error rules of [Configuration and secrets](../../docs/areas/configuration-and-secrets.md)).
- E2: A late value for a bucket whose raw values have already been removed is merged into the stored bucket: minimum, maximum, count
  and average become exact for all values seen; the last value changes only if the late value is later.
- E3: A record already past its period when it arrives (an old import, a long backfill) is stored, summarized and removed by a later
  pass. Removed data never reads as a gap: no sequence number below removed records is reported missing, and the agent's next batch is
  no new start.
- E4: Another process holds the database lock (`vandoxd import`, `vandoxd templates`, a backup): the step waits up to the busy timeout,
  the pass ends with a warning and the next pass continues; nothing is half-removed.
- E5: A step fails (I/O error, full disk): it rolls back completely, the error is logged, the next pass retries; raw values are never
  removed before their buckets are stored.
- E6: Shutdown during a pass: the running step commits or rolls back; shutdown waits for at most that one step.
- E7: With a finite `retention.incidents`, an incident whose window ended before the period is removed together with its notes; its
  number is never given again.
- E8: Removal frees space inside `vandox.db`, not on the volume: the file does not shrink (that is #55), so ingest stopped at the
  free-space floor resumes only when space on the volume is freed.
- E9: A record whose capture time lies in the future (a wrong agent clock, a forged batch) is kept until its capture time is past its
  period.
- E10: Removed log lines are never returned by the log search, the logs view or the context of a hit.

## Non-functional requirements

- N1: New options with defaults `retention.raw` `30d`, `retention.rollups_5m` `365d`, `retention.rollups_1h` `1095d`, `retention.logs`
  `90d`, `retention.snapshots` `30d`, `retention.incidents` `unlimited` (plus `retention.events`, Q1). A record is past its period when
  its capture time is earlier than the backend's current UTC time minus the period.
- N2: Ingest is never blocked: one step of the job does at most the work of one ingest batch of 10,000 records, measured on the
  reference host with the storage benchmark ([Storage](../../docs/areas/storage.md), *Write throughput*); the write-throughput criterion
  still holds.
- N3: Bounded size: under a constant simulated load, once the shortest-lived data has passed its period, the file grows over a further
  span of the same length by at most the size of the 5-minute and hourly values written in it plus 10 % of its size at the start.
- N4: Once caught up, data is removed at most one day after its period ends; the job runs at start-up and at least hourly.
- N5: A pass holds at most one step's data in memory, within the container's 512 MiB ([Backend host](../../docs/areas/backend-host.md)).
- N6: A new schema step adds the summary tables; an older build refuses the newer schema (existing rule). Only the running service
  summarizes and removes; `vandoxd import`, `incidents`, `report` and `templates` remove nothing.
- N7: Every pass that changed data logs one `info` line with the counts it removed per kind and the buckets it wrote; a failed step logs
  its operation and error, never stored text.
- N8: The guarantee *No data gaps unless explicitly recorded* ([`.squad/project.md`](../../.squad/project.md)) holds after removal:
  a removed span never reads as normal (Q1 b).

## Acceptance criteria

- AC1: The six `retention.*` keys (seven with Q1) accept `1d` to `9999d` and `unlimited`, default as N1, and refuse anything else with
  the E1 text (S4, E1, N1).
- AC2: For every series, each 5-minute and hourly bucket holds minimum, average (mean of all values), maximum, last value (latest
  capture time) and count equal to a computation from all its values, available within 10 minutes after the bucket ends (S2, N4).
- AC3: A batch or import that adds values to already summarized buckets corrects exactly those buckets, whether their raw values are
  still stored or already removed (S3, E2). Tested.
- AC4: With the default periods, each kind is removed only once past its period (one second before stays, one after goes), incidents
  stay, and a future capture time is kept (S1, E7, E9, N1).
- AC5: No raw value is removed before its buckets are stored; a failed, locked or interrupted step leaves the database as before it,
  and the next pass completes the work; an upgraded database is migrated and summarized before removal, and no command other than the
  service removes data (S6, E4, E5, E6, N6).
- AC6: While a pass runs, an ingest batch is acknowledged with a delay of at most one step, and a step stays within N2 and a pass
  within N5 (S5, N2, N5).
- AC7: Under a simulated load over twice the shortest period, the database size meets N3 (N3).
- AC8: After removal, the log search and logs view return no removed line, no sequence gap or new start is reported for removed
  numbers, the template statistics of expired hours remain, and `vandoxd templates` keeps them (S7, E3, E10).
- AC9: The answers to Q1 hold: imported data, events and gaps, and incident window data are kept as answered (Q1, N8).
- AC10: A pass that changed data writes the N7 log line; a failed step its error (N7).

## Out of scope

- Reading the summaries in charts and choosing a resolution (#47, #49), showing gaps (#54), live incidents (#53).
- Shrinking the file, vacuum, backup (#55) (E8).
- Summaries of process or connection snapshots (memory per program over a year, #50 may need them: a follow-up issue).
- A command to remove imported data on demand; several agents (#89).

## Open questions

- Q1 (escalated to the Product Manager, three parts; proposed answers):
  - a. Imported data: kept outside the periods (recommended: the v0.1.0 forensics history stays whole, and an import is finite and
    idempotent), or removed by capture time like agent data, or aged from the import time.
  - b. Events (`kernel_event`, `service_state`, `backup_run`), gap records and gap rows, and listener findings: kept as long as the
    hourly values, 1,095 days, under `retention.events` (recommended: #49 marks events over a year, and a summarized span without its gap
    would read as normal, against the gap guarantee), or removed with the raw data after 30 days.
  - c. The data inside a kept incident's window (log lines, snapshots, metric values): kept as long as the incident (recommended: its
    charts stay, and a re-run of `vandoxd incidents` cannot replace it by a poorer one; residual: forged kills could pin data, bounded
    by `ingest.min_free`), or removed by the periods with older incidents frozen against re-runs and their charts empty.
- Answered: the UI stack is .NET Blazor, not Go templates (records 0073, 0197); this feature has no UI.
- Answered: "snapshots" are the `process_snapshot`, `connection_snapshot` and `mariadb_status` records, "raw data" the `metric` values
  (wire format records), the template statistics count as hourly values ([Storage](../../docs/areas/storage.md)).
- Answered: incidents are unlimited by default and configurable (the issue); the residual of forged incidents is recorded in the plan's
  decision record.

## Area documents

- New `docs/areas/retention-and-rollups.md`: the periods and what each covers, bucket rules, recomputation, pass timing, failure and
  shutdown behavior, the log line; [Storage](../../docs/areas/storage.md) *Retention (planned)* replaced by a link and the schema rows.
- [Configuration options](../../docs/areas/configuration-options.md): the `retention.*` keys, form and error.
- [Storage writing](../../docs/areas/storage-writing.md): marking buckets from the write path; deletes keep `log_fts` and derived rows.
- [Storage reading](../../docs/areas/storage-reading.md): the summary reader for #47.
- [Log templates](../../docs/areas/log-templates.md), [Incidents](../../docs/areas/incidents.md),
  [Forensics report](../../docs/areas/forensics-report.md), [Ingest endpoint](../../docs/areas/ingest-endpoint.md): behavior after
  removal (the rebuild, re-runs, observed hours, the floor sentence of E8); `docs/ARCHITECTURE.md` retention table no longer planned.
