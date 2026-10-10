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
  parsers*) and for the local times of the MariaDB error log (see *MariaDB error log*); a value that is no zone ID is refused at start-up with the line of the key and without the value. The
  example file holds it commented out.

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
  directory until the operator presses Ctrl-C.

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
  imported up to the hashed size; the appended lines come with a later import.
- Limits of the guarantee: a file that **grew** since its import has another hash and is imported again as a whole,
  storing the overlapping lines twice. A file whose hashed part changes while it is imported fails; the records
  written before the change was detected stay stored under the original hash. Records do not reference their import
  file, so one import cannot be removed or redone.

## Parsers

A parser turns the lines of one file into records. The importer and every parser fulfill this contract:

- A parser has a type name matching `^[a-z][a-z0-9._-]{0,63}$`; the type is stored with the imported file.
- `Detect` receives the file (cleaned name and modification time) and the first 4,096 bytes and returns a
  confidence: none, name match or content match. The parser with the highest confidence wins, the one registered
  earlier on a tie. A content signature beats a name match; generic parsers claim files weakly.
- The parsers are registered in an explicit, ordered list (the order is part of the behavior). The registry refuses
  a missing parser, a duplicate type or an invalid type name.
- `Parse` reads a stream and emits records of origin `import` with capture times in UTC. It is **deterministic**
  for the same content, file and configuration (the importer resumes by dropping the first *n* emitted records, so the
  records emitted before a failure are a prefix of those a later complete parse emits), emits the records in a
  deterministic order (file order, except that a parser that combines lines into one record may emit records of lines
  written inside it first), keeps memory bounded independently of the input size, honors cancellation and returns the
  error of the emitter. Skip reasons are fixed texts that never contain input content.
- The importer validates every record; a refused record is counted as skipped, so a parser bug cannot fail a whole
  batch. Stored source type: a changed detection of a resumed content fails the file, a changed parser version is
  not noticed.
- Lines are read with a bounded line reader: lines without the line ending, cut at 16 KiB at a UTF-8 boundary with a
  truncated flag, the rest of the line discarded, so a file without line breaks cannot grow memory.
- The file name handed to a parser has a `.gz` suffix removed when the content was decompressed.
- A new parser is one type and one entry in the list; the importer does not change.

## System log parsers

`BuiltInParsers.Create(timeZone)` is the list `vandoxd import` registers when no hook replaces it: `journal`
(`JournalExportParser`), then `mariadb` (`MariaDbErrorLogParser`, see *MariaDB error log*), then `syslog`
(`SyslogParser`). The journal parser claims a file by content (a head that begins
with a `__CURSOR=` or `__REALTIME_TIMESTAMP=` line and holds a `__REALTIME_TIMESTAMP=` line; the file name never
matters); the syslog parser claims weakly (name match) a file named `syslog` or `kern.log` with an optional `.N` or
`-YYYYMMDD` suffix, or whose first line has a syslog header. Both parsers produce `log_line` records with origin `import`
and sequence 0 and pass them through two stages in this order: MariaDB entries (see *Lines from the journal and syslog*
under *MariaDB error log*), then kernel reports (see *Kernel reports* below).

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
ends after a line containing `Out of memory: Killed process`, `Memory cgroup out of memory: Killed process` or `Out of
memory and no killable processes` (OOM), or `---[ end trace ` (`cut here`), or at a new start line, at a same-host kernel
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

**Duplicates.** Ubuntu's rsyslog writes `kern.*` to `kern.log` and `*.*` without auth to `syslog` (from the packaged
configuration, not checked on the server), so every kernel line is in both files and importing both stores it twice.
There is no deduplication across sources. Importing a journal export and the syslog files of the same period stores
every message twice as well (sources `journal` and `syslog`), MariaDB entries included.

## MariaDB error log

**Recognition.** The parser `mariadb` has no path option: `vandoxd import` reads copies and archives under any layout, so a
server path would match nothing, and the file is found by its content. `Detect` returns a content match only when the
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
  server's entry. In a syslog file the pid is what the line says, so a forger can use the server's pid. Its lines without
  a header, written within 60 seconds of the server's header line, join the server's open entry and can fill it until the
  server's next line no longer fits. That line ends the entry, and it and the server's following lines are emitted as
  plain lines. Its header line opens an entry of the server's key, which takes in the server's following lines without a
  header within 60 seconds of the forged header, under the forged level and event. A server line is therefore always stored, in an entry or as a plain line, and
  never reduced to a count. In a syslog file, though, it is not necessarily in its own entry, and the lines joined in one
  message can come from different writers. Later analysis that splits a message at its line feeds must not take any line
  of it as the server's. A program name, like an event, is a classification of text and no proof of origin. A separate
  file shaped like a journal export (one a web-space user wrote under a saved `/var/www`) carries any `_PID` its writer
  chose. But the grouping state belongs to one parse of one file, so such a file cannot reach the lines of another file.

