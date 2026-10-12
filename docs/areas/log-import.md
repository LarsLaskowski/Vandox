# Log import

## Scope

Importing historical logs into the backend: the `import` sub-command of `vandoxd`, what it accepts, how it reads
untrusted input, how it stays repeatable, what it reports and the contract the log parsers fulfill. What a parsed
record is and how it is stored is described by the storage area and the [wire format](wire-format.md); how
imported data is kept apart from live data (it never raises alerts) by the detection area.

## Command

- `vandoxd [flags] import [-config <file>] <path>` imports the input at `<path>`. `-config` is accepted before and
  after `import`. The command takes exactly one path. `-h` prints its usage and exits 0. `-healthcheck` together
  with `import` is a usage error.
- In the container: `docker exec -it vandoxd /vandoxd import /import/<name>`. `-it` is needed for Ctrl-C to
  reach the import; stopping the container also ends it. Nothing committed is lost, and the next run resumes.
- The import loads the configuration like the service (see *Configuration and secrets*), logs as JSON lines to
  standard error at `log.level`, opens the database next to the running service, and stops cleanly on SIGINT or
  SIGTERM.
- The import runs as the container's user (65532) and reads only what that user may read; an unreadable file is listed as failed. The operator grants read
  access to that user only (`chown -R 65532:65532` and `chmod -R u+rX`, or `setfacl -R -m u:65532:rX`) and never makes
  the copy world-readable: a copied `/var/log` holds files such as `auth.log` and `mail.log` with mode `0640 root:adm`.
- The backend option `import.time_zone` (IANA time zone ID, ordinal match against NodaTime's embedded database, no
  default) names the zone of the server the logs come from. It is used for year-less syslog times (see *System log
  parsers*), for the local times of the MariaDB error log (see *MariaDB error log*), for the times of Dovecot's log file (see *Dovecot log file*), for the local times of the Plesk and web server error logs (see *Plesk and web server error logs*) and for the header times of the legacy monitoring log (see *Legacy monitoring log*); a value that is no zone ID is refused at start-up with the line of the key and without the value. The
  example file holds it commented out.
- The backend options `import.plesk_panel_log`, `import.sw_cp_server_error_log`, `import.nginx_error_log` and
  `import.apache_error_log` name the server path of the Plesk panel log, the `sw-cp-server` error log and the nginx and
  Apache error logs (see *Plesk and web server error logs*). Each has the path of a current Plesk on Ubuntu as its
  default; a value that is not an absolute, clean path with a directory is refused at start-up with the line of the key
  and without the value.

## Input

- `<path>` is a directory (walked recursively in lexical order), a regular file, a `.tar` or `.tar.gz` archive or a
  `.gz` file. Anything else (FIFO, socket, device, missing path) is an error of the run and is never opened.
