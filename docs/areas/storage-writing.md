# Storage writing

## Scope

How the backend writes into its database ([Storage](storage.md)): batches of records with their validation and deduplication, the
full-text index and the template statistics kept in the batch's transaction, the listener marks and comparisons, the agent timeline
(live or backfilled, gaps, clock), the template rebuild, the import state, and the saving of incidents and incident notes. The ingest
endpoint, the log import, `vandoxd templates`, `vandoxd incidents` and the incidents view rely on it.

## Behavior

### Batches

- A **batch** is written in one transaction. A batch holds at most 20,000 records and at least one, except the batch that completes an
  import file. It has a required receive time in UTC and, for records of origin `agent`, an agent ID that follows the agent ID rule of
  the [wire format](wire-format.md); the boot ID is stored as given. An import batch has no agent ID and every record has origin `import`.
- Every record is validated before anything is written (the payload size rule below is the exception: it is checked while the record
  is written, in the same transaction). A record is refused with a message that names the record's index and the rule when its capture
  time is outside 1677-09-21 to 2262-04-11 (the range of a signed 64-bit nanosecond count), its sequence number is above 2^63 − 1, or it
  is of origin `agent` in a batch without an agent ID.
- Invalid UTF-8 in strings that are stored as JSON (payloads, metric labels) is replaced by U+FFFD; log fields and the other metric
  fields are stored byte for byte. A record from an agent never contains invalid UTF-8, because the wire encoder replaces it and the
  decoder refuses a line that holds it ([Wire format](wire-format.md), *Common rules*).
- **JSON payloads** (every payload except a metric point and a log line) are serialized to UTF-8 in a bounded buffer and stored as text
  (bound as `CAST($data AS TEXT)`, because `records` is `STRICT` and refuses a blob in a text column). A payload whose JSON is larger than
  **16,777,216 bytes** (counted after escaping, exactly the stored bytes) is refused: the import skips the record with
  `record refused: data is larger than 16777216 bytes as JSON` (checked before the record is buffered, so a resumed import refuses it
  again), and any other batch is refused whole with `store: invalid batch: data is larger than 16777216 bytes as JSON` and nothing of it
  is stored. A record from an agent cannot reach the limit: its wire line has at most 1 MiB and re-serializing expands a byte by at most
  six. Memory per payload: the JSON as bytes (at most the limit), the buffer while it is built (at most about twice the limit; above the
  limit the bytes are dropped and the buffer is released) and SQLite's copies while it is inserted (about twice the JSON). A JSON payload
  of an import is serialized twice, once for the importer's size check and once by the writer.
- **Deduplication:** a record of origin `agent` is identified by (agent ID, sequence number) across all kinds. It is inserted with
  `ON CONFLICT DO NOTHING` and counted as a duplicate; a stored record is never replaced or updated. Records of origin `import` are not
  deduplicated, because log lines have no identity of their own; the import is idempotent per file instead ([Log import](log-import.md)).
- The rows of `listener_comparisons` and the agent timeline rows (below) are the only rows the batch write path ever updates; a
  `records` row never is.

### Full-text index

- The write path inserts the message into `log_fts` right after each `log_lines` row, in the same transaction, with no trigger.
  Invariant: every `log_lines` row has exactly one index entry with its message, written in the transaction that wrote the row.
- The write path of a batch is the only code that inserts into `log_lines`, so a log line is searchable as soon as its batch is
  committed. Any code that writes or deletes `log_lines` (a repair tool, retention) must maintain `log_fts` in the same transaction.

### Template statistics

- In the batch's transaction the write path counts every log line it stored under its template (normalization and rules in
  [Log templates](log-templates.md)): the template row's `count`, `first_seen` and `last_seen`, the template's hour and the source's hour.
  The counts of a batch are summed per template and hour first, so a batch runs one statement per template, template hour and source
  hour it touches; a batch without log lines runs none. A record that is not stored (a duplicate) is not counted.
- The template tables are written only by this path and the rebuild below; code that deletes log lines leaves them unchanged.

### Template rebuild

`vandoxd templates` rebuilds the statistics:

- A clear transaction empties the three template tables (hours first), writes the run marker (the row `template_rebuild` of `meta`,
  32 random hexadecimal characters) and reads the high-water ID (the highest record ID). Then the stored log lines up to it are counted in
  batches of at most 10,000 by record ID, one transaction each, with the write path's own counting. Every counting transaction first
  checks that the marker is its own; the finish transaction checks it again and removes it. Lines stored meanwhile have IDs above the
  high-water ID and are counted by the write path, so each line is counted once.
