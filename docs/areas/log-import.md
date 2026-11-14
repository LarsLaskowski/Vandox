# Log import

## Scope

The `import` sub-command of `vandoxd`, with which the operator loads saved server logs into the backend: its input,
safe file access, repeatability, the contract every log parser fulfills, and its report. Each parser's own rules are
in the documents under *Not here*.

## Command

- `vandoxd [flags] import [-config <file>] <path>` imports the input at `<path>`. `-config` is accepted before and
  after `import`. The command takes exactly one path. `-h` prints its usage and exits 0. `-healthcheck` together
  with `import` is a usage error.
- In the container: `docker exec -it vandoxd dotnet /app/vandoxd.dll import /import/<name>` (`-it` lets Ctrl-C reach
  the import). Stopping the container also ends it; nothing committed is lost, and the next run resumes.
- The import loads the configuration like the service ([Configuration and secrets](configuration-and-secrets.md)), logs
  as JSON lines to standard error at `log.level`, opens the database next to the running service, and stops cleanly on
  SIGINT or SIGTERM.
- The import runs as the container's user (65532 by default, or the IDs set with `user:` or `--user`) and reads only
  what that user may read; an unreadable file is listed as failed. The operator grants read access to that user only
  (`chown -R` to that user's IDs and `chmod -R u+rX`, or `setfacl -R -m u:<uid>:rX`) and never makes the copy
  world-readable: a copied `/var/log` holds files such as `auth.log` and `mail.log` with mode `0640 root:adm`.
- `import.time_zone` (an IANA time zone ID, matched ordinally against the backend's embedded time zone database; no
  default, commented out in the example file) is the zone of the server the logs come from and of every local time a
  parser reads (*Local time*). `import.plesk_panel_log`, `import.sw_cp_server_error_log`, `import.nginx_error_log` and
  `import.apache_error_log` name the server paths of four error logs ([Plesk and web server error log
  parsers](log-parsers-web.md)). An invalid value is refused at start-up with the line of the key, never the value
  ([Configuration options](configuration-options.md)).

## Input

- `<path>` is a directory (walked recursively in lexical order), a regular file, a `.tar` or `.tar.gz` archive or a
  `.gz` file. Anything else (FIFO, socket, device, missing path) is an error of the run and is never opened.
- **Formats are recognized by content, not by extension.** A gzip signature is decompressed (all members), one layer
  only: gzip inside gzip is listed as "compressed twice". A `ustar` signature at offset 257 is a tar archive; tar
  archives are handled at the top level and in the directory tree, never inside another archive ("archive inside an
  archive (not opened)"). bzip2, xz, zstd, lz4, zip and 7z signatures are listed as "unsupported format". Empty
  content is listed as "empty". Everything else goes to the parsers; a file no parser claims is listed as "no parser
  recognized the file". Version 7 tar archives (no signature) are not supported. Truncated or corrupt gzip data fails
  the file (*Repeatable import*).
- Tar entries: regular files (including sparse files) are read; directories and PAX global headers are not listed;
  links, devices, FIFOs and unknown types are listed; a corrupt header ends the archive, which is listed as failed.
  A tar entry with a PAX size record is refused with a reason, and extended headers are capped at 1 MiB.
- Symbolic links, hard links and special files in a directory or archive are listed and never followed or opened as
  data; the operator copies the target instead.
- At most **20,000 entries** per run (every directory entry of any kind and every tar header except PAX global
  headers). The limit is enforced while scanning: the entry that exceeds it ends the scan at once, is not read, and
  nothing has been written. A larger input is split by the operator.
- A path or entry name longer than 1,024 bytes is listed as failed.
- There is no limit on the size of a file or of decompressed data. Memory stays flat for any size and number of
  entries; a decompression bomb costs time and stops only by cancellation (Ctrl-C). Until then, one of valid lines
  fills the storage directory with records, and any content with line feeds fills it at 16 bytes per line end (the
  line hashes of *An older copy is recognized*) even when no record is stored, as a small `.gz` of nothing but `\n`
  does. A content that fails keeps the line hashes of its latest attempt only; the next import of it deletes them.

## Safe file access

- **Nothing is extracted or written** to disk. Archives are streamed; entry names and relative paths are cleaned and
  used only as labels in the summary, in logs and in the stored file name (invalid UTF-8 replaced, cut to 1,024 bytes).
- The root is resolved once and checked without following the last link. Every file below it is opened without
  following links, non-blocking (a special file swapped in cannot stall the run): every path component is resolved
  with the kernel's beneath-root resolution without symbolic links where the kernel supports it. On kernels without it
  (older NAS kernels, such as 4.4) the fallback checks every directory of the path without following it, refuses `..`
  and opens the last element without following a link, so containment is checked by the import, not enforced by the
  kernel; a link swapped in between check and open is not caught.
- Modification times are passed in UTC; one outside the range storable as Unix nanoseconds (for example a forged
  tar `mtime`) is stored as unknown.
- Values derived from the input or the command line (paths, entry names, reasons, a run error's text) are never part
  of a log message: in log lines they are attributes, JSON strings whose encoding escapes control characters, DEL, C1
  controls and format characters such as U+202E; in the summary and in log attributes they are quoted, every
  character of the categories Cc, Cf, Zl and Zp and invalid UTF-8 escaped.

## Repeatable import

### Per file content

- **Idempotency is per file content.** Each recognized file is identified by the SHA-256 of its **decompressed**
  content, so a rotated file (`syslog.1`, then `syslog.2.gz`) and a copy inside a `.tar.gz` count as the same file.
  Importing a content that was imported completely stores nothing and is reported as "already imported"; two
  identical files in one run are stored once.
- Records are not deduplicated line by line (log lines have no identity; a message can legitimately appear twice in
  the same second).
- **No deduplication across sources or with live data.** Files of the same period that hold the same lines store
  them once each ([System and mail log parsers](log-parsers-system.md), *Duplicates*). Lines the agent shipped live
  ([Agent log shipping](agent-log-shipping.md)) are stored again by an import of the same period: the import does not
  know which lines arrived live, and live records carry the agent's `log` (the absolute path of a file) where the
  import's carry the listed name. Both carry the same classification, but a MariaDB entry or kernel report can be
  split differently at a restart. The agent ships no history from before its first start; that is the import's job.

### Two passes, batches and resume

- Pass 1 lists every file, detects its parser and reads the whole decompressed content to compute hash and size; a
  file that cannot be read completely (truncated or corrupt gzip, I/O error) fails there and is not imported, not even
  in part (the operator can decompress the readable part and import it as a plain file). Pass 2 imports the recognized
  files in input order.
- Records are written in **batches** of at most 2,000 records or the records from 4 MiB of input, whichever comes
  first, each batch in one transaction, so the single write lock is never held for a whole file. Per file the store
  keeps the number of records stored; a batch advances it in the same transaction as its records, as a
  compare-and-set on the expected count. An interrupted file is resumed by parsing it again and dropping the first
  *n* records. Two concurrent imports therefore never store a file twice (the loser fails with a conflict).
- A resumed content is parsed with the file name and modification time of its **first** import, so the rest is parsed
  exactly like the part stored earlier (an inferred year cannot change in the middle of a file).
- Pass 2 reads the content only up to the size hashed in pass 1 and hashes it again. If fewer bytes can be read or
  the hash differs, the file fails and is not completed. A file that is only appended to while it is imported is
  imported up to the hashed size; the appended lines come with a later import, which continues the file (below).

### A file that grew is continued

A content that starts with a content imported completely before (the same log with more lines at its end) has another
hash, but only its new part is parsed and stored:

- Only a file whose parser is **line-framed** (*Parser contract*) is continued. A journal export that grew is imported
  as a whole, its earlier entries stored again.
- Pass 1 computes the **head key** of every recognized file: the SHA-256 of its first line (up to and including the
  first line feed within the first 4,096 bytes; the whole head when it holds none). **Bases** are the complete
  contents of the same source type and head key from earlier runs (the store, schema version 6 and later) and the
  earlier files of the same run, in input order. At most 64 bases (the largest) are compared for one file. While
  hashing, pass 1 compares the hash of the first *n* bytes with each base of size *n*; the **longest** base whose
  bytes the content starts with wins.
- The new part starts at the **beginning of the line in which the base ends** (after the last line feed within the
  base). A base without any line feed is never continued. The whole content is still hashed in both passes, the
  skipped part included, so a change of the skipped bytes between the passes fails the file ("the content changed
  while it was imported").
- The new part is parsed on its own with the file name and modification time of the **continued** content's own
  first import (its own state row), never those of the base, so the year of its year-less times is anchored on the
  continued file. The continuation point is stored with the state; a resume continues there, dropping the records
  already stored as for any resume. Skip line numbers of the new part count in the whole file.
- The parse treats the first new line as the file's first line: a UTF-8 byte order mark (EF BB BF) at its start is
  removed, while a whole-file parse keeps it in that line's message. Framing and stored lines are otherwise the same.

### An older copy is recognized

A content that is the start of a content imported completely before (the same log with fewer lines at its end,
imported later or later in the same input) stores nothing and is reported as "contained":

- **Line hashes.** In pass 2, every content imported or continued (any parser) stores, for every line end (before a
  continuation point too), the first 16 bytes of the SHA-256 of the decompressed bytes up to it, in rows of at most
  4,096 hashes written while the content is read and before the file is completed.
- **Attempts.** Every start of an import of an incomplete content raises its attempt by one and deletes the line
  hashes of its earlier attempts. Line-hash writes and batches carry the attempt, and a run whose attempt was
  superseded gets a conflict on its next write. Completing the file keeps the rows of the completing attempt only. A
  content completed without line hashes (a store from schema version 6) is never a container.
- **Containment.** A content without a state of its own is checked before the forward continuation: it is contained
  when a **complete** content of the same source type and head key, larger than it, holds the hash of the whole
  content at one of its line ends. At most the 64 largest such contents are compared, largest first. Containment
  wins over a forward base. A content that equals a stored content is not checked (already imported, or resumed).
- **Nothing is parsed or stored** for a contained content, whatever its parser, and no state is kept: importing it
  again gives "contained" again. The summary names the container.

### Limits of the guarantee

- An older copy that ends inside a line (no line feed at its end), one whose container was imported before schema
  version 7, and one whose container is not among the 64 largest candidates are not recognized and are imported as a
  whole. Contents imported before schema version 6 are never a base.
- The lines of a contained older copy are present as the container's parse stored them (its file name, modification
  time and grouped entries). The copy ends at a line end, which for the journal export or a parser that groups lines
  (MariaDB error log, kernel reports) can lie inside an entry of the container or a binary journal field. Nothing is
  lost, because the container's parse stored that entry whole, but an entry the copy holds only in part is not stored
  as such a part.
- A file that starts with the exact bytes of a real log and is imported before it becomes that log's container; the
  log's lines are then found only under that file's name. Containment rests on a 128-bit match of truncated SHA-256.
- At the continuation point a grouped entry is split: continuation lines of a MariaDB error log entry after the point
  are skipped as "line before the first entry" (the summary counts them), and MariaDB entries and kernel reports of
  syslog become the stored entry plus lines of their own. A line the earlier copy cut (no final line feed) is stored
  again, complete: the one case in which a line has two records, the cut one and the complete one.
- Imported records are never removed by retention ([Retention and rollups](retention.md)), whatever their age. A content whose records were removed some other way keeps its complete state: it is still
  already imported, a longer content is still continued after it, and the removed part is never restored.
- A file whose hashed part changes while it is imported fails; the records written before the change was detected
  stay stored under the original hash. Records do not reference their import file, so one import cannot be removed
  or redone.

## Parser contract

A parser turns the lines of one file into records. The importer and every parser fulfill this contract; the parser
documents add only their own rules.

### Registration and detection

- A parser has a type name matching `^[a-z][a-z0-9._-]{0,63}$`; the type is stored with the imported file.
- The parsers are registered in an explicit, ordered list (the order is part of the behavior). The registry refuses
  a missing parser, a duplicate type or an invalid type name. A new parser is one parser and one entry in the list;
  the importer does not change.
- Unless a hook replaces it, `vandoxd import` registers in this order: `journal`, `mariadb`, `dovecot`, `syslog`,
  `plesk-panel`, `sw-cp-server-error`, `nginx-error`, `apache-error`, `legacy-monitor` (documents under *Not here*).
- Detection receives the file (cleaned name and modification time) and its **head**, the first 4,096 bytes, and
  returns a confidence, in rising order none, name match, content match or path match (only the Plesk and web server error log parsers
  return the last). The highest confidence wins, the parser registered earlier on a tie; generic parsers claim weakly.
  The file name handed to a parser has a `.gz` suffix removed when the content was decompressed.
- A **syslog name** is a base name `syslog` or `kern.log`, alone or followed by `.N` or `-YYYYMMDD`; `syslog.err`,
  `mysql-syslog` and a directory named `syslog` do not count. The parsers that claim a file by its first line (MariaDB,
  Dovecot, the Plesk and web server error logs, the legacy monitoring log) never claim a file with a syslog name.
- **Claim by the first line.** Such a parser reads the head with a UTF-8 byte order mark at its start and a `\r`
  directly before `\n` removed, and looks at its first line only (the MariaDB parser: first non-empty line), so one
  line of its shape that someone gets into another file cannot take that file from its parser. Whether a first line
  cut by the end of the head counts is the parser's own rule.
- **The shape is no proof.** Any imported file whose first line has a parser's shape, such as one a web-space user
  wrote under a saved `/var/www`, is read as that log, and its `log` keeps its real path. A source type, program name,
  priority or event is a classification of text, never proof of which program wrote a line, and later analysis must
  not treat it as one. Each parser names how its lines can be forged (*Forged lines*).

### Parsing

- A parse reads a stream and emits records of origin `import` with capture times in UTC, sequence 0 and, unless the
  parser says otherwise, `log` the file name as the import lists it (relative to the import root or archive, rotation
  suffix included, for example `backup/var/log/syslog.1`).
- A parse is **deterministic** for the same content, file and configuration, emits the records in a deterministic
  order (file order, except that a parser that combines lines into one record may emit records of lines written inside
  it first), keeps memory bounded independently of the input size, honors cancellation and returns the error of the
  emitter. The importer resumes by dropping the first *n* emitted records, so the records emitted before a failure
  must be a prefix of those a complete parse emits: a record that combines several lines (a kernel report, a MariaDB
  entry, a legacy snapshot) is emitted only at its normal end, never when the parse ends by an exception
  (cancellation, an emitter error, the missing time zone).
- Malformed or hostile input gives a skipped line or entry (or a raw record, below), never a crash, an unbounded
  allocation or a hang. The built-in parsers are hand-written, use ordinal, case-sensitive comparisons and no regular
  expression, and are linear in the length of a line.
- Skip reasons are fixed texts that never contain input content. Skip line numbers are 1-based and count in the whole
  file (the journal export reports 0).
- A parser may keep a line it cannot read as a **raw record** and report it with a warning (a fixed reason and a line
  number, as for a skip). The line is stored, not skipped; the importer counts such lines per file and keeps the
  first ten reasons. Only the Plesk and web server error log parsers keep raw lines; the others skip.
- The importer validates every record, including the size of a JSON payload (at most 16,777,216 bytes of JSON; metric
  points and log lines are not checked). A refused record is counted as a skipped line with a reason that begins
  `record refused: ` (for example `record refused: data is larger than 16777216 bytes as JSON`), so a parser bug
  cannot fail a whole batch.
- Stored source type: a changed detection of a resumed content fails the file; a changed parser version is not
  noticed, so a content keeps the records of the version that stored them (before the resume point of a resumed one).
  `vandoxd import` offers no way to read a content again.
- **Line-framed** tells whether the format is framed by lines only: every byte after a line feed starts a new line and
  is never the rest of a field begun before it. Every parser declares it; the importer starts a parse at a line start
  in the middle of a content only for a line-framed parser. Every built-in parser but the journal export (whose binary
  fields hold line feeds) is line-framed. Declaring a format with length-framed fields line-framed is a parser bug.

### Lines and text

- Lines are read with a bounded line reader: lines without the line ending, cut at 16 KiB at a UTF-8 boundary with a
  truncated flag, the rest of the line discarded, so a file without line breaks cannot grow memory. A record built
  from a line the reader cut has `truncated` set.
- **Limits count UTF-8 bytes of the decoded text.** Invalid bytes decode to U+FFFD (three bytes each), so every kept
  text of a log line is decoded and cut at a character boundary to its limit: `message` 16,384 bytes, `host` and
  `program` 1,024 bytes, with `truncated` set when cut; the record rules never refuse a log line because of its
  input.
- A UTF-8 byte order mark is removed at the start of line 1 only (and of a continued part, above); on any other line
  it is part of the text.

### Time

- **Storable range.** Storage holds instants from 1677-09-21T00:12:43.1452242Z to 2262-04-11T23:47:16.8547758Z
  ([Storage](storage.md)). Years, days, time fields and anchors are checked as integers before any date value is
  built, so no out-of-range input makes a parse fail. A date that does not exist (31 November, 29 February in a common
  year, `2026-02-30`) is skipped as "invalid date", an instant outside the range as "time outside the storable range",
  unless the parser names other checks or reasons.
- A file's modification time comes from the file system or a tar header and can be forged; one outside the storable
  range counts as no modification time.
- **Local time.** A local time without a zone is read in `import.time_zone`, by a mapping that never fails: a time in
  the hour skipped at the start of daylight saving time is shifted forward by the gap; a time in the repeated hour
  takes the earlier offset unless that puts it more than ten minutes before the last instant the parse resolved from a
  local time (which lines count is the parser's rule; skipped lines never do), then the later offset. Without any line
  between the end of the first pass and a line of the second pass within ten minutes of it, the choice stays ambiguous
  and the earlier offset is taken. Times with an explicit offset ignore the option. The legacy monitoring log resolves
  the repeated hour by its zone abbreviation instead ([Legacy monitoring log parser](log-parsers-legacy-monitoring.md)).
- **Missing time zone.** With `import.time_zone` unset, the first line that needs it (the parser names
  which) fails the parse with the fixed error text "import.time_zone is not set" before anything else of that line is
  looked at: the line is neither emitted nor skipped, the file is listed as failed, the records before it stay stored
  and the run exits with 1. A file without such a line does not need the option. Setting the option and importing
  again resumes and completes the file. Changing the option between an interrupted run and its resume shifts the
  rest of that file.

## Result

- **Outcomes per file:** imported, continued (the new part of a file that grew was stored), already imported,
  contained (the content is the start of a content imported before; nothing was stored), not recognized (with the
  reason), failed (with the reason). Every file that is not imported appears with its outcome and reason; nothing is
  skipped silently.
- **Progress** goes as JSON log lines to standard error: one per finished file (path, outcome including `continued`
  and `contained`, source type, reason, lines, records, skipped), after it one at level warning for a file with lines
  kept raw (path, source type, number of raw lines), one per 64 MiB hashed, one when the scan is done (files, pending)
  and one per 100,000 lines of a large file.
- **The summary** goes as text to standard output and holds nothing else: the root; files found and counts per
  outcome; lines read (for a continued file only from the continuation point), records stored, lines skipped and lines
  kept raw; the time range of the stored records (RFC 3339, UTC) or that none were stored; whether the run was
  interrupted; then the files continued (path, the name of the content continued, the line after which the new part
  starts and, when there are any, the lines the parser skipped before its first record), the files contained in an
  imported file (path and the name of the content they are the start of), the files not recognized and the files that
  failed with their reasons, the files with skipped lines with their first problems (at most ten per file), and the
  files with lines kept raw with their number and first reasons (at most ten per file). Lines kept raw are stored
  records: they count among the records stored and never change the outcome or the exit code. Skipped and raw lines
  are counted over the whole parse of this run, including the part a resumed run drops as stored earlier. The text format belongs to the command and is not an interface.
- **Interruption** (Ctrl-C) anywhere in pass 2 — while a file is parsed, verified or stored, between two files or
  between the entries of an archive — stops the run and still prints the summary, which says that the run was
  interrupted. The file being imported then is listed as failed with the reason `interrupted` and is resumed by the
  next run. Entries of an archive not reached are not listed; a plain file reached after the interruption may be
  listed as failed with the reason `interrupted` and is likewise imported by the next run.
- **Exit code:** 0 when every file was imported, continued, contained, already imported or not recognized (a saved
  `/var/log` always holds files no parser claims, such as `wtmp` and `lastlog`); 1 when a file failed, the run was
  interrupted or ended with an error, or the configuration or the database could not be opened (the summary is still
  printed when there is one); 2 for a usage error.

## Related decisions

- [0014](../decisions/0014-log-import-is-a-core-component.md) — why historical import and log shipping are core.
- [0021](../decisions/0021-no-pseudonymization-of-log-data.md) — why log data is stored unchanged.
- [0069](../decisions/0069-log-import-idempotent-per-file-content-hash-with-resumable-batches.md) — why content hashes, two passes, resume by count and the continuation of a grown file.
- [0072](../decisions/0072-vandoxd-import-sub-command-output-and-exit-codes.md) — why the sub-command, the output channels and the exit codes.
- [0076](../decisions/0076-strict-gzip-validation-in-the-backend.md) — why truncated or corrupt gzip data fails a file.
- [0079](../decisions/0079-log-parsing-and-import-in-the-backend-without-following-links.md) — why streaming, content-based formats and kernel-level link-free file access.
- [0085](../decisions/0085-syslog-time-zone-from-import-time-zone-with-embedded-tzdb.md) — why `import.time_zone` has no default and an embedded zone database.
- [0092](../decisions/0092-error-logs-by-content-or-configured-path-unreadable-lines-kept-raw.md) — why a configured path ranks above content and unreadable lines can be kept as raw records.

## Not here

- The parsers: [System and mail log parsers](log-parsers-system.md) (`journal`, `syslog`, `dovecot`),
  [MariaDB error log parser](log-parsers-mariadb.md),
  [Plesk and web server error log parsers](log-parsers-web.md),
  [Legacy monitoring log parser](log-parsers-legacy-monitoring.md).
- The record kinds and their fields: [Wire format records](wire-format-records.md).
- Storing records and the import state: [Storage writing](storage-writing.md).
- Why imported records never raise alerts: [Detection, alerts and reports](detection-and-reports.md).
- The validation of the `import.*` options: [Configuration options](configuration-options.md).
- The service entry point (`-healthcheck`, `-version`, shutdown): [Backend host](backend-host.md).
- Live log lines from the agent: [Agent log shipping](agent-log-shipping.md).

## Implementation

`Vandox.Import` (`Importer`, `Scanner`, `SourceRoot`, `FormatSniffer`, `ImportLimits`, `RecordEmitter`),
`Vandox.Core.LogParsing` (`ILogParser`, `ParserRegistry`, `BuiltInParsers`, `LogLineReader`, `Utf8Text`, `SyslogClock`
on NodaTime's TZDB), `Vandox.Core.Model` (`StorableTime`), `Vandox.Core.IO` (`SecureRoot`: `openat2` with
`RESOLVE_BENEATH | RESOLVE_NO_SYMLINKS`, else `lstat` and `O_NOFOLLOW`, always `O_NONBLOCK`; `FileProbe`),
`Vandox.Backend/Cli` (`ImportCommand`, `ImportSummaryWriter`, `ImportProgressLogger`). New-parser checklist:
`.squad/project.md`, *Integration surface*.
