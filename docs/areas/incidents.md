# Incidents

## Scope

An **incident** is one outage reconstructed from the stored data: the OOM kills whose windows overlap, a timeline of the events of the
window, a structured hypothesis about the cause, metric extremes, the connection peak and the log templates of the window. It is stored
([Storage writing](storage-writing.md), *Incidents*) and kept without limit. The web UI shows a list and a page per incident, with charts
of the window, every time linked to the logs of that moment, and a note per incident ([Incidents view](web-ui-incidents-view.md)); the
[Forensics report](forensics-report.md) lists them.

## When incidents are built

Only by the sub-command `vandoxd incidents` ([Backend host](backend-host.md), *Command line and exit codes*), which **rebuilds from
everything stored**; nothing runs it automatically, so it is run again after each import. Incidents of live and backfilled data alike are
built the same way; nothing alerts ([Detection, alerts and reports](detection-and-reports.md), *Live data and backfill*). The agent's
`kernel_event` and `gap` records are not read yet (issue #53).

## Kills and windows

- A **kill** is an event of the signature `oom_kill` (the copies of one kill line in `syslog`, `kern.log` and the journal within 60
  seconds are one kill, [Log signatures](log-signatures.md), *Events and merging*). The window of a kill at `t` is `[t − before, t +
  after)`, both ends clipped to the storable range; `before` is `incidents.window_before` (default `1h`) and `after` is
  `incidents.window_after` (default `30m`), each 1 minute to 24 hours ([Configuration options](configuration-options.md)).
- Kills are taken in time order. A kill whose window starts **before the end of the open incident** joins it, and the incident then
  ends where that kill's window ends; otherwise the open incident is closed and the kill opens the next. Windows that only touch are
  two incidents.
- An incident keeps its **first 16 kills** (time, host, victim process ID and command, anonymous RSS, `oom_score_adj`, the first 16
  record IDs, the number of lines) and counts all of them.
- A kill captured at exactly the last storable instant is ignored: no window can end at its own kill.

## Content

- The **lookback** is the window before the first kill. Boots and service events are judged on the lookback; the timeline covers the
  whole window.
- The **timeline** holds every detected event of the window (time of its first line, signature, host, texts, numbers, the first 16
  record IDs, line count), taken in arrival order up to **1,000 entries and 4 MiB** of serialized JSON (each entry measured as it is
  stored, with the serialization the store uses). The first event that would exceed either cap and every later one are counted in
  `timeline_omitted` and not kept. Two entries are added outside the budget: `backup_first_seen` and `backup_last_seen`, the first and last
  process snapshot of the window that holds a backup program (only the first when it is the same snapshot; the program name and the backup
  programs' summed RSS). The timeline is ordered by time, then type, then first record ID.
- The **programs of a snapshot** are its `programs` entries or, when it has none, its processes grouped by the first space-separated
  word of `command` (the command itself when that word is empty). A name that starts with `/` becomes the part after its last `/`
  (unless that is empty), so `/usr/sbin/mariadbd` and `mariadbd` are one program. RSS and counts are summed and saturate.
- The **snapshot period** is the lookback after its last boot (`boot` event): a snapshot captured at or before that boot describes
  processes the reboot ended and never serves as the baseline; with no boot the period is the whole lookback. A snapshot at or after
  the first kill never counts.
- The **hypothesis** comes from the first and the last process snapshot of the snapshot period and their number: the **growth
  candidates** are the programs of the last snapshot whose RSS is above their RSS in the first (0 when absent there), by growth
  descending, then name, **top three**; the **consumers** are the last snapshot's programs by RSS, **top three**; `backup_running` is
  true when a snapshot of the period holds a backup program, false when snapshots exist and none does; `recent_boot_at` is the time of
  the last boot in the lookback; `service_events` counts `service_start`, `service_stop` and `service_failed` events in the lookback.
  Backup programs are the built-in list, **`pzstd` only**, matched by the program name after the normalization above.
- **Metric extremes**, in this order: the minimum of `mem_available_bytes`, the maximum of `swap_used_bytes`, of `pressure_avg10`
  with the labels `resource=memory` and `kind=some`, of `pressure_avg10` with `resource=memory` and `kind=full`, and of `load1`.
  Labels must equal exactly; a tie keeps the earliest sample; a value that is not finite is not counted. Each extreme has its value,
  time, source and sample count.
- The **connection peak** is the connection snapshot of the window with the most sockets (the sum of its state counts; a tie keeps
  the earliest), with its time, record ID, the number of snapshots and its three processes with the most sockets (summed per
  command). It is taken over the snapshots of **every source** in the window, regular and full alike (the state counts cover every
  socket in both). The agent's snapshots (source `connections`) count sockets without an owner, such as `TIME_WAIT`, which `lsof -ni`
  never listed, so when a window holds snapshots of the agent and of an imported legacy log, the agent's usually win the peak; the
  peak names its record, so its source can be read there. This is accepted, not filtered (record 0115). The *Connections* chart of the
  incident page shows the source of the earliest snapshot only ([Incidents view](web-ui-incidents-view.md)).
- **Templates**: the new templates of the window (source, program, template, first seen, count) and the template spikes of the
  window (source, program, template, hour, count, usual level), the first 20 of each ([Log templates](log-templates.md), *Queries*). The
  spike query covers at most 744 hours from the start of the window.

## Unknown is not none

Missing data makes a statement **unknown**, never negative: fewer than two snapshots in the snapshot period give `growth_known` false
and no growth candidates; no snapshot there gives no consumers and `backup_running` null; a metric without samples is absent; no
connection snapshot gives no peak (`connections` null).

## Re-running

- A rebuilt incident is matched to the stored ones whose window overlaps it ([Storage writing](storage-writing.md), *Incidents*): the
  oldest number keeps the incident and takes the new content, the other overlapped ones are merged into it, an incident without overlap
  is added, and an unchanged one is left untouched. A number that was merged away is never reused.
- **Notes stay with the incident.** The note a user wrote for an incident ([Incidents view](web-ui-incidents-view.md)) survives every
  re-run that updates or keeps the incident (the number is kept), and a note of an incident that a re-run merges away is moved to the
  incident that survives, in the same transaction, keeping the number it was written for ([Storage writing](storage-writing.md),
  *Incident notes*). A re-run never deletes or rewrites a note.
- A stored incident that no rebuilt one overlaps is **kept** as it is, because its lines may have expired.
- Every incident is saved in its own transaction: an interrupted run keeps what it saved, and the next run completes it.
- An incident the store refuses (a rule of [Storage writing](storage-writing.md), *Incidents*, such as more than 8 MiB of serialized JSON) is **skipped**: it is logged as
  a warning with its first kill time and the store's text, counted as skipped, and the run goes on.
- The run reads every stored log line in capture order in pages of 1,000 (by capture time, then record ID), so it is never confused by
  lines that share a time. A page that does not lead past the previous one is an error (`store: reading records: page did not
  advance`). For each incident it then reads the log lines and metrics of its window in pages of 1,000 and the process and
  connection snapshots one at a time.

## Limits and errors

- **Memory** does not grow with the number of lines, kills or incidents: one page of up to 1,000 log lines of the pass over all lines
  (held while an incident it closes is built, about 40 MiB at the model caps) and one page of the incident's window, two detectors, one
  open incident and its builder (at most 1,000 timeline entries within 4 MiB, two program maps of at most 4,096 entries each, one
  connection peak, 20 + 20 templates). The largest single read is one process snapshot, read as UTF-8 bytes; one forged at every cap (a
  legacy monitoring log with 4,096 processes and programs, all texts such that the stored JSON escapes them to six bytes per character,
  about 100 MiB of stored JSON) peaks at about **240 MiB**, plus the page of log lines held by the pass over all lines, which leaves the
  service less than half of the container's 512 MiB while it is read. Only a database written by an earlier build can hold such a
  snapshot: this version refuses a payload of more than 16 MiB of JSON when it is written ([Storage writing](storage-writing.md)), so a
  snapshot stored by this version is read from at most 16 MiB of JSON.
- **Time** grows with all stored log lines and, per incident, with the data of its window, so forged kills spaced far enough apart
  to form many incidents multiply the window reads; the cost of the spike query is not bounded by its range ([Log
  templates](log-templates.md), *Limits and errors*). The sub-command runs off any request path.
- Kills and boots come from **stored log lines**; a reboot without a stored boot line is not seen, and the snapshots before it then
  count as the growth baseline. Copies of one kill more than 60 seconds apart (a wrong `import.time_zone`) are two kills. The old and
  the new names of a legacy program are only partly joined by the program key. `rss_overcounted` program sums add shared memory
  more than once.
- Backup recognition covers `pzstd` only; other backup programs are not recognized.
- **Forged lines make forged incidents**: any local process can write a line that reads as an OOM kill. A flood of kills close
  together merges into one incident whose kept parts are capped; kills spaced more than `before + after` apart make many incidents,
  each up to 8 MiB and kept without limit, so the disk use from forged lines outlives their retention (issue #46).
- The victim command, hosts, units, timeline texts, programs, commands and templates of an incident are **external text**: whoever shows
  them escapes them (security area 12 of `.squad/project.md`). The terminal output of `vandoxd incidents` writes the victim command of an
  incident's first kill in double quotes, with `"` and `\` escaped by a backslash and line breaks, tabs, control, format and the other
  invisible characters escaped as in the report's code spans ([Forensics report](forensics-report.md), *External text in the report*); its log lines carry only the store's text and the
  first kill time, never input text.
- The agent's `kernel_event` and `gap` records are not read yet (issue #53). Retention (issue #46) must keep a re-run from replacing
  a stored incident by a poorer one ([Storage](storage.md), *Retention (planned)*).

## Related decisions

- [0096](../decisions/0096-outage-incidents-rebuilt-from-stored-data-merged-by-overlapping-windows.md) — why incidents are rebuilt by a
  sub-command, matched by overlapping windows, given a structured hypothesis from fixed rules and bounded in memory and size.
- [0107](../decisions/0107-incidents-view-summaries-notes-per-incident-number-time-links-to-the-logs.md) — why notes follow an incident
  through updates and merges and how the web UI shows incidents.
- [0115](../decisions/0115-connection-collector-proc-net-tables-regular-and-full-snapshots-in-one-record-kind.md) — why the agent's
  connection snapshots count sockets without an owner and why the connection peak is not filtered by source.

## Not here

- How incidents and their notes are stored, matched and merged in the database: [Storage writing](storage-writing.md), *Incidents* and
  *Incident notes*; the command line and exit codes of `vandoxd incidents`: [Backend host](backend-host.md).
- The options `incidents.window_before` and `incidents.window_after` and their syntax: [Configuration options](configuration-options.md).
- How a kill and a boot are recognized: [Log signatures](log-signatures.md); the new templates and spikes: [Log templates](log-templates.md).
- The incidents list, page, charts and notes: [Incidents view](web-ui-incidents-view.md).
- Recurrence of outages and the activities before them: [Forensics report](forensics-report.md).
- Live incidents from the agent's records: issue #53.

## Implementation

`Vandox.Core.Incidents` (`Incident` and its parts, `KillClusterer`, `KillCluster`, `IncidentBuilder`, `MemoryAnalysis`, `BackupPrograms`,
`IncidentSignatures`, `IncidentLimits`, `IncidentJson`); `Vandox.Storage` (`IIncidentStore`, `IncidentRepository`, `SqliteStore`);
`Vandox.Backend` (`Incidents/IncidentReconstructor`, `Incidents/RecordPager`, `Cli/IncidentsCommand`, `Cli/IncidentSummaryWriter`, and
`Cli/Terminal` for the quoting).