- **Formats are recognized by content, not by extension.** A gzip signature is decompressed (all members), one layer
  only: gzip inside gzip is listed as "compressed twice". A `ustar` signature at offset 257 is a tar archive; tar
  archives are handled at the top level and in the directory tree, never inside another archive ("archive inside an
  archive (not opened)"). bzip2, xz, zstd, lz4, zip and 7z signatures are listed as "unsupported format". Empty
  content is listed as "empty". Everything else goes to the parsers; a file no parser claims is listed as "no parser
  recognized the file". Version 7 tar archives (no signature) are not supported.
- Tar entries: regular files (including sparse files) are read; directories and PAX global headers are not listed;
  links, devices, FIFOs and unknown types are listed; a corrupt header ends the archive, which is listed as failed.
  A tar entry with a PAX size record is refused with a reason, and extended headers are capped at 1 MiB.
- Symbolic links, hard links and special files in a directory or archive are listed and never followed or opened as
  data. The operator copies the target instead.
- At most **20,000 entries** per run (every directory entry of any kind and every tar header except PAX global headers).
  The limit is enforced while scanning: the entry that exceeds it ends the scan at once, is not read, and nothing
  has been written. A larger input is split by the operator.
- A path or entry name longer than 1,024 bytes is listed as failed.
- There is no limit on the size of a file or of decompressed data. Memory stays flat for any size and number of
  entries; a decompression bomb costs time and stops only by cancellation, and one of valid lines fills the storage
  directory until the operator presses Ctrl-C. Any decompressed content with line feeds fills the storage directory at
  16 bytes per line end, even when no record is stored (a small `.gz` of nothing but `\n` suffices), until the operator
  presses Ctrl-C. A content that fails keeps the line hashes of its latest attempt only, and the next import of it
  deletes them.

## Safe file access

- **Nothing is extracted or written** to disk. Archives are streamed; entry names and relative paths are cleaned and
  used only as labels in the summary, in logs and in the stored file name (invalid UTF-8 replaced, cut to 1,024 bytes).
- Every file is opened below the import root without following links: every path component is resolved with the
  kernel's beneath-root resolution without symbolic links where the kernel supports it. On kernels without it
  (older NAS kernels) the fallback checks every directory of the path, refuses `..` and opens the last element
  without following a link. The fallback is weaker (a link swapped in between check and open is not caught) and is
  stated openly in the project's security notes.
- The root is resolved once and checked without following the last link.
- Modification times are passed in UTC; one outside the range storable as Unix nanoseconds (for example a forged
  tar `mtime`) is stored as unknown.
- Names and values derived from the input or the command line are quoted wherever they appear in logs or the
  summary, because the JSON log handler writes C1 control characters and format characters such as U+202E raw. Every
  character of the categories Cc, Cf, Zl and Zp and invalid UTF-8 is escaped.

## Repeatable import

- **Idempotency is per file content.** Each recognized file is identified by the SHA-256 of its **decompressed**
  content, so a rotated file (`syslog.1`, then `syslog.2.gz`) and a copy inside a `.tar.gz` count as the same file.
  Importing a content that was imported completely stores nothing and is reported as "already imported"; two identical
  files in one run are stored once.
- Records are not deduplicated line by line: log lines have no identity of their own, and a message can legitimately
  appear twice in the same second.
- The run has two passes. Pass 1 lists every file, detects its parser and reads the whole decompressed content to
  compute hash and size; a file that cannot be read completely (truncated or corrupt gzip, I/O error) fails there and
  is not imported at all, in part either; the operator can decompress the readable part and import it as a plain file.
  Pass 2 imports the recognized files in input order. Hashing reports progress every 64 MiB.
- Records are written in **batches** of at most 2,000 records or the records from 4 MiB of input, whichever comes
  first, each batch in one transaction, so the single write lock is never held for a whole file. Per file the store
  keeps the number of records stored; a batch advances it in the same transaction as its records, as a
  compare-and-set on the expected count. An interrupted file is resumed by parsing it again and dropping the first
  *n* records. Two concurrent imports therefore never store a file twice (the loser fails with a conflict).
- A resumed content is parsed with the file name and modification time of its **first** import, so the part
  parsed later is parsed exactly like the part stored earlier (a year inferred from the time stamps cannot change
  in the middle of a file).
- Pass 2 reads the content only up to the size hashed in pass 1 and hashes it again. If fewer bytes can be read or
  the hash differs, the file fails and is not completed. A file that is only appended to while it is imported is
  imported up to the hashed size; the appended lines come with a later import, which continues the file (below).
- **A file that grew is continued.** A content that starts with a content imported completely before (the same log with
  more lines at its end) has another hash, but only its new part is parsed and stored. The rules:
  - Only a file whose parser is **line-framed** (`LineFramed`: syslog, the MariaDB error log, the Dovecot log file, the Plesk and web server error logs and the legacy monitoring log) is continued. A journal
    export that grew is imported as a whole and its earlier entries are stored again, because its binary fields hold
    line feeds and a line feed in the middle is no safe place to start a parse.
  - Pass 1 computes the **head key** of every recognized file: the SHA-256 of its first line (up to and including the
    first line feed within the first 4,096 bytes; the whole head when it holds none). **Bases** are the complete
    contents of the same source type and head key from earlier runs (the store, schema version 6 and later) and the
    earlier files of the same run, in input order. At most 64 bases (the largest) are compared for one file. While
    hashing, pass 1 compares the hash of the first *n* bytes with each base of size *n*; the **longest** base whose bytes
    the content starts with wins.
  - The new part starts at the **beginning of the line in which the base ends** (after the last line feed within the
    base). A base without any line feed is never continued. The whole content is still hashed in both passes, the
    skipped part included, so a change of the skipped bytes between the passes fails the file ("the content changed
    while it was imported").
  - The new part is parsed on its own with the file name and modification time of the **continued** content's own
    first import (its own state row), never those of the base: the year of year-less times in the new part is
    anchored on the continued file's name date or modification time. The continuation point is stored with the state,
    so a resume continues at the same place, with the records already stored dropped as for any resume.
  - Skip line numbers of the new part count in the whole file.
  - A parse that starts at the continuation point treats the first new line as the file's first line: a UTF-8 byte order mark (EF BB BF) at its start is removed, as it is at the start of a file, while a whole-file parse keeps it in that line's message. Framing and the stored lines are otherwise the same.
- **An older copy is recognized.** A content that is the start of a content imported completely before (the same log
  with fewer lines at its end, imported later or later in the same input) stores nothing and is reported as
  "contained". The rules:
  - **Line hashes.** In pass 2, every content that is imported or continued (any parser) stores, for every line end, the
    first 16 bytes of the SHA-256 of the decompressed bytes up to that line end, in rows of at most 4,096 hashes (the
    part before a continuation point included). The rows are written while the content is read and before the file is
    completed.
  - **Attempts.** Every `BeginImportAsync` of an incomplete content raises its attempt by one and deletes the line
    hashes of its earlier attempts. Line-hash writes and batches carry the attempt, and a run whose attempt was
    superseded gets a conflict on its next write. Completing the file keeps the rows of the completing attempt only.
    A content completed without line hashes (a store from schema version 6) is never a container.
  - **Containment.** A content without a state of its own is checked before the forward continuation: it is contained
    when a **complete** content of the same source type and head key, larger than it, holds the hash of the whole
    content at one of its line ends. At most the 64 largest such contents are compared, largest first. Containment wins
    over a forward base. A content that equals a stored content is not checked (already imported, or resumed).
  - **Nothing is parsed or stored** for a contained content, whatever its parser, and no state is kept: importing it
    again gives "contained" again. The summary names the container.
- Limits of the guarantee: an older copy that ends inside a line (no line feed at its end), one whose container was
  imported before schema version 7, and one whose container is not among the 64 largest candidates are not recognized
  and are imported as a whole. The lines of a contained older copy are present as the container's parse stored them
  (the container's file name and modification time, its grouped entries). The grouping is the container's: the older
  copy is cut at a line end, which for a parser that is not framed by lines (`LineFramed` false, the journal export) or
  that groups lines into entries (MariaDB error log, kernel reports) can lie inside an entry of the container, or inside
  a binary field of a journal entry. Nothing of the older copy is lost, because the container's parse stored that
  entry whole; its lines are found only inside the container's records, framed and grouped as the container's parse
  made them, and an entry that the older copy holds only in part is not stored as such a part. A file that starts with
  the exact bytes of a real log and is imported before it becomes that log's container, and the log's lines are then
  found only under that file's name. Lines are left out on a 128-bit match of the truncated SHA-256. Contents imported
  before schema version 6 are never a base. At the continuation point a
  grouped entry is split: continuation lines of a MariaDB error log entry after the point are skipped as "line before
  the first entry" (the summary counts them), and MariaDB entries and kernel reports of syslog become the stored entry
  plus lines of their own. A line the earlier copy cut (no final line feed) is stored again, complete: the one case in
  which a line has two records, the cut one and the complete one. A content whose records were removed later
  (retention, once it exists) keeps its complete state, so it is still reported as already imported and a longer
  content is still continued after it; the removed part is never restored. A file whose hashed part changes while it is
  imported fails; the records written before the change was detected stay stored under the original hash. Records do
  not reference their import file, so one import cannot be removed or redone.

## Parsers

A parser turns the lines of one file into records. The importer and every parser fulfill this contract:

- A parser has a type name matching `^[a-z][a-z0-9._-]{0,63}$`; the type is stored with the imported file.
- `Detect` receives the file (cleaned name and modification time) and the first 4,096 bytes and returns a
  confidence: none, name match, content match or path match. The parser with the highest confidence wins, the one
  registered earlier on a tie. A configured path beats a content signature, a content signature beats a name match;
  generic parsers claim files weakly. Only the parsers of *Plesk and web server error logs* return a path match.
- The parsers are registered in an explicit, ordered list (the order is part of the behavior). The registry refuses
  a missing parser, a duplicate type or an invalid type name.
- `Parse` reads a stream and emits records of origin `import` with capture times in UTC. It is **deterministic**
  for the same content, file and configuration (the importer resumes by dropping the first *n* emitted records, so the
  records emitted before a failure are a prefix of those a later complete parse emits), emits the records in a
  deterministic order (file order, except that a parser that combines lines into one record may emit records of lines
  written inside it first), keeps memory bounded independently of the input size, honors cancellation and returns the
  error of the emitter. Skip reasons are fixed texts that never contain input content.
- A parser may keep a line it cannot read as a **raw record** and report it with a warning (a fixed reason, like a skip
  reason, with the line number). The line is stored, not skipped; the importer counts such lines per file and keeps the
  first ten reasons, with line numbers counted in the whole file as for skipped lines. Only the parsers of *Plesk and
  web server error logs* keep raw lines; the others skip what they cannot read.
- The importer validates every record, including the size of a JSON payload (at most 16,777,216 bytes of JSON, *Bounds* of the legacy monitoring log; metric points and log lines are not checked); a refused record is counted as skipped, so a parser bug cannot fail a whole
  batch. Stored source type: a changed detection of a resumed content fails the file, a changed parser version is
  not noticed.
- Lines are read with a bounded line reader: lines without the line ending, cut at 16 KiB at a UTF-8 boundary with a
  truncated flag, the rest of the line discarded, so a file without line breaks cannot grow memory.
- `LineFramed` tells whether the format is framed by lines only: every byte after a line feed starts a new line and is
  never the rest of a field begun before it. The importer starts a parse at a line start in the middle of a content only
  for such a parser. Syslog, the MariaDB error log, the Dovecot log file, the Plesk and web server error logs and the legacy monitoring log are line-framed; the journal export is not (binary fields hold
  line feeds). A parser a hook supplies must declare it; `true` for a format with length-framed fields is a parser bug.
- The file name handed to a parser has a `.gz` suffix removed when the content was decompressed.
- A new parser is one type and one entry in the list; the importer does not change.

## System log parsers

`BuiltInParsers.Create(timeZone, paths)` is the list `vandoxd import` registers when no hook replaces it: `journal`
(`JournalExportParser`), then `mariadb` (`MariaDbErrorLogParser`, see *MariaDB error log*), then `dovecot`
(`DovecotLogParser`, see *Dovecot log file*), then `syslog` (`SyslogParser`), then `plesk-panel`, `sw-cp-server-error`,
`nginx-error` and `apache-error` (`ErrorLogParser`, see *Plesk and web server error logs*), then `legacy-monitor` (`LegacyMonitorParser`, see *Legacy monitoring log*). The journal parser claims a file by content (a head that begins
with a `__CURSOR=` or `__REALTIME_TIMESTAMP=` line and holds a `__REALTIME_TIMESTAMP=` line; the file name never
matters); the syslog parser claims weakly (name match) a file named `syslog` or `kern.log` with an optional `.N` or
`-YYYYMMDD` suffix, or whose first line has a syslog header. Both parsers produce `log_line` records with origin `import`
and sequence 0 and pass them through three stages in this order: the classification of mail lines (see *Mail lines
(Postfix and Dovecot)* below), MariaDB entries (see *Lines from the journal and syslog* under *MariaDB error log*), then
kernel reports (see *Kernel reports* below).

**Journal export (`journalctl -o export`).** Entries are blocks of fields separated by an empty line. Text fields are
`NAME=value\n`, binary fields `NAME\n`, a 64-bit little-endian length, the bytes and `\n`. Mapping: `captured_at` from
`__REALTIME_TIMESTAMP` (microseconds since the Unix epoch, UTC, 1 to 20 decimal digits, greater than 0 and at most
9,223,372,036,854,775, bound-checked while reading), `host` from `_HOSTNAME`, `program` from `SYSLOG_IDENTIFIER`, else
`_COMM`, `pid` from `_PID`, else `SYSLOG_PID` (1 to 2,147,483,647, else 0), `priority` from `PRIORITY` when it is one digit
0 to 7, `message` from `MESSAGE`, `log` and `source` `journal`. A repeated field keeps its first value. Only these eight
fields are kept; every other field, text or binary, is read through a reused buffer and discarded, and a declared
binary length never sizes an allocation. A field name must be 1 to 64 bytes of `A-Z 0-9 _` and not start with a digit,
otherwise the entry is skipped as "malformed field" and reading resumes after the next empty line (scanned in the
reused buffer). An entry without a time stamp, with an invalid one or without `MESSAGE` is skipped as "entry without
__REALTIME_TIMESTAMP", "invalid __REALTIME_TIMESTAMP" or "entry without MESSAGE"; an entry cut off by the end of input
(a text value without `\n`, a binary length larger than the remaining input or at least 2^63) is skipped as "truncated
entry" and ends the parse. A last entry without the closing empty line is accepted. The skip line number is 0.

**Syslog (`syslog`, `kern.log`, rotations).** A line is `[<PRI>]TIMESTAMP HOST [TAG] MESSAGE` in the traditional format
(`Mmm dd HH:MM:SS`, day space- or zero-padded) or the RFC 3339 format (`YYYY-MM-DDTHH:MM:SS[.fraction]` and `Z` or an
offset of at most 14:00, upper-case `T` and `Z` only). `priority` is the `<PRI>` number modulo 8 (0 to 191), `null`
without it. `TAG` is `PROGRAM[PID]:` or `PROGRAM:`; without a tag the program is empty and the pid 0; a process ID above
2,147,483,647 gives pid 0 with the tag kept. The message follows the colon and at most one space. `log` is the file name
as the import lists it (relative to the import root or archive, rotation suffix included, for example
`backup/var/log/syslog.1`), `source` `syslog`. RFC 5424 lines and other text are "not a syslog line"; an empty line is
"empty line"; the skip line number is the 1-based line number. A UTF-8 byte order mark at the start of a file is
ignored. Parsing is hand-written and linear in the length of a line.

**Limits count UTF-8 bytes of the decoded text.** Invalid bytes decode to U+FFFD (three bytes each), so every kept text
is decoded and cut at a character boundary to its limit: `message` 16,384 bytes, `host` and `program` 1,024 bytes, with
`truncated` set when cut; the record rules never refuse a record because of its input. The syntactic limits of a syslog
`HOST` (1 to 255) and `PROGRAM` (1 to 128) are counted in UTF-8 bytes of the decoded line and lie below the model's
limit, so they are never cut: a longer host makes the line "not a syslog line", a longer program means no tag. A journal
value longer than its raw read bound (16,384 bytes for `MESSAGE`, 1,024 for the short fields) keeps the first bytes
(a character split by the cut is dropped whole) and sets `truncated`; the rest is discarded in the reused buffer.

**Storable time range.** Storage holds instants from 1677-09-21T00:12:43.1452242Z to 2262-04-11T23:47:16.8547758Z
(`StorableTime`, also used by the storage layer). Years, days and anchors are checked as integers before any date value
is built, so no out-of-range input throws. An instant outside the range is skipped as "time outside the storable range"
(syslog) or "invalid __REALTIME_TIMESTAMP" (journal); a date that does not exist (31 November, 29 February in a common
year, `2026-02-30`) is skipped as "invalid date".

**Year of a year-less time.** The anchor is a valid `-YYYYMMDD` date at the end of the file's base name (the end of that
local day), else the modification time; a name date is preferred because a plain copy replaces the modification time but
keeps the name. An anchor outside the storable range is not usable. The year of the first year-less line is the
anchor's year, or the year before when the line lies more than a day after the anchor. Every later line keeps the
predecessor's year and advances it by one when it lies more than 180 days before the predecessor (a small step back
within the same year does not advance it); the year stops advancing once it is past the storable range. The predecessor
is the last year-less line that was resolved, whatever the outcome (a line skipped as "invalid date" or "time outside the
storable range" counts); lines that are not syslog lines, empty lines and RFC 3339 lines never are. Without a usable
anchor every year-less line is skipped with "year unknown: the file has no usable date" and RFC 3339 lines are still read.

**Time zone.** A year-less time is local time in `import.time_zone`, mapped with NodaTime's `MapLocal` (never a call that
throws). A time in the hour skipped at the start of daylight saving time is shifted forward by the gap. A time in the
repeated hour takes the earlier offset unless that puts it more than ten minutes (`SyslogClock.BackwardTolerance`)
before the last instant resolved for a year-less line (RFC 3339 lines and skipped lines do not count), then the later
offset; without any line between the end of the first pass and a line of the second pass within ten minutes of it, the
choice stays ambiguous and the earlier offset is taken. RFC 3339 lines ignore the zone. The option has no default:
with it unset, the first year-less line makes `ParseAsync` throw `InvalidOperationException` with the fixed message
"import.time_zone is not set" (before the anchor is looked at; the line is neither emitted nor skipped), the file is
listed as failed, the records before it stay stored and the run exits with 1. Setting the option and importing again
resumes and completes the file. Changing the option between an interrupted run and its resume shifts the rest of that
file.

**Kernel reports.** Both parsers pass their `kernel` lines through `KernelReportGrouper`. A line containing
`invoked oom-killer:` or `------------[ cut here ]------------` opens a report; kernel lines of the same host join it; it
ends after a line containing `Out of memory: Killed process`, `Out of memory (oom_kill_allocating_task): Killed
process`, `Memory cgroup out of memory: Killed process` or `Out of memory and no killable processes` (OOM), or `---[ end trace ` (`cut here`), or at a new start line, at a same-host kernel
line more than 60 seconds after the first line, after 2,000 lines, or at the normal end of input. The record has
program `kernel`, pid 0, the host, `log` and time of the first line, the member messages joined by `\n` in file order and
the lowest member priority (a single-line report is emitted unchanged). Kernel lines are recognized by program `kernel`
alone, without checking the journal transport, so a local process that logs as `kernel` can open a report that takes in
the real kernel lines of the same host for up to 60 seconds or 2,000 lines (see record 0086, option 19). Lines of other programs and kernel lines of
other hosts written inside the report are emitted as their own records before the report record. The message keeps the
first lines up to 8,192 UTF-8 bytes and the last whole lines that fit into 16,384 bytes, with the line
`[N lines omitted]` (the exact count) between them and `truncated` set; memory is independent of the report length. An
open report is emitted only at the normal end of input, never when the parse ends by an exception (cancellation, an
emitter error, the missing time zone): the importer resumes by dropping the first records, so the records emitted
before a failure must be a prefix of a complete parse.

**Mail lines (Postfix and Dovecot).** Both parsers pass every `log_line` record through `MailLineClassifier` before the
MariaDB stage. The classification sets only the priority and the event, never adds, drops or reorders a record, and
holds no state; it is hand-written, uses ordinal and case-sensitive text operations only and is linear in the length of
a message. `mail.log` and the other rsyslog files keep the source type `syslog`; a journal export keeps `journal`.
`mail.log`, `mail.err`, `maillog` and their rotations are claimed by their first line, like `auth.log`; they are no
syslog names, so a copy whose first line is cut is not recognized.

*Programs.* `IsPostfix` is true for a program that begins with `postfix/` or `postfix-`, contains a `/` and has at least
one character after the last `/` (`postfix/smtpd`, `postfix/submission/smtpd`, `postfix-out/smtp`); `IsDovecot` is true
for `dovecot` only. Any other program is left unchanged, whatever its message carries.

*Severity markers.* The marker sets the priority of the record; a line without a marker keeps the priority and the event
it has, and the message, program, pid, host, `truncated` and `log` are never changed.

| Family | Position | Marker, priority (event) |
| ------ | -------- | ------------------------ |
| Postfix | the message begins with it | `warning: ` 4, `error: ` 3, `fatal: ` 2 (`postfix.fatal`), `panic: ` 2 (`postfix.panic`) |
| Dovecot | the message begins with it, or the message is `S: ` + marker + text where `S`, the text before the first `: `, is non-empty and holds no space | `Debug: ` 7, `Warning: ` 4, `Error: ` 3, `Fatal: ` 2 (`dovecot.fatal`), `Panic: ` 2 (`dovecot.panic`) |

The markers include their trailing space. For Dovecot the start of the message is checked first. Dovecot's syslog
format has no `Info: ` marker, so such a line keeps its priority; only Dovecot's own log file reads `Info: ` (see *Dovecot
log file*).

