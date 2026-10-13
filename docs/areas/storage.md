# Storage

## Scope

The backend's database: the file and how it is opened, the schema with its migrations, how batches of records are written, how
they are read and searched, and the write-throughput criterion. A database file written by one version of the backend must stay
readable by the next, whichever language implements it, so the schema below is part of the contract. What a record means is in the
[wire format](wire-format.md); how the log import tracks files on top of this store is in [Log import](log-import.md).

## Database file and connections

- The backend keeps all data in one SQLite database, `vandox.db` in `storage.directory`, with FTS5 for log search. There is no external
  database. A backup is a copy of the file (together with its `-wal` and `-shm` files).
- The file is created exclusively with mode `0600`. A symbolic link or any other entry that is not a regular file in place of
  `vandox.db`, `vandox.db-wal` or `vandox.db-shm` is refused, because SQLite would write next to the link target, outside
  `storage.directory`.
- The database runs in WAL mode with `foreign_keys` on and **`synchronous=FULL`**: a committed batch survives a power cut. The
  ingest path acknowledges a batch only after its commit, and the agent then deletes it from its spool, so an acknowledged batch must
  not become a silent gap.
- **One writer connection**, whose transactions start with `BEGIN IMMEDIATE` (the lock is taken at the start, so the busy timeout
  applies instead of a failure in the middle of a transaction), and up to four **query-only reader connections**. Readers never wait
  for the writer. The busy timeout is 5 seconds; it matters only against another process that opens the same file, such as
  `vandoxd import` or a backup. The writer has a page cache of up to 16 MiB; readers keep the default.
- A **cancelled token or a passed deadline interrupts a running reader statement.** Every reader connection gets a SQLite progress
  handler bound to the token of its call, checked every 1,000 virtual-machine instructions, so it also stops the work SQLite does
  before the first row (a sort, an aggregate, the `MATCH` of a search). The call then ends with `OperationCanceledException` carrying
  that token and no partial result; every other SQLite error stays a `StoreException` with the message of its operation. A token
  that cannot be cancelled installs no handler. Work inside one instruction (for example one call into the FTS5 module) finishes
  before the next check, and the bound applies while the statement runs, not while it waits for a lock. The writer connection is never
  interrupted: a batch, an incident save, an import step and a migration run to their commit or rollback.
- Two processes may open the database at once.

## Schema

The schema version is stored in `meta.schema_version`; the current version is 9 (step 9 adds the table `incidents` with its index, empty; step 8 adds the tables `log_templates`,
`log_template_hours` and `log_source_hours` with their indexes, empty, so log lines stored before it have no template statistics until `vandoxd templates` rebuilds them; step 7 adds `import_files.attempt`, `INTEGER NOT NULL DEFAULT 0`, and `import_files.hashes_attempt`, `INTEGER`, and the table `import_line_hashes`, so existing rows get attempt 0 and no line hashes; step 6 adds `import_files.head_sha256`, `continues_id` and `start_offset` and the index `import_files_head`; step 4 adds `log_lines.host`, `TEXT NOT NULL DEFAULT ''`, so lines stored before it get an empty host; step 5 adds `log_lines.event`, `TEXT NOT NULL DEFAULT ''`, so lines stored before it get an empty event). Times are Unix nanoseconds in `INTEGER` columns.
All tables are `STRICT`.

