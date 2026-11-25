# Storage reading

## Scope

How stored data is read from the backend's database ([Storage](storage.md)): record series and kinds, incidents and their notes,
template statistics, rollups, the time-series reads, listener history, the agent timeline (live flag, gaps, clock), the reads of the overview page, the reads of the history view (the stored metrics and the snapshot closest to a moment), the per-program and the connection rollups, the reads of the services view (service availability, MariaDB status steps, thread snapshots); the log search and exploration of the logs view are in [Log search and exploration](log-exploration.md). `vandoxd incidents`, `vandoxd report` and the web UI's views rely on it. Every read runs on
a reader connection and is interrupted by a cancelled token or a passed deadline ([Storage](storage.md), *Database file and connections*).

## Behavior

### Records

- A series is read by kind, source and name for a time range, in time order; a kind-wide time range uses the kind indexes. A query
  returns at most 10,000 records.
- A series or a kind can be paged through records that share a capture time with an *after ID*: when it is above 0, records whose
  capture time equals the range's start and whose ID is at most the after ID are skipped (the next page starts at the last capture time,
  with its ID as after ID); a negative value is refused with `store: invalid query: after id must not be negative`. This holds in whole
  100 ns, because every writer stores capture times in whole 100 ns ticks, so the capture time a reader returns equals the stored one. A
  caller that pages must check that each page leads past the previous one.
- Every stored record read carries whether it was stored as live data (the `live` column).
- A stored payload is read from the column's UTF-8 bytes, not from a string, so the payload exists once as bytes and once as the parsed
  document. It is read with the payload rules of the wire decoder, so an unknown member is ignored; this includes the `Kind` member that
  rows written before #183 carry.

### Rollups

- The rollup buckets of one metric series (source and name; every label set of it) are read for one tier, 5 minutes or one hour, and a
  time range of bucket starts (the start inclusive, the end exclusive), oldest first, at most 10,000 per query. The range and the limit
  follow the rules of a record query; a missing source or name, or an unknown tier, is refused as an invalid query.
- A bucket carries its minimum, average (the sum over the count), maximum, last value with its capture time and count, and its labels as
  stored (empty for none). A bucket is current only up to the last fold step, at most 5 minutes behind ([Retention and rollups](retention.md)).

### Program rollups

The top programs of a source per step and the sources that have program buckets are read from the per-program rollups through
`IProgramRollupReader`, with their own limits and errors (at most 50 programs, 2,000 steps, 256 sources; `store: reading program rollups`),
described in [Program rollups](program-rollups.md), *Reading*.

### Connection rollups

The states and top programs of a source per step, its top 20 remote addresses over a range and the sources that have connection buckets
are read from the connection rollups through `IConnectionRollupReader`, with their own limits and errors (10 programs, 20 addresses, 2,000
steps, 256 sources; `store: reading connection rollups`), described in [Connection rollups](connection-rollups.md), *Reading*.

### Time-series reads

The reads of the time-series query ([Time-series queries](time-series-query.md)); each takes the caller's token and is interrupted by it.

- **Raw values** of one series (source and name) in a range: rows ordered by capture time, labels and record ID, labels normalized as the
  fold does (none and `{}` as empty), at most 10,000 distinct times and 1,024 lines per read; the read reports whether the times
  exceeded its limit and whether lines were cut. **Rollup steps** of one series, one tier and a step: the buckets of the range merged
  per step in SQL (minimum, maximum, sum clamped to a double, count, the last value of the step's latest bucket, whose last time the fold
  writes inside the bucket; the step index is the floor of the bucket start over the step), one line per label set, at most 1,024
  lines. The label sets are found by a loose scan of the primary key, at most 1,024 sets examined, and are ordered by the UTF-8 byte
  order of the label text, never re-sorted in the caller.
- **Gaps in a range**: the rows of the agent timeline whose span overlaps the range and whose derived state is not `closed`, of all
  agents, ordered by start and ID.