*Unavailable database.* A warning or error line gets `postfix.database_unavailable` or `dovecot.database_unavailable`
when the text *T* after the marker carries a client error *C* at an anchored position. *C* starts with one of eight
phrases (ASCII apostrophe, case-sensitive): `Can't connect to local MySQL server`, `Can't connect to MySQL server`,
`MySQL server has gone away`, `Lost connection to MySQL server` (libmysqlclient) and `Can't connect to local server`,
`Can't connect to server`, `Server has gone away`, `Lost connection to server` (Connector/C).

| Family | Anchor in *T* | *C* |
| ------ | ------------- | --- |
| Postfix | *T* is `connect to mysql server ` + *H* + `: ` + *C*, where *H* is the text up to the first `: ` after the anchor, non-empty and without a space | as written; no `: ` after the anchor means no event |
| Postfix | *T* is `mysql:` + *N* + `: query failed: ` + *C*, where *N* is the text up to the first `: ` after `mysql:`, non-empty and without a space | as written |
| Dovecot | the message begins with `auth-worker(` + one or more ASCII digits + `): ` + marker, and *T* is one to three driver segments + `Connect failed to database (` + ... | the text after the first `): ` that follows the anchor; none means no event |
| Dovecot | as above, with `Query failed, retrying: ` + *C* after the segments | as written |

A driver segment is exactly `mysql: `, or `mysql(` + *P* + `): `, or `sqlpool(` + *P* + `): `, where *P* is the text up to
the first `)` after the opening parenthesis, non-empty and without a space; anything else (another name, another case,
an empty or spaced *P*, a `)` not followed by `: `) ends the walk without an event. The walk reads at most three
segments, each by a prefix comparison; zero segments and a fourth segment give no event. Only the `auth-worker` process
is read, which holds Dovecot's MySQL connections; lines of `lmtp`, `imap` and the other processes, which log remote text
after the marker, are never matched. The text of Postfix's table and host contains no space, while the remote lookup
key of `mysql:<table> lookup error for "<key>"` follows a space before any `: `, so it never reaches the anchor.
Fatal and panic win: a `fatal:` or `Panic:` line gets only its fatal or panic event.

*Forged lines (limitation).* The classification is a reading of text, not a trust boundary. (a) Any local process can
log under `postfix/<x>` or `dovecot` with any marker and any database message. (b) A marker's priority replaces the
priority the transport gave the line (a journal `PRIORITY`, a `<PRI>`), also a more severe one, so a classified priority
is the daemon's class read from the text, not the priority the line was sent with. (c) Remote text reaches a marker
position only through a user name in Dovecot's first segment (`imap(<user>)...`): the default `auth_username_chars` keeps
spaces and colons out of it, and a configuration whose `auth_username_chars` allows a space and a colon lets a remote
user forge a Dovecot marker, and with it the priority and `dovecot.fatal` or `dovecot.panic` (not
`dovecot.database_unavailable`: such a first segment begins with the logging process, e.g. `imap(`, never with
`auth-worker(<digits>)`). A user name reaches the database rule in one configuration only: an `auth-worker` line of a
passdb or userdb whose `name` setting is `mysql` or `sqlpool` carries the auth prefix `<name>(<user>,<ip>): ` where the
rule reads a driver segment, so with an `auth_username_chars` that allows a space (and the colon and parentheses of such
a name) a remote user name such as `a): Connect failed to database (d): Server has gone away` yields
`auth-worker(1): Error: mysql(a): Connect failed to database (d): Server has gone away,1.2.3.4): ...` and
`dovecot.database_unavailable`. (d) Apart from that configuration the database rules read remote text nowhere: a
sender's address, lookup key or Message-ID cannot produce a database event. (e) An event or priority is a
classification, not proof of origin.

*Earlier imports.* Contents imported by a version without the classification keep their records unclassified: an import
is idempotent per content and a changed parser version is not noticed. A file whose interrupted import is resumed by a
version with the classification has unclassified records before the resume point; `vandoxd import` offers no way to read
such a content again.