- Two runs at once: the one that cleared last wins, the other fails with `store: rebuilding templates: superseded by another rebuild`.
- An interrupted or failed run leaves the marker: the statistics are incomplete, and hours may hold **partial counts** (batches go by
  record ID, not by time), so they look covered with counts too low, not unknown. Every run starts over.
- The clear transaction grows with the statistics: it holds the writer lock that long (another process may hit its busy timeout and is
  run again) and writes the deleted pages to the WAL, which grows by about the size of the three tables until the next checkpoint.
- A batch size outside 1 to 20,000 is an argument error; database errors are `store: rebuilding templates: ` errors.

### Listener marks and comparisons

- A record of origin `agent`, source `connections`, with an agent ID in its batch and a connection snapshot that carries
  `listener_check`, `inventory` and at least one table is **usable**; any other record writes nothing of this.
- Right after the `records` insert of a usable snapshot returned an ID, in the same transaction, the write path inserts its row of
  `connection_marks`; a duplicate, which returns none, writes nothing, so a resent batch changes nothing.
- The new listeners are stored cut to the first 64 (the wire allows 4,096), and `new_count` counts the listed and the omitted ones (the
  agent's `new_listeners_omitted` plus the entries cut), so a read tells how many were left out. The agent's changed listeners are stored
  the same way, the first 64 and `changed_count` (listed plus `changed_listeners_omitted`), only for a check of `all` or `privileged`, each
  entry with its `command` emptied (the key stays, as `"command":""`); the user name is stored as on the wire.
- A mark of `listener_check` `start` is an **affected start**, and so is the agent's next mark by `seq` when it is a start (a snapshot
  stored later below a stored start changes what that start is compared with).
- The comparison of each affected start is computed **once per batch**, after the last record and before the commit, next to the
  template statistics: it reads the start's predecessor mark by `seq` (never by capture time), decodes the predecessor's and the start's
  stored payloads (two decodes per start, however many records of the batch lie below it), compares them by the rules of
  [Listener changes](listener-changes.md) and inserts or replaces the start's row of `listener_comparisons`, with the changed endpoints of
  the comparison (also with an empty `command`) and `finding` 1 when it has new or changed ones. Without a predecessor the outcome is
  `no_earlier`; a payload that is missing or does not decode makes the outcome `not_comparable` and never fails the batch, so a resend is
  never refused for it.
- The rows of a batch therefore equal the rows of the same records stored in any order or batches. The throughput effect is in
  `docs/BENCHMARKS.md` (*Storage write throughput*).

### Agent timeline

For a batch with an agent ID the write path decides, in the batch's own transaction, whether each agent record is live or backfilled
(the `live` column; rule and constants in [Detection, alerts and reports](detection-and-reports.md), *Live data and backfill*) and keeps
the agent's gaps, clock samples, sample times and stream row.

- Records of origin `import` or `backend`, and batches without an agent ID, are stored with `live` 0 and take no part. The header's
  `mode` is only logged, never stored, and its `clock_offset_ns` is stored as before; neither is used.
