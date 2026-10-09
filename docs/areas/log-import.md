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
  for the same content and file, keeps memory bounded independently of the input size, honors cancellation and
  returns the error of the emitter. Skip reasons are fixed texts that never contain input content.
- The importer validates every record; a refused record is counted as skipped, so a parser bug cannot fail a whole
  batch. Stored source type: a changed detection of a resumed content fails the file, a changed parser version is
  not noticed.
- Lines are read with a bounded line reader: lines without the line ending, cut at 16 KiB at a UTF-8 boundary with a
  truncated flag, the rest of the line discarded, so a file without line breaks cannot grow memory.
- The file name handed to a parser has a `.gz` suffix removed when the content was decompressed.
- A new parser is one type and one entry in the list; the importer does not change.

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
`LogLineReader`), `Vandox.Core.IO` (`SecureRoot`, `FileProbe`), `Vandox.Backend/Cli` (`ImportCommand`,
`ImportSummaryWriter`). The checklist for a new parser is in `.squad/project.md` (*Integration surface*).