**Duplicates.** Ubuntu's rsyslog writes `kern.*` to `kern.log` and `*.*` without auth to `syslog` (from the packaged
configuration, not checked on the server), so every kernel line is in both files and importing both stores it twice.
There is no deduplication across sources. Importing a journal export and the syslog files of the same period stores
every message twice as well (sources `journal` and `syslog`), MariaDB entries included. `mail.log`, `syslog` and a journal
export of the same period store each mail line up to three times with the same classification.

## MariaDB error log

**Recognition.** The parser `mariadb` has no path option and finds the file by its content (record 0088; only the error logs of
*Plesk and web server error logs* have path options, record 0092). `Detect` returns a content match only when the
file's name is not a syslog name (`syslog` or `kern.log`, alone or with `.N` or `-YYYYMMDD`, the function the syslog parser
uses; `syslog.err`, `mysql-syslog` and a directory named `syslog` do not count) **and** the **first non-empty line** of the
first 4,096 bytes is an entry header (see below). A UTF-8 byte order mark at the start of the head is removed, a `\r`
directly before `\n` is removed, empty lines before the first non-empty line are passed over, and a header cut by the end
of the head counts when its prefix is complete. No later line of the head is looked at, so one header-shaped line that
someone gets into another file cannot take it away from its parser: a syslog file stays with the syslog parser whatever
lines it holds, and a file the syslog parser claims by its first line is never claimed here (a syslog line starts with
`<`, a month name or `DDDD-DD-DDT`, a header with `DDDD-DD-DD ` or `DDDDDD `). The price: a copy that starts inside an
entry (the output of `tail`), a MariaDB log whose first line has no header and a MariaDB error log under a syslog name are
listed as not recognized. Neither the source type `mariadb` nor an event proves that the file was a MariaDB error log: any
imported file whose first non-empty line has a header's shape, such as one a web-space user wrote under a saved
`/var/www`, is read as one, and `log` keeps its real path.

**Entries.** A line is an **entry header** when its bytes begin with one of four ASCII forms, checked on the raw line
before any decoding (the hour is two digits or a space and one digit): `YYYY-MM-DD HH:MM:SS <thread> [ERROR|Warning|Note] `
(the server line; the thread is 1 to 20 digits, not stored), `YYYY-MM-DD HH:MM:SS 0x<1 to 16 lower-case hex digits>` and
one or more spaces (an InnoDB time stamp, no level), `YYMMDD HH:MM:SS [ERROR|Warning|Note] ` (the signal handler, year
2000 + YY) and `YYMMDD HH:MM:SS mysqld_safe ` (the start script, program `mysqld_safe`). Digits of month, day, hour,
minute and second are not range-checked by the grammar. Every other line, an empty one included, is a continuation line.
A header and the continuation lines after it up to the next header or the end of input are **one record**:
`source` `mariadb`, origin `import`, sequence 0, `log` the file name as listed, `host` empty, `pid` 0, `program` empty
(`mysqld_safe` for the start script), `priority` 3 for `ERROR`, 4 for `Warning`, 6 for `Note` and none without a level, the
message the header's message and the continuation lines joined by `\n`. Inner empty lines are kept, trailing ones are
dropped. A UTF-8 byte order mark is removed from line 1 only; on any other line it is part of the text. Continuation lines
before the first header are skipped: "empty line" for an empty one, "line before the first entry" for any other.

**Bounds.** The line reader cuts a line at 16 KiB (`truncated`). A message of at most 16,384 UTF-8 bytes (decoded; invalid
bytes count three) is kept whole. A longer entry with at least one continuation line keeps its first line (cut at a
character boundary to 16,320 bytes if longer) and the following whole lines while the text stays within 16,320 bytes,
then `\n[N lines omitted]` with the exact number of lines left out (trailing empty lines not counted), and `truncated`. A
long header line alone is cut to 16,384 bytes without a marker. Empty lines count like any other line: a message that fits in 16,384 bytes keeps
them. When an entry is cut, the kept part is the longest run of whole lines, empty ones included, that fits in 16,320
bytes, so the cut can fall inside a run of empty lines; empty lines beyond are counted, never stored, so millions of
empty lines cost a counter. A continuation line is
decoded only when it is kept, so memory does not depend on the input size. An entry that is cut or whose line was cut by
the reader has `truncated` set.

**Events.** The record field `event` (see [Wire format](wire-format.md)) is set from the level and the header's first
line, with ordinal, case-sensitive string operations and no regular expression:

| Event | Level | Header message |
| ----- | ----- | -------------- |
| `mariadb.start` | `Note` | starts with `Starting MariaDB ` and contains ` as process ` (10.6.12 and later, also 10.3.39 and 10.5.22); or a non-empty text, ` (server `, later `) starting as process ` and ends with ` ...` (10.6.7 to 10.6.11) |
| `mariadb.ready` | `Note` | ends with `: ready for connections.` |
| `mariadb.shutdown` | `Note` | ends with `: Normal shutdown` |
| `mariadb.shutdown_complete` | `Note` | ends with `: Shutdown complete` |
| `mariadb.abort` | `ERROR` | a non-empty text, ` got signal `, 1 to 3 digits, ` ;` at the end |
| `mariadb.recovery_start` | `Note` | starts with `InnoDB: Starting crash recovery`, or is exactly `Starting table crash recovery...` |
| `mariadb.recovery_end` | `Note` | is exactly `Crash table recovery finished.`, or starts with `InnoDB: ` and contains ` started; log sequence number ` while a recovery is open |

Anything else, and every entry without a level, has no event. A recovery is open from a `mariadb.recovery_start` entry until
the next `mariadb.recovery_end` or `mariadb.start` entry in file order (in the journal and syslog, in input order over the
entries of every host, see *Lines from the journal and syslog*); an entry skipped for its time is not classified and does
not change that. Only the header's first line counts. The formats were checked in the MariaDB source at 10.3.39,
10.5.22, 10.6.7, 10.6.11, 10.6.12, 10.6.22, 10.6.28 and 10.11.9; a lifecycle line that another version words differently
gets no event and the entry is still imported. The `mysqld_safe` "ended" line is not classified.

**Time.** MariaDB writes the server's local time without a zone. It is read in `import.time_zone` with the rules of
*Time zone* above (the skipped hour is shifted forward, the repeated hour takes the earlier offset unless that lies more
than ten minutes before the previous resolved header), through `SyslogClock.ResolveLocal`. The year is part of the header.
The digits are checked as integers before any date is built, and the file is skipped entry by entry: an entry whose time
does not exist (month 13, `2026-02-30`, hour 24, minute or second 60) is skipped once with the header's line number as
"invalid date", and one outside 1677-09-21 to 2262-04-11 as "time outside the storable range"; its continuation lines are
neither records nor skips of their own, and a skipped header leaves the previous instant unchanged. With `import.time_zone`
unset, the first header makes `ParseAsync` throw `InvalidOperationException` with "import.time_zone is not set" before its
time is looked at; the file is listed as failed, setting the option and importing again completes it. As for every parser, the
open entry is emitted only at the normal end of input, never when the parse ends by an exception.

**Forged lines (limitation).** MariaDB writes some client text raw into its log: the query of a crash report (up to 64 KiB,
`my_safe_print_str`) and, without any credentials, the user name of a failed login (`Access denied for user '<name>'@...`
at the default `log_warnings` of 2, cut to 128 characters but not filtered). A line break in that text followed by a fake
header splits the entry there and creates an entry of its own with any time, level and event. The format has no escaping
that would tell such a line apart, so this is documented, not prevented: an event is a classification of text and no proof
that MariaDB wrote the line, and later analysis (signature detection, outage reconstruction) must not treat it as one. The
journal and syslog add a second way to forge entries (see *Lines from the journal and syslog*).

**Lines from the journal and syslog.** Under systemd the packaged MariaDB writes its error log to standard error, which the
journal stores line by line (the Debian packaging leaves `log_error` commented out), so a server with the packaged default
has no error log file and this parser imports nothing; its MariaDB output reaches the import through a journal export or a
syslog file. The journal and syslog parsers apply these rules to the records they map, before kernel reports are grouped:

- A **MariaDB line** is a record whose `program` is exactly `mariadbd` or `mysqld` (ordinal comparison) and whose message
  holds no line feed. A line the parser skips (see *System log parsers*) is not seen by these rules: it neither opens nor
  ends an entry.
- A MariaDB line whose message begins with an entry header (one of the four forms of *Entries*, checked on the UTF-8 bytes
  of the decoded message) opens an **entry**. The following MariaDB lines that are no header, have the same `host`,
  `program` and `pid` as the header line (its **key**) and whose time is at most 60 seconds before or after the header
  line's are its continuation lines, as long as the entry's message with them stays within 16,384 UTF-8 bytes. One entry
  is open at a time. It ends at the next MariaDB header line, of any key, or at the normal end of input. It also ends at a
  MariaDB line of its key that is no header and either lies more than 60 seconds before or after the header line or would
  make the message longer than 16,384 bytes. That last case counts the line feed and the empty lines held back before
  the line. A line that ends the entry without being a header is emitted unchanged after the entry's record. Each
  following line of the key is then emitted unchanged too, since no entry is open until the next header. The empty lines
  held back before such a line are trailing lines of the entry and are dropped, as in *Bounds*. The time bound counts
  from the header line, not from the previous member, so no member of an entry lies more than 60 seconds from the
  record's `captured_at`. A crash report whose lines arrive later than that, or that is longer than 16,384 bytes, is
  split, and its later lines are emitted as plain lines. Unlike in the error log, where a continuation line has no time
  of its own and a longer entry is cut to its head and `[N lines omitted]`, no line's text is dropped or reduced to a
  count.
- Every other record, a MariaDB line that is no header and of another key or without an open entry included, is emitted
  unchanged as it comes, before the record of the open entry.