| Table | Content |
| ----- | ------- |
| `meta` | key and value; the schema version, and the run marker `template_rebuild` from the start of a template rebuild until one completes |
| `records` | one row per record: `id`, `kind`, `origin` (`agent`, `backend` or `import`), `source`, `agent_id`, `seq`, `captured_at`, `received_at`, `boot_id`, `clock_offset_ns`, `data` (the record's payload as the `data` object of the wire format, without the kind, which is the `kind` column, for every kind except metrics and log lines; `NULL` there) |
| `metrics` | typed rows for metric records: `record_id`, `source`, `name`, `captured_at`, `value`, `unit`, `labels` (JSON object or `NULL`) |
| `log_lines` | typed rows for log lines: `record_id`, `log`, `program`, `pid`, `priority`, `message`, `truncated`, `host`, `event` |
| `log_fts` | FTS5 index over `log_lines.message` (external content, tokenizer `unicode61 remove_diacritics 2`) |
| `import_files` | one row per imported file content: SHA-256 of the decompressed content (unique), size, display name, file name and modification time of the first import, source type, number of records stored, complete flag, start and completion times, the head key (SHA-256 of the first line; `NULL` for rows of versions before 6), the id of the content it continues (no foreign key) and the continuation point in bytes (0 when it is not continued), the attempt (the number of times an incomplete content was begun; 0 for rows of versions before 7) and the attempt whose line hashes are kept (`hashes_attempt`, `NULL` when the content was completed without line hashes) |
| `log_templates` | one row per log template: `id`, `source` (the record's source), `program`, `template` (the normalized message, at most 1,024 UTF-8 bytes), `first_seen` and `last_seen` (the earliest and latest capture time of its lines), `count` (its stored lines, at least 1); unique on `(source, program, template)` |
| `log_template_hours` | `template_id`, `hour` (the start of a UTC hour), `count` (the template's stored lines in that hour, at least 1); primary key `(template_id, hour)`, without row ID |
| `log_source_hours` | `source`, `hour`, `count` (the stored log lines of the source in that hour, at least 1); primary key `(source, hour)`, without row ID |
| `incidents` | one row per incident: `id` (`AUTOINCREMENT`, so a number that was merged away is never given again), `started_at` and `ended_at` (the window, `ended_at > started_at`), `first_kill_at`, `kill_count` (at least 1), `created_at`, `updated_at` and `data` (the incident as JSON, at most 8,388,608 UTF-8 bytes) |
| `import_line_hashes` | rows of line hashes of an imported content: `file_id`, `attempt`, `first_offset`, `last_offset` and `hashes` (a blob of 16-byte entries, 1 to 4,096, in ascending order of their line ends); the primary key `(file_id, attempt, last_offset)` is its index |

Indexes: a unique index on `records(agent_id, seq)` for origin `agent`; `records(kind, source, captured_at)`;
`records(kind, captured_at)`; `metrics(source, name, captured_at)`; `import_files(head_sha256, source_type, size)` for complete rows with a head key (`import_files_head`);
`log_templates(source, first_seen)` (`log_templates_source_first_seen`); `log_template_hours(hour)` (`log_template_hours_hour`); `incidents(started_at)` (`incidents_started_at`).

- `agent_id`, `seq`, `boot_id` and `clock_offset_ns` are `NULL` when absent (`seq` for any origin other than `agent`). `source` and
  `captured_at` of a metric are copied from `records`, so one index serves a time series in order.
- **Migrations** are an ordered list of steps in code. A step runs in its own write transaction that first re-reads the version,
  skips itself if the database is already at or past it, and otherwise executes and writes the new version in the same transaction;
  a failing step rolls back completely and earlier steps stay committed. A missing `meta` table or version row is version 0. A
  version above the last known step, or one that is not a decimal integer, is refused before anything is written. A released step is
  never edited: every schema change is a new step, tested by migrating from the previous version. Two processes that start at once
  apply each step once. An older build refuses a newer schema.

## Writing

- A **batch** is written in one transaction. A batch holds at most 20,000 records and at least one, except the batch that completes an
  import file. The batch has a required receive time in UTC and, for records of origin `agent`, an agent ID that follows the agent ID
  rule of the wire format; the boot ID is stored as given.
- Every record is validated before anything is written (the size rule of the next bullet is the exception: it is checked while the
  record is written, in the same transaction). A record is refused with a message that names the record's index and the rule
  when its capture time is outside 1677-09-21 to 2262-04-11 (the range of a signed 64-bit nanosecond count), its sequence number is
  above 2^63 − 1, or it is of origin `agent` in a batch without an agent ID. An import batch has no agent ID and every record has
  origin `import`. Invalid UTF-8 in strings that are stored as JSON (payloads, metric labels) is replaced by U+FFFD; log fields
  and the other metric fields are stored byte for byte. A record from an agent never contains invalid UTF-8, because the wire encoder
  replaces it and the decoder refuses a line that holds it ([Wire format](wire-format.md), *Common rules*).
- A **JSON payload** (every payload except a metric point and a log line) is serialized to UTF-8 in a bounded buffer and stored as
  text (the insert binds the bytes with `CAST($data AS TEXT)`, because `records` is `STRICT` and refuses a blob in a text column). A
  payload whose JSON is larger than 16,777,216 bytes (`StorageLimits.MaxPayloadBytes`, counted after escaping, exactly the stored
  bytes) is refused: the import skips the record with `record refused: data is larger than 16777216 bytes as JSON` (the check runs
  before the record is buffered, so a resumed import refuses it again), and any other batch is refused whole with `store: invalid
  batch: data is larger than 16777216 bytes as JSON` and nothing of it is stored (the exception leaves the transaction before the
  commit). A record from an agent cannot reach the limit: its wire line has at most 1 MiB and re-serializing expands a byte by at
  most six. Memory per payload: the JSON as bytes (at most the limit), the buffer while it is built (at most about twice the limit;
  above the limit the bytes are dropped and the buffer is released) and SQLite's copies while it is inserted (about twice the
  JSON). A JSON payload of an import is serialized twice, once for the size check of the importer and once by the writer; this is a
  deliberate cost that keeps the check free of copies.
- **Deduplication:** a record of origin `agent` is identified by (agent ID, sequence number) across all kinds. It is inserted with
  `ON CONFLICT DO NOTHING` and counted as a duplicate; a stored record is never replaced or updated. Records of origin `import` are not
  deduplicated, because log lines have no identity of their own; the import is made idempotent per file instead (*Log import*).
- **Log lines:** the write path inserts the message into `log_fts` right after each `log_lines` row, in the same transaction, with no
  trigger. Invariant: every `log_lines` row has exactly one index entry with its message, written in the transaction that wrote the row.
  The write path of a batch is the only code that inserts into `log_lines`, so a log line is searchable as soon as its batch is
  committed. Any code that writes or deletes `log_lines` (a repair tool, retention) must maintain `log_fts` in the same transaction.
- **Log templates:** in the same transaction, the write path counts every log line it stored under its template (normalization and
  rules in [Detection, alerts and reports](detection-and-reports.md), *Log templates*): the template row's `count`, `first_seen` and
  `last_seen`, the template's hour and the source's hour. The counts of a batch are summed per template and hour first, so a batch
  runs one statement per template, template hour and source hour it touches; a batch without log lines runs none. A record that is not
  stored (a duplicate) is not counted. The template tables are written only by this path and the rebuild below; code that deletes log lines leaves them
  unchanged.
- **Template rebuild** (`vandoxd templates`, `SqliteStore.RebuildTemplatesAsync`): a clear transaction empties the three template tables
  (hours first), writes the run marker (the row `template_rebuild` of `meta`, 32 random hexadecimal characters) and reads the
  high-water ID (the highest record ID), then the stored log lines up to it are counted in batches of at most 10,000 by record ID, one
  transaction each, with the write path's own counting. Every counting transaction first checks that the marker is its own; the finish
  transaction checks it again and removes it. Lines stored meanwhile have IDs above the high-water ID and are counted by the write path,
  so each line is counted once. Two runs at once: the one that cleared last wins, the other fails with `store: rebuilding templates:
  superseded by another rebuild`. An interrupted or failed run leaves the marker: the statistics are incomplete, and hours may hold
  **partial counts** (batches go by record ID, not by time), so they look covered with counts too low, not unknown; every run starts
  over. The clear transaction grows with the statistics: it holds the writer lock that long (another process may hit its busy timeout
  and is run again) and writes the deleted pages to the WAL, which grows by about the size of the three tables until the next
  checkpoint. A batch size outside 1 to 20,000 is an `ArgumentOutOfRangeException`; database errors are `store: rebuilding templates: `
  errors.
- **Import batches** carry the import state: in the same transaction as its records, a batch advances the file's `records` counter with
  a compare-and-set on the expected count (and sets the complete flag with the last batch). If the counter does not match, nothing is
  written and the caller gets a conflict. A batch with an attempt above 0 also requires the file's current attempt; a batch with
  attempt 0 means no line hashes. Completing a file sets `hashes_attempt` to the batch's attempt (`NULL` for attempt 0) and, in the
  same transaction, deletes the line-hash rows of every other attempt (all rows for attempt 0).
- **Line hashes:** adding a row validates it (offsets, 1 to 4,096 hashes of 16 bytes, no more hashes than bytes covered) and inserts it
  in its own transaction only while the file is incomplete and its current attempt is the row's, otherwise the caller gets a conflict.
  Beginning an import of an incomplete content raises its attempt by one and deletes, in the same transaction, the line-hash rows of
  every other attempt of that file, so an incomplete file keeps the rows of one attempt at most.
- **Import bases:** beginning an import with prefixes (at most 64) picks, in the same transaction, the largest prefix whose content
  is stored complete with the same size and source type, and keeps it as the new row's continued content and continuation point; an
  existing row keeps its continuation. The lookup of complete contents by head key and source type returns the SHA-256 and size,
  largest first, at most 64; rows without a head key are never returned.
- **Import containment:** beginning an import of a content that has no row first looks for a complete row of the same head key and
  source type that is larger, using the 64 largest, largest first (the limit applies to the candidates, whether or not they have line hashes). A candidate is joined to its one line-hash row of `hashes_attempt`
  that spans the content's size; the candidate contains the content when that row holds the first 16 bytes of its SHA-256. Then no
  row is created and the caller gets the candidate's display name. The lookup runs before the choice of a base.

- **Incidents:** saving an incident takes the writer lock and one immediate transaction. The incident is validated first, with these
  exact errors (`InvalidIncidentException`, a `StoreException`): `store: invalid incident: start and end must be within the storable
  range`, `start must be before end`, `the first kill must lie in the window` (the first kill is at or after the start and before the
  end), `kill count must be at least 1`, `saved at must be within the storable range` and `data is larger than 8388608 bytes` (the
  UTF-8 bytes of the serialized JSON). It is serialized with one fixed set of options, the default encoder without indentation, which
  is also what the incident builder measures its timeline with. The stored incidents whose window overlaps the new one (`started_at <
  end` and `ended_at > start`, so windows that only touch do not overlap) are selected oldest number first: none inserts a row
  (`created_at = updated_at =` the save time); otherwise the lowest number is kept and the others are deleted (the number of deleted
  ones is reported as merged), and when nothing was deleted and window, first kill, kill count and data are equal the row is left
  untouched (`unchanged`, `updated_at` too), else every column but `id` and `created_at` is replaced. A null incident is an argument
  error.

## Reading

- A series is read by kind, source and name for a time range, in time order; a kind-wide time range uses the kind indexes. A query
  returns at most 10,000 records.
- A series or a kind can be paged through records that share a capture time with `AfterId`: when it is above 0, records whose capture
  time equals `From` and whose ID is at most `AfterId` are skipped (the next page's `From` is the last capture time and `AfterId` its
  ID); a negative value is refused with `store: invalid query: after id must not be negative`. This holds in whole 100 ns, because
  every writer stores capture times in whole `DateTimeOffset` ticks, so the capture time a reader returns equals the stored one; a
  caller that pages must check that each page leads past the previous one.
- A stored payload is read from the column's UTF-8 bytes, not from a string, so the payload exists once as bytes and once as the
  parsed document; the results are the same.
- A stored payload is read with the payload options of the wire decoder, so an unknown member is ignored; this includes the `Kind`
  member that rows written before #183 carry.
- Incidents are read by range (`started_at < to` and `ended_at > from`, ordered by start, then number) with the window and limit rules
  of the record query, and counted; a `data` value that does not decode (including bytes that are not valid UTF-8 in a member the incident reads; such bytes in an unknown member are skipped unchecked) is `store: reading incidents: decoding incident data`.
  The `data` column is read from its UTF-8 bytes, like a payload. A page holds every returned incident decoded at once: in the worst
  case up to 10,000 incidents per page, each decoded from up to 8 MiB of JSON, so the memory of a read grows with the limit. A caller
  that serves a user pages with a small limit (open limit for #27, which adds the first caller).
- The template statistics are read by the queries of *Log templates* in [Detection, alerts and reports](detection-and-reports.md)
  (template of a line, hourly counts, new templates, templates above their usual level), with the same time range and limit rules,
  on a reader connection. The cost of the usual-level query is not bounded by its range or limit; a cancelled token or deadline interrupts it
  (*Database file and connections*; *Limits of templates* there).
- Callers depend on small interfaces (record writer, record reader, log searcher, log explorer, log template reader, incident store, import tracker). The real store is tested against
  real files; a consumer whose behavior depends on the store's semantics (such as "a resent batch is stored once") is tested against
  the real store. Fakes for other consumers only record calls and return what the test scripts.

### Log search

A search takes literal terms only; the text is never passed to FTS5 as a query.

- The text must be valid UTF-8 of at most 1,024 bytes, without a control or format character other than white space. It is split at
  white space into 1 to 16 terms, and every term needs at least one letter or decimal digit.
- Each term is quoted as an FTS5 string (a `"` inside doubles) and the terms are joined with a space, so all terms must match
  and no FTS5 operator, prefix (`*`), column filter or `NEAR` has any effect. The expression is bound as a parameter, never
  concatenated into SQL.
- Matching is case-insensitive and ignores diacritics; a term with punctuation (`anon-rss`) matches the tokens in order as a phrase.
  `ß` is not folded to `ss`. A term that consists only of numbers that are not decimal digits (`½`, `Ⅻ`) or of private-use characters is
  refused although the tokenizer would index it.
- A violated rule is an error that names the rule and never contains the search text.
- The cost of a search is not bounded by the time range or the limit: SQLite runs the `MATCH` over the whole retention first and filters
  by time and source afterwards. The bound is time: a cancelled token or a passed deadline interrupts the
  running search (*Database file and connections*), so every caller that serves a user must pass a deadline. The logs view does not
  use this search: it searches through *Log exploration* below, which bounds the work of every request.

### Log exploration

The logs view ([Web UI](web-ui.md), *Logs view*) reads through a second interface, `ILogExplorer`, which `SqliteStore` implements next to the
log search above; the log search keeps its rules for its other callers. Every method runs on a reader connection and is interrupted by a
cancelled token or a passed deadline (*Database file and connections*), with no partial result, except for the one case below that the
deadline cannot interrupt. Null strings are an `ArgumentNullException`; a broken rule is an `InvalidQueryException` (a `StoreException`)
whose message is `store: invalid query: ` and the rule, and never contains the search text.

**Finding lines.** A request has a text, a source, a program, a highest priority, a template number, a time range `[From, To)`, an optional
position `After` (capture time and record ID), a page size `Limit` and a bound `ScanLimit`.

- Every filter given must hold: all words of the text (the rules of *Log search*), the source (exact), the program (exact, ordinal), the
  priority at most the given one (a line without a priority is then not returned), the capture time in the range. An empty text applies the
  other filters only. The lines come **oldest first, by capture time, then record ID**: that is the order of the log, not of the record
  IDs, because imported lines are not stored in time order.
- A page holds at most `Limit` lines. `Next` is the position (capture time, record ID) of the page's last line when the page is full,
  and a request with `After` set to it continues with the next line, without a gap or a repeat, also across lines with the same capture
  time. `Next` is empty at the end of the range.
- **Two strategies, chosen per request from the full-text index alone.** First the record IDs of the matching rows are read from the index up to a cap of
  **12,000** (`SelectiveMatches`), without looking at the lines. None: the page is empty and nothing more is read. Fewer than the cap:
  **index mode**, which reads the lines with exactly those record IDs (a lookup by primary key; the index is not read again), applies the
  filters and sorts them (at most the cap, by construction). The cap or more, or no text: **scan mode**, which walks the log
  lines of the range in capture-time order from the position (through the indexes on kind, source and time, or kind and time, with a
  source) and keeps the lines that pass the filters and, with a text, are in the full-text match. The match is tested for each examined
  line on its own, one lookup per term, and nothing is collected, so scan mode holds at most the page and its work grows with the
  examined lines, not with the matches. The result of a request does not depend on the strategy.
- **The scan bound:** scan mode examines at most `ScanLimit` log lines (1 to 100,000; the logs view uses **12,000**), counted whether or not
  they match. When the bound ends the request before the page is full, `ScanLimitReached` is set and `Next` is the position of the last
  examined line, also when the page is empty: a request with `After` set to it searches on, so continuing until `Next` is empty
  returns every matching line exactly once. A full page ends the scan at its last line (`ScanLimitReached` not set).
- **Template lines:** with a template number the lines are those of the template's source and program whose message normalizes (the rules of
  [Detection, alerts and reports](detection-and-reports.md), *Log templates*) to the template's text, exactly. The words of the template
  that contain no placeholder narrow the candidates: they are added to the text as search terms (at most 16 terms and 1,024 bytes in all,
  the user's words first; a template longer than 1,020 bytes loses its last word, which a cut may have split; a template without such a
  word uses scan mode). Every candidate's message is normalized and kept only when it equals the template's text; in scan mode a rejected
  line counts as examined. A source or program filter that differs from the template's gives no lines. An unknown number is
  `store: invalid query: unknown template`.
- **Rules**, each refused with `store: invalid query: ` and: `from and to are required`, `from and to must be within the storable range`,
  `from must be before to`, `limit must be between 1 and 1000`, `scan limit must be between 1 and 100000`, `source is longer than 1024
  bytes`, `program is longer than 1024 bytes`, `priority must be between 0 and 7`, `template ID must not be negative`, `after must be within
  the storable range`, `after id must be positive`, and the rules of *Log search* for a non-empty text.
- **Cost.** Index mode reads at most the cap of matches. Scan mode reads at most `ScanLimit` lines and does one index lookup per term for
  each. The read of the matching IDs runs inside the full-text module: **a text of several common words makes it walk their whole entries to find
  the words' intersection in one step, which the deadline cannot interrupt** (*Database file and connections*). That work is bounded by
  the size of the words' index entries (index only, at most 16 terms), not by the deadline, and is measured in `docs/BENCHMARKS.md`
  (*Log search*). A search without a text and with a program or source that few lines have examines up to `ScanLimit` lines per request
  and says how far it got.

**Context.** `LogContextAsync` returns a line and up to `Before` lines before and `After` lines after it (0 to 20 each) with the same
source, log name and host, in capture-time order, whatever their other fields; lines of other logs of the source are skipped. Each side
examines at most **2,000** lines of the source and returns fewer when that bound or the data ends. Errors: `record ID must be positive`,
`context lines must be between 0 and 20`, `unknown log line` (an ID that is not a stored log line).

**Sources, templates, hours.**

- `LogSourcesAsync`: the distinct sources of stored log lines in ordinal order, at most 64 (one index seek per source).
- `TemplateAsync(id)`: the template or none (`template ID must be positive`).
- `TemplatesAsync`: the templates with at least one line in the hours that start in the range, optionally of one source and one program,
  each with its number of lines in those hours, most lines first, then lowest number, at most `Limit` (1 to 1,000: `limit must be
  between 1 and 1000`; the window and length rules as above). The count is kept per UTC hour, so a `From` or `To` inside an hour counts that
  **whole hour**. The hours are summed per template in key order (the range conditions are on the hour column without using the index on it), then joined to the templates, so the statement reads the whole table of template hours whatever the range is: its cost grows with the stored template hours (about 0.03 s for 374,000 template hours), not with the range.
- `SourceHoursAsync`: the start of every hour in the range in which a source has lines, ascending (`source is required`; limit 1 to 10,000,
  `limit must be between 1 and 10000`); `TemplateHoursAsync`: a template's hourly counts, as the template reader's hourly counts.
  Both come from the template statistics, so lines stored before them (a database upgraded from schema 7) have none until `vandoxd
  templates` has run.

**Storage layout.** No table, column or index was added for this; scan mode relies on the indexes `records(kind, source, captured_at)` and
`records(kind, captured_at)` serving the order of the scan, so a statement plan that stops using them would turn a page into a sort of
every log line of the range (the benchmark shows it).

## Retention (planned)

Raw data is kept 30 days, 5-minute rollups 1 year, hourly rollups 3 years (the template statistics count as hourly rollups), incidents
unlimited, logs 90 days, process and connection snapshots 30 days. Incidents are kept without limit, and an incident that no rebuilt one overlaps is kept as it is (*Incidents* in [Detection, alerts and reports](detection-and-reports.md)). Open limit for #46: once retention deletes lines, a re-run rebuilds an incident whose window lines have partly expired from what is left and replaces the richer stored one (fewer kills, a shorter timeline); retention must prevent that (keep the data of incident windows, or stop rebuilding incidents older than the log retention). Forged incidents (up to 8 MiB each) outlive the lines they came from, so retention must bound them, too. Open limit for #46: a template rebuild counts only the lines still stored, so after retention it would lose the history of expired lines (retention must make the rebuild refuse or keep that history). Retention and rollups are the backend's own job and **are not implemented yet**; deleting log lines must also remove
their `log_fts` entries (the FTS5 `delete` command with the old message).

## Write throughput

The reference host is a Synology DS918+ (Intel Celeron J3455). The criterion: the **median of five batches** of 10,000 records, one
transaction each, takes under one second, measured with `tools/Vandox.StorageBenchmark` on the volume that holds the database; the
first batch (fresh file) is reported next to it. The criterion is a stress value, since an agent sends hundreds of records per minute.
It is met on the SSD volume (783 ms median); on the HDD volume it is not (1,171 ms), because every commit waits for the disk, so the
database should be on the SSD, though any volume works. Results and the procedure are in `docs/BENCHMARKS.md`. The criterion is not
weakened silently, and `synchronous=FULL` stays unless the Product Manager decides otherwise in a new record.

## Related decisions

- [0007](../decisions/0007-sqlite-with-fts5-no-external-database.md) — why one SQLite file with FTS5 and no external database.
- [0063](../decisions/0063-storage-schema-records-table-typed-metric-and-log-tables-json-payloads.md) — why a `records` table plus typed metric and log tables, and why the write path fills the FTS5 index.
- [0066](../decisions/0066-log-search-takes-literal-terms-only.md) — why log search takes literal terms only.
- [0077](../decisions/0077-storage-on-microsoft-data-sqlite-same-schema-and-rules.md) — why the same schema on Microsoft.Data.Sqlite, one writer, `synchronous=FULL` and migrations in code.
- [0083](../decisions/0083-storage-write-criterion-is-the-median-of-five-batches-on-the-data-volume.md) — why the writer was made cheaper and why the criterion is the median of five batches.
- [0095](../decisions/0095-log-templates-token-rules-statistics-in-the-write-path-usual-level-from-covered-hours.md) — why the write path keeps the template statistics in the batch's transaction.
- [0096](../decisions/0096-outage-incidents-rebuilt-from-stored-data-merged-by-overlapping-windows.md) — why incidents are matched by overlapping windows, kept per number and saved one transaction each, and why the record query pages by ID.
- [0097](../decisions/0097-reader-statements-interrupted-by-a-progress-handler-bound-to-the-token.md) — why a progress handler interrupts reader statements and why the interruption is an `OperationCanceledException`.
- [0103](../decisions/0103-logs-view-address-as-the-view-bounded-search-pages-template-lines-by-normalization.md) — why the logs view's search chooses between the index and a bounded scan and finds a template's lines by normalizing candidates.

## Not here

- The meaning of the records and the batch format: [Wire format](wire-format.md).
- How import files are found, hashed and resumed: [Log import](log-import.md).
- The container's volumes and memory limit: the ingest and backend host area.

## Implementation

`Vandox.Storage` (`SqliteStore`, `SchemaMigrator`, `BatchWriter`, `BatchValidator`, `FtsQuery`, `TemplateTally`, `TemplateRebuilder`, `TemplateQueries`, `IncidentRepository`, `ReaderQuery`, `LogFinder`, `LogFinderQueries`, `LogCriteria`, `LogFilterRules`, `LogExplorerQueries`, `TemplateTerms`, `StorageLimits`).