- **Sequence gaps.** Before the inserts the agent's records are walked in ascending `seq`. A number above `max_seq + 1` opens a sequence
  gap of the numbers between (cause `sequence_missing`, state `open`, `from_at` the capture time of the highest stored record, `to_at`
  the record's capture time) and restarts the wait. A number between `floor_seq` and `max_seq` is removed from the gap that contains it:
  the gap shrinks, splits in two (a split-off rest keeps the cause and state of its gap) or is `closed` when nothing is left, and a record
  arriving into an `open` gap restarts the wait. Consecutive numbers of the batch are applied as one run, one indexed seek per gap the run
  touches, and each changed row is written in place.
- An agent `gap` record that names numbers makes the part of the open sequence gaps inside `[first_seq, min(last_seq, its own seq - 1)]`
  `final` with its cause; the clip is done in unsigned numbers, so a range above 2^63 − 1 finalizes nothing and costs nothing.
- **The wait.** The first batch stored from an agent since the store was opened restarts it, and a batch that filled no open gap and
  arrives **5 minutes** or more after it makes every open sequence gap of the agent `final` with one statement (`changed_at` that batch's
  receive time). Only then is each record classified, with the lowest open sequence gap that remains after the whole batch, so a batch
  that closes the last gap below its records is live by lateness alone.
- **Time gaps.** After the inserts, each distinct capture time of the batch's stored agent `metric` records gets a row of
  `agent_sample_times`. A time not seen before narrows, splits or closes the time gap that contains it (a part of at most 2 minutes is
  dropped), else creates a gap to the previous and to the next stored time when that span is over 2 minutes (cause `no_data`, stored
  `open`, with the raw capture times). A time gap is stored `open` or `closed` only; the readers report a not-closed time gap as `final`
  when no open sequence gap overlaps its bounding numbers, so no batch re-evaluates other gaps.
- The clock rows are written before the inserts and the stream row last.
- **Bounded work.** A stored record adds at most three gap rows (two unless it is an agent `gap` record), each found by index seeks
  (every statement's query plan is a search, checked by a test), so the work per record does not grow with the number of open gaps. All
  differences of instants are computed in 128-bit integers and stored bounds are saturated, so no capture time or number can make the
  write fail.
- A refused or rolled-back batch changes none of it, and an agent counts as written since the start only after the commit, so a refused
  first batch does not start the wait. Two batches of one agent give the result of their commit order (the writer lock). The throughput
  effect is in `docs/BENCHMARKS.md` (*Storage write throughput*).

### Import state

- **Import batches** carry the import state: in the same transaction as its records, a batch advances the file's `records` counter with
  a compare-and-set on the expected count (and sets the complete flag with the last batch). If the counter does not match, nothing is
  written and the caller gets a conflict. A batch with an attempt above 0 also requires the file's current attempt; attempt 0 means no
  line hashes. Completing a file sets `hashes_attempt` to the batch's attempt (`NULL` for attempt 0) and, in the same transaction,
  deletes the line-hash rows of every other attempt (all rows for attempt 0).
- **Line hashes:** adding a row validates it (offsets, 1 to 4,096 hashes of 16 bytes, no more hashes than bytes covered) and inserts it
  in its own transaction only while the file is incomplete and its current attempt is the row's, otherwise the caller gets a conflict.
  Beginning an import of an incomplete content raises its attempt by one and deletes, in the same transaction, the line-hash rows of
  every other attempt of that file, so an incomplete file keeps the rows of one attempt at most.
- **Import bases:** beginning an import with prefixes (at most 64) picks, in the same transaction, the largest prefix whose content is
  stored complete with the same size and source type, and keeps it as the new row's continued content and continuation point; an
  existing row keeps its continuation. The lookup of complete contents by head key and source type returns the SHA-256 and size, largest
  first, at most 64; rows without a head key are never returned.
- **Import containment:** beginning an import of a content that has no row first looks for a complete row of the same head key and
  source type that is larger, using the 64 largest, largest first (the limit applies to the candidates, whether or not they have line
  hashes). A candidate is joined to its one line-hash row of `hashes_attempt` that spans the content's size; the candidate contains the
  content when that row holds the first 16 bytes of its SHA-256. Then no row is created and the caller gets the candidate's display
  name. The lookup runs before the choice of a base.

### Incidents

- Saving an incident takes the writer lock and one immediate transaction. A missing incident is an argument error.
- The incident is validated first, with these exact errors: `store: invalid incident: start and end must be within the storable
  range`, `start must be before end`, `the first kill must lie in the window` (at or after the start and before the end),
  `kill count must be at least 1`, `saved at must be within the storable range` and `data is larger than 8388608 bytes` (the UTF-8 bytes
  of the serialized JSON).
- It is serialized with one fixed set of options, compact without indentation, the same the incident builder measures its timeline with
  ([Incidents](incidents.md)).
- The stored incidents whose window overlaps the new one (`started_at < end` and `ended_at > start`, so windows that only touch do not
  overlap) are selected oldest number first. None: a row is inserted (`created_at = updated_at =` the save time). Otherwise the lowest
  number is kept and the others are deleted, and the number of deleted ones is reported as merged; **the notes of each deleted incident
  are first moved to the kept one in the same transaction, each keeping its `written_for`**. When nothing was deleted and window, first
  kill, kill count and data are equal, the row is left untouched (`unchanged`, `updated_at` too); else every column but `id` and
  `created_at` is replaced. An update and an unchanged save keep the notes of the incident.

### Incident notes

- Saving a note takes the writer lock and one immediate transaction; it is the first write of the web UI
  ([Incidents view](web-ui-incidents-view.md)). The incident must exist, else the outcome is *not found* and nothing changes.
- A note is written for one incident number (`written_for`) and belongs to one incident (`incident_id`). The note of an incident is the
  one with `written_for` equal to its number; the notes moved to it by a merge are the others.
- A non-empty text with `written_for` equal to the incident upserts (`INSERT … ON CONFLICT(written_for) DO UPDATE … WHERE
  incident_notes.incident_id = excluded.incident_id`; a conflict with the row of another incident updates nothing and is *not found*); with
  another `written_for` it updates only a row of that incident, else *not found*. An **empty text deletes** the row of `written_for` and
  the incident (*deleted*, also when there was none for the incident's own number). The outcomes are *saved*, *deleted* and *not found*.
- The edit is validated first, with these exact errors: `store: invalid note: incident ID must be positive`, `written for must be
  positive`, `text is longer than 1800 characters` (UTF-16 characters, 1,800 accepted), `text must not contain U+0000`, `text is not valid
  UTF-16` and `saved at must be within the storable range`; a missing edit or text is an argument error. U+0000 is refused because
  `length()` ends at it, so a character check of the table would let a NUL hide characters; the table checks the bytes whatever the text
  holds. A database error is `store: saving note: …`.
- A note of an incident survives every re-run of `vandoxd incidents` that updates or keeps the incident, and follows it when it is merged.
  Retention must move or keep notes, as an incident with notes cannot be deleted ([Storage](storage.md), *Retention*).

## Limits and errors

The limits and exact error texts are stated with each rule above. A refused batch stores nothing of it.

## Related decisions

- [0063](../decisions/0063-storage-schema-records-table-typed-metric-and-log-tables-json-payloads.md) — why the write path fills the FTS5 index.
- [0083](../decisions/0083-storage-write-criterion-is-the-median-of-five-batches-on-the-data-volume.md) — why the writer was made cheaper.
- [0095](../decisions/0095-log-templates-token-rules-statistics-in-the-write-path-usual-level-from-covered-hours.md) — why the write path keeps the template statistics in the batch's transaction.
- [0096](../decisions/0096-outage-incidents-rebuilt-from-stored-data-merged-by-overlapping-windows.md) — why incidents are matched by overlapping windows, kept per number and saved one transaction each.
- [0107](../decisions/0107-incidents-view-summaries-notes-per-incident-number-time-links-to-the-logs.md) — why notes are rows per incident number moved in the merge.
- [0116](../decisions/0116-listeners-across-agent-starts-compared-when-stored-and-shown-in-the-forensics-report.md) — why the write path stores a mark per agent connection snapshot and the comparison of each agent start, computed once per batch.
- [0130](../decisions/0130-live-or-backfilled-at-write-time-gaps-as-stateful-objects-offset-from-caught-up-self-samples.md) — why the write path classifies each agent record, keeps gaps as stateful rows and measures the clock offset.

## Not here

- The database file, connections, schema, migrations, retention and the throughput criterion: [Storage](storage.md).
- Reading, log search and log exploration: [Storage reading and log exploration](storage-reading.md).
- Template normalization: [Log templates](log-templates.md); the listener comparison rules: [Listener changes](listener-changes.md);
  the live rule and its constants: [Detection, alerts and reports](detection-and-reports.md); incident building: [Incidents](incidents.md).
- The acknowledgement of a batch: [Ingest endpoint](ingest-endpoint.md); how the import uses its state: [Log import](log-import.md).

## Implementation

`Vandox.Storage`: `SqliteStore` (`WriteBatchAsync`, `RebuildTemplatesAsync`, `SaveIncidentNoteAsync`), `BatchWriter`, `BatchValidator`,
`TemplateTally`, `TemplateRebuilder`, `ListenerMarkWriter` (comparison by `ListenerComparer` of `Vandox.Core`; the decode count is checked by the test
`PayloadDecodes`), `AgentTimeline`, `LiveRules`, `ClockEstimator`, `IncidentRepository` (`InvalidIncidentException`),
`IncidentNoteRepository` (`InvalidNoteException`), `StorageLimits.MaxPayloadBytes`, `PayloadJson` and `BoundedBufferWriter`; both
validation exceptions are `StoreException`s.