- The record of an entry is the header line's record with four fields replaced: `message` is the header's message (the
  text after the header prefix, as in *Entries*) and the messages of the continuation lines joined by `\n`, at most 16,384
  UTF-8 bytes (decoded, invalid bytes count three) with inner empty lines kept and trailing ones dropped, as in *Bounds*.
  The message is never cut and carries no `[N lines omitted]` marker, because a line that does not fit ends the entry
  (see above). `truncated` is set when the header line or a member line was truncated;
  `priority` is 3 for `ERROR`, 4 for `Warning` and 6 for `Note`, and the header line's own priority for a header without
  a level; `event` is set as in *Events*. `captured_at`, `log`, `source`, `origin`, the sequence, `host`, `program` and
  `pid` stay those of the header line, also for the `mysqld_safe` form.
- The time is the journal entry's `__REALTIME_TIMESTAMP` or the syslog line's time. The time stamp in the message only
  marks the header and is never read: a journal export needs no `import.time_zone` for its MariaDB lines, and a header
  whose date does not exist (`2026-02-30`) still opens an entry. A syslog file needs the option for its year-less times as
  before (see *Time zone*).
- The journal's `PRIORITY` of a service's standard error is the unit's `SyslogLevel=`, the same for every line (`info`,
  6, by systemd's default; not checked on the server); the level of a header replaces it.