- **Earliest unfolded**: the earliest capture time of the metric values above the fold watermark, none when every value is folded.
- **Service changes** in a range: for each unit its first row in the range and every row whose `restarts` differs from, or whose
  `active_enter_at` is not the same as, its predecessor's, with the predecessor's values and both rows' boot IDs, in order of capture
  time and ID, at most the limit. The records of the range are streamed once in capture order with three members each (unit, `restarts`,
  `active_enter_at`), the last record of each unit is kept, and only the rows found and their predecessors are read in full; the read
  stops at the limit. **Previous service states** of a list of units before a time: the latest row of each unit among the 10,000 most
  recent `service_state` records before the time; the units are one bound JSON parameter and filtered after the 10,000 rows are limited.
- **Boot times** in a range: the boot events **newest first** by capture time and ID, each with its record ID, paged with a *before ID*:
  the next page reads the range from `From` to the capture time of the last boot read with its ID as `BeforeId` (`BeforeId` 0 keeps `To`
  exclusive), and a range may end where it starts. The read refuses an *after ID* with `store: invalid query: after id is not used by boot
  times` and a negative *before ID* with `store: invalid query: before id must not be negative`; every other range read refuses a *before
  ID* with `store: invalid query: before id is only for boot times`. **Gap records**, **kernel events** and **backup runs** in a range:
  projections of the named members of the rows (never the payload or a message), ordered by capture time and ID, paged with an *after ID*.
- **Limits.** Each read refuses a limit above its own maximum with `store: invalid query: limit must be 1 to <maximum>`: 1,001 for gap
  rows and the event projections (boots, gap records, kernel events, backup runs), 2,001 for service changes; the previous-state read
  refuses more than 2,001 units. The gap rows and the service changes have no paging ID and refuse a non-zero *after ID* with
  `store: invalid query: after id is not used by gap rows` and `store: invalid query: after id is not used by service changes`. A
  range follows the rules of a record query. A caller reads at most ten pages of 1,001 rows per kind.

### Incidents

- **By range:** incidents are read by range (`started_at < to` and `ended_at > from`, ordered by start, then number) with the window and
  limit rules of the record query, and counted. A `data` value that does not decode (including bytes that are not valid UTF-8 in a member
  the incident reads; such bytes in an unknown member are skipped unchecked) is `store: reading incidents: decoding incident data`. The
  `data` column is read from its UTF-8 bytes, like a payload. A page holds every returned incident decoded at once: in the worst case up
  to 10,000 incidents per page, each decoded from up to 8 MiB of JSON, so the memory of a read grows with the limit. A caller that serves
  a user does not page through this read: the web UI reads the summaries, one incident and the notes below.