## Result

- **Outcomes per file:** imported, already imported, not recognized (with the reason), failed (with the reason).
  Every file that is not imported appears with its outcome and reason; nothing is skipped silently.
- **Progress** goes as JSON log lines to standard error: one line per finished file (path, outcome, source type,
  reason, lines, records, skipped), one per 64 MiB hashed, one when the scan is done (files, pending) and one per
  100,000 lines of a large file. Names are attributes, never part of the message.
- **The summary** goes as text to standard output and holds nothing else: the root; files found and counts per
  outcome; lines read, records stored and lines skipped; the time range of the stored records (RFC 3339, UTC) or that
  none were stored; whether the run was interrupted; then the files not recognized and the files that failed with
  their reasons, and the files with skipped lines with their first problems (at most ten per file). The text
  format belongs to the command and is not an interface.
- **Exit code:** 0 when every file was imported, already imported or not recognized (a saved `/var/log` always holds
  files no parser claims, such as `wtmp` and `lastlog`); 1 when a file failed, the run was interrupted or ended with
  an error, or the configuration or the database could not be opened (the summary is still printed when there is one);
  2 for a usage error.

## Related decisions

- [0084](../decisions/0084-log-line-record-gets-an-optional-host-field.md) — why `log_line` gets an optional `host`.
- [0085](../decisions/0085-syslog-time-zone-from-import-time-zone-with-embedded-tzdb.md) — why `import.time_zone` has no default, NodaTime and the year rules.
- [0086](../decisions/0086-system-log-parsers-generic-syslog-claim-and-grouped-kernel-reports.md) — why the generic syslog claim and grouped kernel reports.
- [0088](../decisions/0088-mariadb-error-log-entries-by-content-and-lifecycle-events-in-log-line.md) — why the MariaDB error log is detected by content, kept as entries and classified into an `event` field, and why MariaDB lines of the journal and syslog are joined by program, host and pid within 60 seconds of their header and 16,384 bytes, split rather than cut, and keep the time of their line.

- [0014](../decisions/0014-log-import-is-a-core-component.md) — why historical import and log shipping are core.
- [0021](../decisions/0021-no-pseudonymization-of-log-data.md) — why log data is stored unchanged.
- [0069](../decisions/0069-log-import-idempotent-per-file-content-hash-with-resumable-batches.md) — why content hashes, two passes and resume by count.
- [0072](../decisions/0072-vandoxd-import-sub-command-output-and-exit-codes.md) — why the sub-command, the output channels and the exit codes.
- [0079](../decisions/0079-log-parsing-and-import-in-the-backend-without-following-links.md) — why streaming, content-based formats and kernel-level link-free file access.

## Not here

- The storage of records and of the import state: storage area.
- Why imported records never raise alerts: detection area.
- The service entry point (`-healthcheck`, `-version`, shutdown): ingest and backend host area.

## Implementation

`Vandox.Import` (`Importer`, `Scanner`, `ImportLimits`), `Vandox.Core.LogParsing` (`ILogParser`, `ParserRegistry`,
`LogLineReader`, `BuiltInParsers`, `JournalExportParser`, `JournalExportReader`, `SyslogParser`, `SyslogLine`,
`SyslogClock`, `KernelReportGrouper`, `SystemLogGrouper`, `MariaDbLineGrouper`, `MariaDbErrorLogParser`,
`MariaDbParseSession`, `MariaDbLine`, `MariaDbMessage`, `MariaDbEventClassifier`, `MariaDbEvents`, `Utf8Text`), `Vandox.Core.Model` (`StorableTime`), `Vandox.Core.IO` (`SecureRoot`, `FileProbe`), `Vandox.Backend/Cli` (`ImportCommand`,
`ImportSummaryWriter`). The checklist for a new parser is in `.squad/project.md` (*Integration surface*).