- journald stores no entry for an empty line of standard error and forwards none to syslog (`stdout_stream_log` in
  systemd's `src/journal/journald-stream.c`, checked in systemd 249 and the current source). An entry from a real journal
  export, or from a syslog file that journald feeds, therefore has no empty lines, and its message lacks the inner empty
  lines that the error log keeps (a crash report has several); otherwise the same output gives the same message as the
  error log, for every entry within 16,384 bytes. An empty message that does arrive (an entry with an empty `MESSAGE`, a syslog line that ends after
  `mariadbd[<pid>]:`) is a line that is no header, with the empty-line rules of *Bounds*.
- The crash recovery of *Events* is tracked as in the error log: one state per parse, in input order over the MariaDB
  entries of every host, program and pid. Vandox reads one server, whose journal export carries one host. In an input with
  several hosts (a merged journal, a central syslog), another host's `InnoDB: ... started; log sequence number` entry can
  end a recovery it did not start and get `mariadb.recovery_end`, and another host's `mariadb.start` entry closes a
  recovery without an end (limitation).
- As for kernel reports, the open entry is emitted only at the normal end of input, never when the parse ends by an
  exception. The memory these rules hold is the open entry's message, at most 16,384 bytes, and at most two records
  passed from the MariaDB stage to the kernel stage per line, independently of the number and length of the lines.
- The records keep the source type `journal` or `syslog` (not `mariadb`); see *Duplicates* for a journal export and
  syslog files of the same period.
- **Forged lines (limitation).** Any local process can log under the name `mariadbd` or `mysqld` (`logger -t mariadbd`,
  `systemd-cat -t mariadbd`, `openlog()` in a PHP script of a web-space user) and so create entries with any level and
  event, and end the server's open entry with a forged header line. In the journal, `pid` comes from `_PID` when the entry
  has it, which journald sets from the sender's credentials. A forger's header there opens an entry of its own key, so
  the rest of the server's entry is emitted as plain lines, and a forger's lines without a header never join the
  server's entry. That holds only while the forger's entries carry `_PID`: an entry without it takes `SYSLOG_PID`, which
  the sender chooses, so such entries can be forged as in a syslog file. In a syslog file the pid is what the line says, so a forger can use the server's pid. Its lines without
  a header, written within 60 seconds of the server's header line, join the server's open entry and can fill it until the
  server's next line no longer fits. That line ends the entry, and it and the server's following lines are emitted as
  plain lines. Its header line opens an entry of the server's key, which takes in the server's following lines without a
  header within 60 seconds of the forged header, under the forged level and event. A server line is therefore always stored, in an entry or as a plain line, and
  never reduced to a count. In a syslog file, though, it is not necessarily in its own entry, and the lines joined in one
  message can come from different writers. Later analysis that splits a message at its line feeds must not take any line
  of it as the server's. A program name, like an event, is a classification of text and no proof of origin. A separate
  file shaped like a journal export (one a web-space user wrote under a saved `/var/www`) carries any `_PID` its writer
  chose. But the grouping state belongs to one parse of one file, so such a file cannot reach the lines of another file.

## Dovecot log file

**Recognition.** With `log_path` (or `info_log_path`, `debug_log_path`) set to a file, Dovecot writes its own log instead of
syslog: each line is the time stamp of `log_timestamp` (default `%b %d %H:%M:%S `, the server's local time), the process
prefix and the log type, without host or program. The parser `dovecot` has no path option and claims a file by content
only: `Detect` returns a content match when the file's name is not a syslog name (as for the MariaDB error log) **and** the
**first line** of the first 4,096 bytes is a Dovecot line (see *Lines*). A UTF-8 byte order mark at the start of the head
and a `\r` directly before `\n` are removed, and a first line cut by the end of the head counts when it is a Dovecot line
as far as it goes. An empty first line, or one that is no Dovecot line, means no match; no later line is looked at. The
syslog parser claims such a file weakly as well (its grammar reads the prefix as the host); the content match wins. A
Dovecot log under a syslog name stays with the syslog parser, and a copy cut inside its first line or a file written with
another `log_timestamp` is listed as not recognized. Neither the source type `dovecot` nor an event proves that Dovecot
wrote the file: any imported file whose first line has the shape, such as one a web-space user wrote under a saved
`/var/www`, is read as one, and `log` keeps its real path.

**Lines.** A **Dovecot line** is the traditional time stamp of *Syslog* (`Mmm dd HH:MM:SS`, English month abbreviation, day
zero- or space-padded, hour 00 to 23, minute and second 00 to 59), one space, and a message *M* that carries a Dovecot
marker at Dovecot's position: *M* begins with `Debug: `, `Info: `, `Warning: `, `Error: `, `Fatal: ` or `Panic: `, or *M* is
`S: ` + marker + text, where `S`, the text before the first `: `, is non-empty and holds no space. The comparison is
ordinal and case-sensitive, the parse hand-written and linear in the length of a line. Every Dovecot line is one record:
`source` `dovecot`, origin `import`, sequence 0, `log` the file name as the import lists it, `host` and `program` empty,
`pid` 0, `message` *M* as written (prefix and marker included), cut to 16,384 UTF-8 bytes of the decoded text with
`truncated` set, and `truncated` also for a line the reader cut. No lines are joined: Dovecot repeats prefix and type on
every line it writes. Any other non-empty line (a syslog line with a host, a `<PRI>` or an RFC 3339 time, a line whose
prefix holds a space) is skipped as "not a Dovecot line", an empty line as "empty line", with the 1-based line number. A
UTF-8 byte order mark is removed from line 1 only.

**Priority and events.** The rules of the Dovecot family in *Mail lines (Postfix and Dovecot)* apply to every record, with
`Info: ` added: `Debug: ` 7, `Info: ` 6, `Warning: ` 4, `Error: ` 3, `Fatal: ` 2 (`dovecot.fatal`), `Panic: ` 2
(`dovecot.panic`), and a warning or error of `auth-worker(<digits>)` with the anchored database message gets
`dovecot.database_unavailable`. A Dovecot line always carries a marker, so every record has a priority. `Info: ` is read
only here: in syslog and the journal Dovecot writes no such label.

**Time.** The time stamp is local time in `import.time_zone`, with the rules of *Year of a year-less time* and *Time zone*
and their skip reasons ("invalid date", "time outside the storable range", "year unknown: the file has no usable date").
With the option unset, the first Dovecot line makes `ParseAsync` throw `InvalidOperationException` with "import.time_zone
is not set", and the file is listed as failed, as for syslog.

**Forged lines (limitation).** Only Dovecot's log process writes the real file, so `logger -t dovecot` does not reach it.
Remote text reaches a marker position or the database rule there only in the configurations named under *Forged lines* in
*Mail lines (Postfix and Dovecot)* (a widened `auth_username_chars`, a passdb or userdb named `mysql` or `sqlpool`); an
event or priority is a classification of text, not proof of origin.

## Plesk and web server error logs

**Sources.** Four parsers read the error logs of Plesk and the web servers. They are one parser with a source type, a
path option and a line format each, registered in this order:

| Source type | Log | Path option (default) | Format | Claimed by content |
| ----------- | --- | --------------------- | ------ | ------------------ |
| `plesk-panel` | Plesk panel log | `import.plesk_panel_log` (`/var/log/plesk/panel.log`) | Plesk panel | yes |
| `sw-cp-server-error` | error log of `sw-cp-server`, Plesk's web server of the panel | `import.sw_cp_server_error_log` (`/var/log/sw-cp-server/error_log`) | nginx | no |
| `nginx-error` | nginx error log | `import.nginx_error_log` (`/var/log/nginx/error.log`) | nginx | yes |
| `apache-error` | Apache error log | `import.apache_error_log` (`/var/log/apache2/error.log`) | Apache | yes |

The defaults are the paths of a current Plesk on Ubuntu as documented by Plesk; they were not checked on the server, and
older Plesk versions wrote the panel log elsewhere (for example `/usr/local/psa/admin/logs/panel.log`), which the operator
sets in the option. The Apache default is the Debian and Ubuntu path; on a RHEL-family server (AlmaLinux, Rocky Linux,
CentOS) Apache writes `/var/log/httpd/error_log`, which the operator sets in `import.apache_error_log`.

**Configured path.** A file is at a configured path when the path has at least a directory and a file name, the file's
parent directory (the element before the base name in the name the import lists) equals the parent directory of the
configured path, and the file's base name equals the configured base name, alone or followed by `.` and one or more
ASCII digits (`error.log.1`) or by `-` and exactly eight ASCII digits (`error.log-20261010`); the `.gz` of a decompressed
file is removed before. The comparison is ordinal and case-sensitive. Only these two elements count, because a copy or
an archive has any root: `nginx/error.log`, `var/log/nginx/error.log` and `server-1/nginx/error.log` are all at
`/var/log/nginx/error.log`, while a file imported on its own (`error.log`, no directory) is never at a configured path.
The configured paths are compared as text and never opened or resolved on the backend host. A file at a configured path
is claimed above every content signature, so it is read by that parser whatever it holds; content of another format
becomes raw lines (below): an Apache error log at the nginx path is read as `nginx-error` raw lines, not as
`apache-error`. When the paths of two of these parsers match the same file (for example `import.sw_cp_server_error_log`
and `import.nginx_error_log` both set to `/var/log/nginx/error.log`), the one registered first in the table above wins
and the other never sees the file; the configuration does not refuse such paths.

**Content.** `plesk-panel`, `nginx-error` and `apache-error` also claim a file by content: its name is not a syslog name
(as for the MariaDB error log) and the first line of the first 4,096 bytes is a line of their format (a UTF-8 byte order
mark at the start and a `\r` directly before `\n` removed; a first line cut by the end of the head counts when its
beginning is complete). No later line is looked at. `sw-cp-server-error` has nginx's format and claims a file only by its
path: a copy of that log under another parent directory is read as `nginx-error`, with `log` keeping its path. The
per-domain logs of Plesk (`proxy_error_log` in nginx's format and `error_log` in Apache's, under
`/var/www/vhosts/system/<domain>/logs/`) are found by content. The beginnings of the three formats differ from each other
and from the first lines the other parsers claim (nginx starts with `DDDD/`, the Plesk panel log with `[DDDD-`, Apache
with `[` and a weekday), so no first line is claimed by content twice. Neither a source type nor a priority proves which
program wrote a file: any file whose first line has one of the shapes is read as that log, and `log` keeps its real path.

**Lines.** A **line of the format** begins with these fields, checked on the decoded line with ordinal comparisons of ASCII
characters (`D` an ASCII digit). The digits of month, day, hour, minute and second are not range-checked here (see
*Time*), and the weekday is not compared with the date.

| Format | Beginning of the line | Levels and priority |
| ------ | --------------------- | ------------------- |
| Plesk panel | `[DDDD-DD-DD DD:DD:DD`, optionally `.` and 1 to 6 digits, `] `, optionally 1 to 10 digits, `:`, 1 to 64 ASCII letters or digits and a space, then the level | `EMERG` 0, `ALERT` 1, `CRIT` 2, `ERR` 3, `WARN` 4, `NOTICE` 5, `INFO` 6, `DEBUG` 7 |
| nginx | `DDDD/DD/DD DD:DD:DD [`, the level, `]` | `emerg` 0, `alert` 1, `crit` 2, `error` 3, `warn` 4, `notice` 5, `info` 6, `debug` 7 |
| Apache | `[`, a weekday `Mon` to `Sun`, a space, a month `Jan` to `Dec`, a space, `DD DD:DD:DD`, optionally `.` and exactly 6 digits, a space, `DDDD`, `] [`, optionally a module of 0 to 64 ASCII letters, digits or `_` followed by `:`, the level, `]` | as nginx, and `trace1` to `trace8` 7 |

The level is followed by a space or the end of the line; levels, weekdays and months are case-sensitive. nginx's format
was checked in nginx 1.18.0 and Apache's in httpd 2.4.52 (`[module:level]`, `[:level]` without a module name) and 2.2.34
(no fraction, no module); the Plesk panel format is taken from Plesk's documentation (Obsidian writes the process ID and a
request ID before the level, older versions only the level) and is not verified, nor is it verified that `sw-cp-server`
writes nginx's format.

**Records.** Every line of the format is one record: source the source type, origin `import`, sequence 0, `log` the file
name as the import lists it, `host` and `program` empty, `pid` 0, no event, `priority` from the level, and `message` the
text after the time stamp and the one space after it (so the level, a process or request ID, the module and a client
address stay in it), cut to 16,384 UTF-8 bytes of the decoded text (invalid bytes count three) with `truncated` set;
`truncated` is also set for a line the reader cut. No lines are joined.

**Time.** The time stamp is the server's local time with its year. It is read in `import.time_zone` with the rules of
*Time zone* (the skipped hour shifted forward; the repeated hour takes the earlier offset unless that lies more than ten
minutes before the previous line of the format whose time was resolved; raw lines do not count), and the fraction is
added to the resolved instant; an instant that leaves the storable range only by the fraction is outside it. With
the option unset, the first line of the format makes `ParseAsync` throw `InvalidOperationException` with
"import.time_zone is not set", and the file is listed as failed, as for syslog; raw lines before it stay stored. That
Plesk writes the panel log in the server's local time is not verified.

**Raw lines.** A non-empty line that is not a line of the format, or whose time does not exist (month 13, `2026-02-30`,
hour 24) or lies outside the storable range, is kept as a **raw record**: the fields of *Records*, but no `priority` and
the whole line as `message` (a UTF-8 byte order mark removed on line 1 only), cut and marked like a message. Its time is
the capture time of the record emitted before it in the same parse, else the file's modification time when it lies
inside the storable range (for the first lines of a file and of the new part of a continued file, so such a line can be
stored with a time later than it was written). A modification time is taken from the file system or a tar header and
can be forged; one outside the storable range (before 1677-09-21 or after 2262-04-11) counts as no modification time,
as for the year anchor of syslog. With neither time, the line is skipped as "raw line without a time" and gets no
warning. The parser reports every other raw line with a warning and one of the fixed reasons "not a Plesk panel log
line", "not an nginx error log line" (also for `sw-cp-server-error`), "not an Apache error log line", "invalid date" and
"time outside the storable range", and then emits its record. The parser's checks keep a raw record's time and message
within the rules the importer applies to a record; should the importer refuse a raw record for another rule, its line
appears among the skipped lines ("record refused: …") as well as in the count of lines kept raw, because the warning
comes first, and nothing of it is stored. An empty line is skipped as "empty line", with the 1-based line number. Raw lines occur in normal logs: nginx copies the standard
error of a FastCGI application unescaped, so a PHP message with line feeds continues on lines without a time stamp, and
Apache writes startup messages and the standard error of child processes without one. A file of an unknown format at a
configured path becomes raw records only. Memory is independent of the input: a parse keeps the time of the last record
and of the last line of the format.

**Forged lines (limitation).** These logs hold text of clients and web applications (request lines, host names, the
standard error of FastCGI and CGI programs). Where such text reaches the file raw with a line feed (nginx's FastCGI
standard error, the standard error of an Apache child process), the line after it can have the log's format with any
time and level; a PHP message that echoes request data lets a remote client do this. Any local process that may write to
the files can do the same. A priority is a classification of text, not proof that the web server or Plesk wrote the line. Forged lines reach the
signatures of PHP and nginx (*Detection, alerts and reports*, *Log signatures*).

## Legacy monitoring log

**Source.** The parser `legacy-monitor` (`LegacyMonitorParser`, line-framed) reads the log of a shell script that, before the
agent exists, appends a snapshot of the monitored server every five minutes (later every minute): a header line with the
output of `date`, the output of `top -b -n 1` and, in the **new format**, the sections `meminfo`, `pressure`, `vmstat` and
`lsof`. The **old format** has the `top` output only. Both formats were built from the description in issue #20 and tested with
anonymized fixtures (`testdata/logs/legacy-monitor-old.log`, `legacy-monitor-new.log`), not with the real file: the exact line
shapes of `top` (the memory suffixes of the process table, that a command line cut by `-w 512` ends in `+`, that the number of
rows equals `Tasks: N total`) and of the `pressure` lines are not verified, which is why a single bad row does not skip a
snapshot (below).

**Recognition.** The parser claims a file by content (content match) when its name is not a syslog name (as for the MariaDB error
log) and the first line of the head (a UTF-8 byte order mark and a `\r` before `\n` removed) is a valid header. An empty head, a
first line that is no header and a header on line 2 are not claimed. Any process that can write the file can therefore make it
a legacy log by its first line; that is the *Forged lines* limitation below.

**Snapshots and header.** A line beginning with `===== ` is a header line: it ends the open snapshot and opens the next. A valid
header is `===== ` + weekday (`Mon` to `Sun`) + space + month (`Jan` to `Dec`) + space + two characters of day (a space or a
digit, then a digit) + space + `HH:MM:SS` + space + a zone of 1 to 16 characters of `A-Z a-z 0-9 + -` + space + a four-digit year +
` =====`, ASCII and case-sensitive, nothing before or after (the weekday is not compared with the date). A header line that is
not valid gives the skip "invalid snapshot header" at its line. Lines before the first header line are skipped one by one as
"line before the first snapshot".

**Time.** The header is the server's local time. It is read in `import.time_zone` with the offset of the zone interval **whose name
equals the header's zone** (ordinal comparison; NodaTime's TZDB names the intervals of `Europe/Berlin` `CEST` and `CET`, of
`Etc/UTC` `UTC`, of `Asia/Istanbul` `+03`): in the repeated hour at the end of daylight saving time the abbreviation tells the two
passes apart, and when both intervals carry the name, the earlier offset is taken. A local time in the skipped hour, a day that
does not exist, hour 24, minute or second 60 give the skip "invalid date"; a year outside 1677 to 2262 or an instant outside the
storable range gives "time outside the storable range" (the year is checked as a number before any date is built). A zone
name that is not a name of an interval at that local time gives the skip "time zone does not match import.time_zone" at the
header's line. With `import.time_zone` unset, the first valid header makes `ParseAsync` throw `InvalidOperationException` with
"import.time_zone is not set" and the file is listed as failed, as for syslog; a file without a valid header does not need the
option. A zone mismatch **before any header of this parse resolved** throws `InvalidOperationException` with "the time zone of the
first snapshot header does not match import.time_zone" (the option is probably wrong for this file); a mismatch after a header
resolved skips that snapshot. The snapshot of a header that is invalid or cannot be resolved is skipped whole: its lines give
no further skip.