- **Summaries** return incidents newest first (window start descending, then number descending), at most a limit of 1 to 100 per call,
  continuing after a position (a start and a number: a row follows when its start is earlier, or equal and its number lower, so equal
  starts neither repeat nor skip). A summary carries the columns (number, window, first and last kill, number of kills, created and
  updated), the first victim, the hypothesis and whether the incident has notes. The first victim, last kill and hypothesis are read from
  the document by a projection that skips every other member (timeline, metrics, templates), one row's bytes at a time, so a page holds
  one document's bytes at once. Exact errors: `store: invalid query: limit must be between 1 and 100`, `before must be within the
  storable range`, `before id must be positive`; undecodable data is `store: reading incidents: decoding incident data`.
- **One incident** returns the stored incident of a number decoded whole (up to 8 MiB of JSON), or none; a number that is not positive
  is `store: invalid query: incident ID must be positive`.
- **Notes** return the notes that belong to an incident, by `written_for`, at most **64**, with a flag set when a 65th exists; the same
  rule for the incident ID.

### Template statistics

The template statistics are read by the queries of [Log templates](log-templates.md) (template of a line, hourly counts, new templates,
templates above their usual level), with the same time range and limit rules, on a reader connection. The cost of the usual-level query is
not bounded by its range or limit; a cancelled token or deadline interrupts it ([Log templates](log-templates.md), *Limits and errors*).

### Listener history

The listener history reads only the narrow derived tables and decodes no payload. The queries of one read share one reader connection
without a shared transaction, so a batch committed between them can make a total differ from the rows by that batch.

- **Tallies** return, for the 64 lowest agent IDs of `connection_marks` (byte order of the text), the number of marks, the first and last
  capture time and the number of starts per outcome (compared on all ports, compared on ports 1 to 1023 only, not compared), and the
  number of further agent IDs (counted only when 64 were returned).
- **Findings** return the latest 1 to 20 findings of one kind with the total of that kind: *after an agent start* reads the
  `listener_comparisons` rows with `finding = 1`, *found by the agent* the `connection_marks` rows with `new_count > 0`, and *found by the
  agent with changes only* those with `new_count = 0` and `changed_count > 0`. The outcome of both agent kinds is the ports the agent
  compared: `all_ports` for `all`, else `privileged_only`.
- Both order by capture time descending, then agent ID ascending, then `seq` descending, so the arrival order never matters (the capture
  time decides which findings are the latest, and a clock that stepped back can keep an older one).
- A finding holds at most 64 listeners and the number left out (`new_count` minus the listed), and at most 64 changed listeners and the
  number left out (`changed_count` minus the listed).
- Exact errors: `store: invalid query: kind must be a known finding kind` and `limit must be between 1 and 20`; a stored listener list
  that does not decode is `store: reading listener findings: decoding listeners`; an unknown stored outcome reads as not comparable.

### Agent timeline

- **Gaps** returns a page of 1 to 1,000 gaps of one agent in ID order (after an ID, with a limit), optionally of one state, with kind,
  cause, state, number range or time span, `detected_at` and `changed_at`. The state of a time gap is the derived one
  ([Storage writing](storage-writing.md), *Agent timeline*), also for the filter.
- **Clock** returns the measured offset and when it was measured (empty fields for an agent without a measurement), or nothing for an
  agent the store has no stream of.
- Exact errors: `store: invalid query: agent id is invalid`, `unknown gap state`, `after id must not be negative`, `limit must be 1 to
  1000`.

### Overview reads

The four reads of the overview page ([Overview page](web-ui-overview.md)); each is one reader lease, bounded by rows, and ignores every record captured after its `until` bound (the page passes now plus 90 seconds), so a record dated far ahead hides
no current data and is not returned.

- **Freshness** (`live scan limit` 1 to 20,000): the agent of `agent_streams` whose highest-numbered record has the latest receive time,
  with that time; an agent whose record was removed comes after the others (the one with the latest stored capture time first) with no
  receive time; without a stream row the agent ID is empty. For that agent it returns the number of open sequence gaps, their earliest
  start and latest end. It scans the newest metric records up to the bound, newest first, at most the limit: the first one stored as
  live gives the live time; when none is live it returns the capture time of the oldest record read.
- **Latest values** (1 to 16 series, a window above 0 and at most one day, 1 to 64 label sets): per series, the newest capture time up to the
  bound, then at most 10,000 rows from the window before it, newest first. Per label set it returns the newest value, the label sets in
  ordinal order of their stored text, at most the requested number; it also returns the number of distinct label sets of the scan, counted
  by the first 8 bytes of the SHA-256 of the text (a collision can undercount by one). A read holds at most the requested number of label texts.
- **MariaDB status**: the newest `mariadb_status` record of source `mariadb` up to the bound, read by `json_extract` of the availability,
  `Threads_connected`, `Memory_used` and `max_connections`, never decoded whole; `max_connections` is returned as a number only when the
  stored text is at most 20 ASCII digits that fit a 64-bit integer, else none.
- **Service states** (a retirement above 0 and at most one day, a limit of 1 to 1,024): the newest `service_state` capture time up to the
  bound, then the latest record per unit within the retirement before it; units whose state is not `active` first, then by unit in
  ordinal order, at most the limit, with the number of units and the newest capture time.
- A broken rule is refused before any read with `store: invalid query: live scan limit must be 1 to 20000`, `at least one series is
  required`, `at most 16 series`, `window must be positive and at most one day`, `label sets must be 1 to 64`, `retirement must be
  positive and at most one day` or `limit must be 1 to 1024`.

### History reads

The three reads of the history view ([History view](web-ui-history-view.md)), the snapshot reads also those of the processes view ([Processes view](web-ui-processes-view.md)) and the latest snapshot also those of the network view ([Network view](web-ui-network-view.md)); each is one reader lease and one or a few seeks of an
existing index, never a scan.

- **Stored metrics** (a limit of 1 to 256, refused otherwise with `store: invalid query: limit must be 1 to 256`): every source and name
  that has a raw value in `metrics` or a bucket in `metric_rollups_1h`, merged without repeats, by source, then name, in the byte order of
  their UTF-8 form (as SQLite orders text), the first *limit* of them; the result says whether a further metric exists. It is a skip
  scan: the first source with its first name, then the next name of that source, then the first name of the next source, one index seek per entry within a source and three for an entry that starts the next source (`metrics(source, name, captured_at)`, the primary key of the hourly rollups), at most *limit* + 1 entries per table. Its cost
  grows with the entries read, not with the stored rows.
- **Snapshot stamp** (a kind, optionally a source, a moment, a window and a side): the ID and capture time, nothing else, of the snapshot of the kind closest to
  the moment on one side. *At or before* takes capture times from the moment minus the window up to the moment, the latest first;
  *after* from after the moment up to the moment plus the window, the earliest first; among records of one capture time the lower ID wins.
  It returns nothing when no snapshot lies in the window. The kind must be `process_snapshot` or `connection_snapshot`
  (`store: invalid query: kind must be a snapshot kind`), the window above 0 and at most one day (`store: invalid query: window must be
  positive and at most one day`) and the moment within the storable range (`store: invalid query: at must be within the storable range`).
  The read seeks `records(kind, captured_at)`; the *at or before* side adds a temporary tree for the ID term of its order, which holds
  only the two columns read. With a **source** the read takes only the snapshots of that source, as the same two reads with the condition
  `source = $source`, and seeks `records(kind, source, captured_at)`; without one it takes the snapshots of any source, as before. An
  empty source is refused (`store: invalid query: source must not be empty`). The processes view reads the snapshot of the source its
  charts show this way, and the stamp of the other side with the same source, so *Snapshot before this moment* stays within the source.
- **Latest snapshot stamp in a range** (a kind, a source, `from` and `to`): the ID and capture time of the latest snapshot of the kind and the
  source captured at or after `from` and before `to`, whatever the length of the range. It takes two seeks of `records(kind, source,
  captured_at)`: the latest capture time in the range by one descending seek, then the lowest ID at that time. A range with `from` at or after
  `to` returns nothing, as does a range without a snapshot of the source. The kind must be a snapshot kind (`store: invalid query: kind must be
  a snapshot kind`), the source not empty (`store: invalid query: source must not be empty`) and both bounds within the storable range
  (`store: invalid query: at must be within the storable range`), refused before any read. The stamp is read with *Snapshot record*.
- **Snapshot record**: the record of a stamp, by ID and kind (the kind rule above), read and decoded like a record of the record
  reads, so one payload is decoded. It returns nothing when retention removed the record after the stamp was read.
- The view reads the stamps of both sides and decodes only the record of the closer one, so no read decodes a snapshot it does not show.

### Services view reads

The reads of the services view ([Services view](web-ui-services-view.md)): `ServiceAvailabilityAsync` (`IServiceHistoryReader`), `MariaDbStatusAsync`, `ThreadSnapshotsAsync`,
`ClosestThreadSnapshotAsync` and `ThreadSnapshotAsync` (`IMariaDbHistoryReader`), and `TimeSeriesService.RestartsAsync` ([Time-series queries](time-series-query.md)). Each is one reader lease with constant
statements and bound parameters. Each refuses a broken query before any read with an invalid-query error: `store: invalid query: from must be before to`, `store: invalid query: from and to must be within the
storable range`, `store: invalid query: at must be within the storable range`; every lead-in and window edge that a read derives is clamped to the storable range.

- **Collector gaps** (records [0227](../decisions/0227-availability-is-the-time-a-recorded-state-holds-up-to-a-cap-and-gaps-are-unknown.md)): the availability and the status read each read the gaps of their own collector (`systemd`, `mariadb`) in their lease,
  and not through the time-series query's gap list. They stream, without pages, the stateful gap rows that are not closed and overlap `[from, to)` (they concern every collector) and the `gap` records captured from
  1 hour before `from` to 7 days after `to`, and skip a record whose span does not parse or does not overlap the range, and one whose collector the read's collector does not concern: a gap concerns a collector when
  it names it, names none, or names one the time-series query does not name. Each span is clipped to the range and merged with the overlapping and touching spans already read, in any order, so a month of per-run
  gaps of 2,880 a day is one span. At most **1,000 merged spans** are kept; past them the latest-starting span is dropped, the result's `GapsCompleteUntil` is lowered to its start and every later span is ignored.
  The result also says whether a kept record names exactly the collector (`SystemdGaps`).
- **Service availability** (`ServiceAvailabilityQuery`: `from`, `to`): one stream over the covering index `records_service_state` of the window `(from - 65 minutes, to)` in capture order (the start is excluded, so every streamed record lies
  in the range or carries its state into it), each with its unit and active state, folded per unit: a state holds from its record to the unit's next record, at most 65 minutes, minus the gap spans and the time after
  `GapsCompleteUntil`; *available* are `active`, `reloading` and `refreshing`; *unknown* is the range less the available and the not available time. At most **256 units** are kept, the first by ordinal name; a
  name above the largest kept is skipped, one below it evicts the largest, which does not return. Only when a name was skipped or evicted a second statement, `count(DISTINCT` over the same window and index, gives `UnitCount`,
  else it is the number of rows. It keeps a **temporary b-tree** of the window's unit names in SQLite's temporary storage (the `/tmp` of the container is a tmpfs), bounded by the 10-second interrupt of the read, not by the 256
  cap. Then the sub-state of the latest record of each kept unit is read by ID (`json_each` over at most 256 IDs). No payload is read for the stream.
- **MariaDB status** (`MariaDbStatusQuery`: `from`, `to`, `step`): the index `records_mariadb_status` (schema step 18, [Storage](storage.md), named with `INDEXED BY`) is streamed over `[from - 2 minutes, to)` for the source `mariadb`,
  reading capture time, availability, `Memory_used` and `Threads_connected` without the payload, and folded onto the steps (the multiples of the step since the epoch that overlap the range): a record holds from its capture time to the next
  record, at most 2 minutes, minus the gap spans of the collector `mariadb`; per step the share of the up time in the known time, and the minimum, average and maximum of `Memory_used` and `Threads_connected` over the `up` records,
  read as doubles (a value above the largest signed 64-bit integer comes back as a real); the totals of up, down and not answering time, the unknown time, the latest `Memory_used` with its time, the number of records in the range, and the oldest
  status record (`min(captured_at)` through the index). Also refused: `store: invalid query: step must be positive` and `store: invalid query: at most 2001 steps`.
- **Thread snapshots** (`ThreadSnapshotQuery`: `from`, `to`, `limit`): the status records of the range with `json_array_length(data, '$.threads') > 0` through the index, newest first by capture time and then ID, at most *limit*
  (`store: invalid query: limit must be 1 to 51`); per stamp the number of recorded threads, the *running* threads (command not `Sleep`) and the longest running time, the greatest `time_seconds` of those capped in SQL at
  3,153,600,000 so that no stored number reaches a `TimeSpan` (a value above the largest signed 64-bit integer reads as that cap). **Closest** (`at`, `window`): the latest snapshot at or before `at` within the window and the earliest after
  it within the window, the nearer one, the earlier on a tie, the lower ID within one capture time; the window must be positive and at most one day (`store: invalid query: window must be positive and at most one day`).
  **Snapshot record** (`recordId`): the header (capture time, number of threads and running threads) and the thread objects of the record in array order whose command is not `Sleep`, at most **101**, each deserialized into
  the data model's `MariaDbThread` (`id` and `time_seconds` keep their full unsigned value); a thread object that does not deserialize is a store error. It returns nothing when the record has no threads or retention removed it.
- **Restarts** (`RestartsAsync(from, to)`): the markers of the time-series query reduced to the service restarts, without a series; see [Time-series queries](time-series-query.md).

## Limits and errors

- The exact error texts are stated with each read above.

## Related decisions

- [0096](../decisions/0096-outage-incidents-rebuilt-from-stored-data-merged-by-overlapping-windows.md) — why the record query pages by ID.
- [0097](../decisions/0097-reader-statements-interrupted-by-a-progress-handler-bound-to-the-token.md) — why a cancelled token or deadline interrupts a reader statement.
- [0218](../decisions/0218-the-overview-reads-latest-values-through-its-own-bounded-store-reads.md) — why the overview has its own bounded reads over the existing indexes.
- [0223](../decisions/0223-process-snapshots-are-folded-into-per-program-buckets-beside-a-source-total.md) — why the per-program rollups have their own read.
- [0222](../decisions/0222-the-history-view-reads-the-moments-snapshots-and-the-metric-list-through-its-own-bounded-reads.md) — why the history view has its own metric list and snapshot reads.
- [0225](../decisions/0225-connection-snapshots-are-folded-into-per-state-program-and-address-buckets-ended-buckets-trimmed.md) — why the connection rollups have their own read.
- [0227](../decisions/0227-availability-is-the-time-a-recorded-state-holds-up-to-a-cap-and-gaps-are-unknown.md) — why the services view reads the gaps of its own collector, merged before their cap, and folds the state time itself.
- [0228](../decisions/0228-mariadb-status-members-are-indexed-and-folded-on-the-time-series-step-axis.md) — why the status members are read through a covering index and the thread rows through the data model's type.
- [0224](../decisions/0224-the-processes-view-stacks-program-rollups-on-the-history-chart-and-compares-programs-of-one-source.md) — why the snapshot stamp read can be narrowed to one source.
- [0107](../decisions/0107-incidents-view-summaries-notes-per-incident-number-time-links-to-the-logs.md) — why the incidents view reads summaries.
- [0116](../decisions/0116-listeners-across-agent-starts-compared-when-stored.md) — why the listener history reads the narrow derived
  tables.
- [0180](../decisions/0180-sequence-and-time-gaps-are-stateful-rows-that-become-final-after-a-quiet-wait.md) — why gaps are read from
  stateful rows.
- [0181](../decisions/0181-the-agents-clock-offset-is-measured-from-caught-up-self-samples.md) — why the clock is read from stateful rows.

## Not here

- The database file, connections, interruption of reader statements, schema and retention: [Storage](storage.md).
- How the rows are written: [Storage writing](storage-writing.md).
- The template queries and the normalization: [Log templates](log-templates.md).
- The literal-term log search and the bounded log exploration: [Log search and exploration](log-exploration.md).
- The page of the search (its address and deadline): [Logs view](web-ui-logs-view.md); the page of the history reads: [History view](web-ui-history-view.md).

## Implementation

`Vandox.Storage`: `SqliteStore` implements the interfaces the callers depend on (`IRecordReader` with `RecordQuery.AfterId`,
`ILogTemplateReader`, `IIncidentBrowser` with `IncidentSummariesAsync`, `IncidentAsync` and
`IncidentNotesAsync`, `ITimeSeriesReader` (the reads above, with `TimeSeriesQueries`), `IOverviewReader` (`OverviewQueries`, `LabelSetTally`), `IProgramRollupReader` (`ProgramRollupQueries`), `IConnectionRollupReader` (`ConnectionRollupQueries`), `IHistoryReader` (`HistoryQueries`, `SkipCursor`; `StoredMetricList`, `SnapshotQuery`, `SnapshotStamp`, `SnapshotSide`), `IServiceHistoryReader` (`ServiceHistoryQueries`, `ServiceAvailabilityFolder`) and `IMariaDbHistoryReader` (`MariaDbHistoryQueries`, `MariaDbStatusFolder`; with `CollectorGapReader`, `GapSpanSet`, `UnknownSpans`), `IListenerHistoryReader` with `ListenerTalliesAsync` and `ListenerFindingsAsync` (kinds `AgentStart`, `Agent`,
`AgentUidChanges`), `IAgentTimelineReader` with `GapsAsync` and `ClockAsync`, and the record writer, incident store and import tracker of
[Storage writing](storage-writing.md)). The reads are `ReaderQuery`, `TemplateQueries`, `ListenerQueries`, `AgentTimelineQueries`, `IncidentRepository` and `IncidentNoteRepository`. A
broken rule is an `InvalidQueryException`, a cancellation an `OperationCanceledException`, a missing string an `ArgumentNullException`.