**Top part.** A snapshot expects the lines of `top` in this order, empty lines ignored: `top - …` with `load average:`, `Tasks:`,
`%Cpu(s):`, `<unit> Mem :`, `<unit> Swap:` and the table header `PID USER PR NI VIRT RES SHR S %CPU %MEM TIME+ COMMAND`, then
the rows. The summary lines are split at `,` (in the swap line also at the `.` directly after `used`); each piece is a number, one
or more spaces and a label, so a piece needs no space after the comma or the colon, and the labels are read in any order, each
exactly once. **Numbers** (load, CPU, %CPU, pressure averages) are an integer part of 1 to 15 ASCII digits and an optional
point with 1 to 6 ASCII digits and nothing else (no sign, exponent or comma), so every value is finite and below 10^15. The memory
and swap values are scaled by their unit (`KiB` to `EiB`) in exact integer arithmetic (the integer part is compared with
`ulong.MaxValue >> (10 × n)` before anything is multiplied, then `UInt128` is used), rounded half away from zero; a value that
does not fit 64 bits makes its line unreadable.

A snapshot is **skipped whole**, with one skip at its header line and no record, when it ends (next header line or end of input) before
its table header ("truncated snapshot"), or when its top part is malformed ("malformed snapshot"): a non-empty line before the
table header that is not the next expected line, a summary line that is not read, a section marker before the table header, or
a line after the table header that begins like a summary line (`top - `, `Tasks:`, `%Cpu(s):`, `<unit> Mem `, `<unit> Swap:`) or is the table
header again, which are the signs of another run's output mixed in. The snapshots before and after are emitted.

**Summary metrics.** For a snapshot whose top part is complete the parser emits metrics (origin `import`, source `legacy-monitor`,
`captured_at` the header instant) in the order `load1`, `load5`, `load15`, `cpu_percent` (modes `user`, `system`, `nice`, `idle`,
`iowait`, `irq`, `softirq`, `steal`), `mem_total_bytes`, `mem_free_bytes`, `mem_used_bytes`, `mem_buff_cache_bytes`,
`mem_available_bytes` (from `avail Mem`), `swap_total_bytes`, `swap_free_bytes`, `swap_used_bytes`; the names, units and labels are
in *Metric names* of the [wire format](wire-format.md). The values are as `top` printed them with its rounding; that the
first frame of `top` can show averages since boot for the CPU is not verified.

**Process snapshot.** A row is `PID USER PR NI VIRT RES SHR S %CPU %MEM TIME+ COMMAND`, separated by spaces; COMMAND is the rest of
the line. A token before COMMAND longer than 64 bytes makes the row unreadable. `pid` is 1 to 2,147,483,647, `state` one ASCII
letter, `rss_bytes` the RES column: digits (1 to 20) with an optional fraction (1 to 3 digits) and an optional suffix `k` or none
(KiB), `m`, `g`, `t`, `p`, `e` (the powers of 1,024), scaled exactly; a RES that does not fit 64 bits makes the row unreadable. A
COMMAND ending in `+` is cut by `top`: the `+` is removed and `truncated` set. In the **old format** (no section marker in the
snapshot) `command` is the COMMAND without the `+`, cut to 1,024 bytes (`truncated` is also set for a cut), `cmdline` is empty. In the
**new format** (at least one section marker, known or not) `cmdline` is the COMMAND without the `+`, cut to 2,048 bytes (a cut sets
`truncated`), and `command` is the text inside `[...]` for a bracketed COMMAND, else the part after the last `/` of the first
word with one trailing `:` removed, cut to 1,024 bytes. The two formats therefore name the same process differently
(`apache2` against a name derived from the command line); a new-format snapshot cut before its first section reads as old.
`command` and `program` are **never empty**, because the model refuses an empty one and would refuse the whole snapshot: when the
derived name is empty the normalized COMMAND is used, and when that is empty too (the COMMAND `+`) the COMMAND as printed.
`programs` holds one entry per program (the first space-separated word of `command`, the command itself when that word is
empty) in order of first appearance with `count`, the sums of `cpu_percent` and `rss_bytes` (the sum saturates at 2^64 − 1 instead of
wrapping), no `pss_bytes`, and `rss_overcounted` true exactly when `count` is above 1. `total` is `Tasks: N total`; `complete` is
true only when the number of rows equals `total`, no row was left out and the table was not ended by the end of input. A table with
no row gives no process snapshot.

**A single bad row** does not skip the snapshot: a row that does not parse is left out with the skip "process row not read", a row
whose PID an earlier row of the snapshot has with "duplicate process id", both at the row's line, and `complete` is false.

**Acceptance.** The top part is accepted at the first section marker after the table header or when the snapshot ends after the
table header; only then are its records emitted and its row skips reported (at most 4,097 are held until then). After acceptance a
snapshot cannot fail any more. The records of a snapshot are emitted in this order: the summary metrics, the process snapshot,
then per section in file order the metrics in line order and the connection snapshot.

**Sections** (new format). A line `----- <name> -----` (1 to 32 lower-case ASCII letters) begins a section. An unknown name is skipped
with one skip "unknown section" at the marker, a known section that appears a second time in the snapshot with "repeated
section"; the lines of both are not read and give no skip. Empty lines are ignored. An unreadable line is skipped with "meminfo
line not read", "pressure line not read" or "vmstat line not read" at its line, and the rest of the section is read.

- `meminfo`: `Name: N kB` is the metric `meminfo_bytes` (N × 1,024, unit `bytes`) and `Name: N` the metric `meminfo`, label `field` the name
  (1 to 64 characters of `A-Z a-z 0-9 _ ( )`). N is 1 to 20 ASCII digits within 64 bits; for `kB` N times 1,024 must fit as well. A repeated
  field is unread.
- `pressure`: a line `<res>: some avg10=F avg60=F avg300=F total=N` with an optional ` full avg10=…` part (resource `memory`,
  `cpu` or `io`), or a line `full …` directly after a resource line that had only `some`, gives `pressure_avg10`, `pressure_avg60`,
  `pressure_avg300` and `pressure_stall_microseconds_total` with the labels `resource` and `kind`. A line `n/a` or `<res>: n/a` gives
  no metric and no skip. A repeated resource and kind is unread.
- `vmstat`: a line `<counter> N` for `pswpin`, `pswpout`, `pgmajfault` and `oom_kill` gives `vmstat_total` (label `counter`) and, when
  the previous accepted snapshot of this parse had the counter at an earlier instant and a value not above N, `vmstat_per_second` =
  (N − previous) / seconds between the two headers; other well-formed `<name> N` lines are ignored. The rate **depends on where a
  parse starts**: the first snapshot of a parse has no rate, and a continued import starts with an empty history, so the first
  snapshot of a continuation has none either (a gap of one rate per continuation; the raw `vmstat_total` is complete and a
  rate can be derived from it later). A skipped snapshot is not a predecessor. A repeated counter is unread ("vmstat line not read").
- `lsof` (below).

**Connection snapshot.** An `lsof -ni` line is split at runs of spaces: COMMAND, PID (1 to 2,147,483,647), TYPE the first token from
the fourth on that is `IPv4` or `IPv6`, and from the end an optional `(STATE)`, NAME and NODE (`TCP` or `UDP`, after TYPE). A line of
another shape is skipped with "lsof line not read", also a line over 1,024 bytes (counted on the decoded line, an invalid byte counting three). The header line of `lsof` is ignored wherever it
is. COMMAND escapes `\xHH` are decoded to bytes and the result is read as UTF-8, cut to 1,024 bytes. NAME is `LOCAL` or `LOCAL->REMOTE`,
an endpoint is `ADDR:PORT`. The **address guard** maps only what `lsof` prints, in front of `IPAddress.TryParse`, which would accept
far more (`127.1`, `1`, `0x7f.0.0.1`, `010.0.0.1`, `2130706433`, a zone that is looked up on the host):

- TYPE `IPv4`: `*` (0.0.0.0) or exactly four dot-separated decimal octets of 1 to 3 digits, at most 255 and without a leading zero,
  built from the four bytes. TYPE `IPv6`: `*` (::) or an address in brackets followed by `:PORT`; a zone (`%` to the closing
  bracket) is removed and the address kept; the rest is 2 to 45 characters of `0-9 A-F a-f : .` with a colon; a trailing dotted quad
  (after the last colon, with no point before it) must meet the IPv4 octet rule, so `::ffff:1.2.3.04` is refused, not read as decimal;
  only then `IPAddress.TryParse` is called on that text, and the result must be an IPv6 address without a scope.
- Port: `*` (0), 1 to 5 ASCII digits up to 65,535 without a leading zero, or a service name of 1 to 32 characters of `A-Z a-z 0-9 . _ + -`
  found in a built-in table for the protocol (`LegacyServiceNames`: every name and alias of the `/etc/services` of Ubuntu's `netbase` 6.4,
  never a lookup on the host; a name missing from the table only leaves its socket unmapped). For TCP the state is required and must
  be one of the model's TCP states; for UDP a state token is ignored.
- Every other form is skipped with "socket address not mapped". The protocol is `tcp`, `tcp6`, `udp` or `udp6` from NODE and TYPE.

A socket is unique by the text of its line from TYPE on, and its owner is that of its first line (a socket shared by two processes is
counted once). The snapshot has `states` (per protocol and state over unique sockets, by protocol then state), `processes` (per PID
and decoded command over every socket line, in order of first appearance), `remotes` (per remote address over unique connected
sockets, by count descending then address text), `listeners` (unique sockets without a remote endpoint) and `connections` (unique
sockets with one); lists without entries are left out. It is emitted when the section ends (next section marker, next header line
or end of input). A section without a line gives no record; one with only the header an empty, complete snapshot. `complete` is false
when a line was left out (unread, unmapped, too long or beyond the cap) or the section was closed by the end of input.

**The end of the input.** The log has no end marker, and a copy of a growing file can be cut anywhere. A snapshot closed by the end of
input instead of a header line is therefore emitted as far as it was read: an open `lsof` section gives its connection snapshot with
`complete` false, an open process table (old format) its process snapshot with `complete` false, and the metrics of a cut section as
read; one cut before its table header is skipped whole ("truncated snapshot"). The last snapshot of every file is thus marked incomplete
in its open list. The parser is line-framed, so a continued import starts at a line start: the rest of a snapshot cut by the earlier copy
is skipped line by line as "line before the first snapshot", and that rest is lost.

**Bounds.** A line that is empty or holds only spaces counts as empty everywhere in a snapshot: it is ignored and counts
toward no cap (a line with a tab or another character is not empty). Every non-empty line counts toward the cap of its part, read or not, and at the first line beyond a cap one skip is reported and
every later line of that part is dropped unseen and without a skip: 4,096 row lines per snapshot ("too many processes in a snapshot",
`complete` false, the aggregates are over the rows kept), 4,096 `lsof` lines ("too many sockets in a snapshot", `complete` false), and per
section 128 `meminfo`, 16 `pressure` and 64 `vmstat` lines ("too many lines in a section"). Lines are cut at 16 KiB by the line reader. The only
skips held back are the row skips of a top part not yet accepted (at most 4,097); every section skip is reported at once. Memory does not
grow with the number of snapshots or the number of lines in a snapshot; the worst case is a constant of about 40 MiB for one snapshot (4,096
rows with a 2,048-byte command line and a 1,024-byte command, and 4,096 socket keys and commands, counted in UTF-16), plus one line.
A process or connection snapshot whose stored JSON would be larger than 16,777,216 bytes (`StorageLimits.MaxPayloadBytes`; possible
only for forged text, because these caps allow about 105 MB of escaped JSON, while a legitimate snapshot stays far below) is skipped
with `record refused: data is larger than 16777216 bytes as JSON` and not stored; the file goes on with the next record.

**Errors.** The parse never throws for input. It throws `InvalidOperationException` for the time zone (above), `OperationCanceledException` on
cancellation and what the emitter throws; the open snapshot is not finished then, so the records emitted are a prefix of those of a complete
parse.

**Forged lines (limitation).** Any process that may write the file can forge snapshots with any time, process or connection, and the content
claim makes any file whose first line has the shape of a header a legacy log. Command and user texts are stored as they are and escaped where
they are displayed.

## Result

- **Outcomes per file:** imported, continued (the new part of a file that grew was stored), already imported,
  contained (the content is the start of a content imported before; nothing was stored), not
  recognized (with the reason), failed (with the reason).
  Every file that is not imported appears with its outcome and reason; nothing is skipped silently.
- **Progress** goes as JSON log lines to standard error: one line per finished file (path, outcome including `continued` and `contained`, source type,
  reason, lines, records, skipped), after it a line at level warning for a file with lines kept raw (path, source type,
  number of raw lines), one per 64 MiB hashed, one when the scan is done (files, pending) and one per
  100,000 lines of a large file. Names are attributes, never part of the message.
- **The summary** goes as text to standard output and holds nothing else: the root; files found and counts per
  outcome; lines read (for a continued file only from the continuation point), records stored, lines skipped and lines
  kept raw; the time range of the stored records (RFC 3339, UTC) or that
  none were stored; whether the run was interrupted; then the files continued (path, the name of the content continued,
  the line after which the new part starts and, when there are any, the lines the parser skipped before its first
  record), the files contained in an imported file (path and the name of the content they are the start of), the files
  not recognized and the files that failed with
  their reasons, the files with skipped lines with their first problems (at most ten per file), and the files with
  lines kept raw with their number and first reasons (at most ten per file). Lines kept raw are stored records, so
  they count among the records stored and never change the outcome or the exit code. Like skipped lines, they are
  counted over the whole parse of this run, including the part a resumed run drops because an earlier run stored it.
  The text format belongs to the command and is not an interface.
- **Interruption** (Ctrl-C) anywhere in pass 2 — while a file is parsed, verified or stored, between two files or
  between the entries of an archive — stops the run and still prints the summary of what was done, which says that
  the run was interrupted. The file being imported when the interruption arrives is listed as failed with the
  reason `interrupted` and is resumed by the next run. Entries of an archive not reached are not listed; a plain file
  reached after the interruption may be listed as failed with the reason `interrupted`, and is likewise imported by the
  next run.
- **Exit code:** 0 when every file was imported, continued, contained, already imported or not recognized (a saved `/var/log` always holds
  files no parser claims, such as `wtmp` and `lastlog`); 1 when a file failed, the run was interrupted or ended with
  an error, or the configuration or the database could not be opened (the summary is still printed when there is one);
  2 for a usage error.

## Related decisions

- [0084](../decisions/0084-log-line-record-gets-an-optional-host-field.md) — why `log_line` gets an optional `host`.
- [0085](../decisions/0085-syslog-time-zone-from-import-time-zone-with-embedded-tzdb.md) — why `import.time_zone` has no default, NodaTime and the year rules.
- [0086](../decisions/0086-system-log-parsers-generic-syslog-claim-and-grouped-kernel-reports.md) — why the generic syslog claim and grouped kernel reports.
- [0088](../decisions/0088-mariadb-error-log-entries-by-content-and-lifecycle-events-in-log-line.md) — why the MariaDB error log is detected by content, kept as entries and classified into an `event` field, and why MariaDB lines of the journal and syslog are joined by program, host and pid within 60 seconds of their header and 16,384 bytes, split rather than cut, and keep the time of their line.

- [0091](../decisions/0091-mail-lines-classified-in-the-syslog-and-journal-parsers.md) — why Postfix and Dovecot lines are classified inside the syslog and journal parsers by their severity marker, which lines count as an unavailable database, and why Dovecot's own log file gets a parser `dovecot` claimed by its first line.
- [0092](../decisions/0092-error-logs-by-content-or-configured-path-unreadable-lines-kept-raw.md) — why the Plesk and web server error logs have path options that rank above content, how a configured path is matched, and why unreadable lines are kept as raw records with a warning.

- [0093](../decisions/0093-legacy-monitoring-log-snapshots-by-header-zone-and-whole-snapshot-validity.md) — why the legacy monitoring log is read snapshot by snapshot, timed by the zone abbreviation of its header within `import.time_zone`, and why a snapshot with a broken top part is skipped whole while a single bad row is not.

- [0014](../decisions/0014-log-import-is-a-core-component.md) — why historical import and log shipping are core.
- [0021](../decisions/0021-no-pseudonymization-of-log-data.md) — why log data is stored unchanged.
- [0069](../decisions/0069-log-import-idempotent-per-file-content-hash-with-resumable-batches.md) — why content hashes, two passes, resume by count and the continuation of a grown file.
- [0072](../decisions/0072-vandoxd-import-sub-command-output-and-exit-codes.md) — why the sub-command, the output channels and the exit codes.
- [0079](../decisions/0079-log-parsing-and-import-in-the-backend-without-following-links.md) — why streaming, content-based formats and kernel-level link-free file access.

## Not here

- The storage of records and of the import state: storage area.
- Why imported records never raise alerts: detection area.
- The service entry point (`-healthcheck`, `-version`, shutdown): ingest and backend host area.

## Implementation

`Vandox.Import` (`Importer`, `Scanner`, `ImportLimits`), `Vandox.Core.LogParsing` (`ILogParser`, `ParserRegistry`,
`LogLineReader`, `BuiltInParsers`, `JournalExportParser`, `JournalExportReader`, `SyslogParser`, `SyslogLine`,
`SyslogClock`, `KernelReportGrouper`, `SystemLogGrouper`, `MailLineClassifier`, `MailEvents`, `DovecotLogParser`, `DovecotLine`, `MariaDbLineGrouper`, `MariaDbErrorLogParser`,
`MariaDbParseSession`, `MariaDbLine`, `MariaDbMessage`, `MariaDbEventClassifier`, `MariaDbEvents`, `Utf8Text`, `ErrorLogParser`,
`ErrorLogLine`, `ErrorLogFormat`, `ErrorLogPaths`, `LogPath`, `LegacyMonitorParser`, `LegacyMonitorHeader`, `LegacyMonitorSnapshot`, `LegacyProcessTable`, `LegacyConnectionTally`, `LegacyMetrics`, `LegacyTopLine`, `LegacyTopRow`, `LegacyProcessNames`, `LegacyProcLine`, `LegacyPressure`, `LegacyLsofLine`, `LegacyServiceNames`, `LegacyCounter`, `LegacyParseState`), `Vandox.Core.Model` (`StorableTime`), `Vandox.Core.IO` (`SecureRoot`, `FileProbe`), `Vandox.Backend/Cli` (`ImportCommand`,
`ImportSummaryWriter`, `ImportProgressLogger`). The checklist for a new parser is in `.squad/project.md` (*Integration surface*).
