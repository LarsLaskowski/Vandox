# 0079: Log parsing and import in the backend: statx/openat2 file access, same limits and guarantees

- **Status:** Accepted
- **Date:** 2026-10-06
- **Source:** Product Manager request: move the backend to .NET 10 with Blazor, keep the agent in Go
- **Supersedes:** —

## Context

The log parser framework and the import ([0014](0014-log-import-is-a-core-component.md),
[0069](0069-log-import-idempotent-per-file-content-hash-with-resumable-batches.md)) are backend code and move to
C#. They were first written in Go (issue #15), and the rules they settled stay in force.

*Import input* (issue #15): "a directory or archive (`.tar`, `.tar.gz`, single `.gz` rotations)", processed as
streams. The maintainer saved `/var/log` with rotated `.gz` files, a journal export and the legacy log on the
backend host; the compose file mounts `./import` read-only at `/import` (0060), and the import runs in the
service's container through `docker exec`, inside its 512 MiB memory limit. The content comes from the
monitored server, which may have been compromised, so names and contents are external input (*Security areas* 9:
file writes and paths derived from external input, explicitly the log import; 10: parsing of external input — an
error or a skipped record, never a crash, an unbounded allocation or a hang). Go's `os.Root` gave the import
link- and escape-safe file access; .NET has no equivalent.

*Parsers* (issue #15): "detect the source type per file (by path/name and content sniffing) and hand it to the
matching parser". The parsers are separate issues: #16 (journal export, syslog, kern.log; year-less syslog
timestamps need the file's name and modification time), #17 (MariaDB error log), #18 (mail.log), #19 (Plesk and
web server error logs, "unknown formats kept as raw lines"), #20 (legacy `top`/`lsof` log, which yields metrics
and snapshots, not log lines). Every parser reads lines and must keep memory flat.

## Options considered

1. **Plain `File.Open` plus path checks** — races between check and open; links followed.
2. **P/Invoke to the kernel** — `openat2` with `RESOLVE_BENEATH | RESOLVE_NO_SYMLINKS` where available, `statx`
   to classify entries without following links, and as the fallback for kernels without `openat2` (NAS kernels
   such as 4.4) an `lstat` of every directory of the path, a refusal of `..` and an `O_NOFOLLOW`/`O_NONBLOCK` open of
   the last element.

Decisions of the Go version that carry over:

- *Extract archives to a temporary directory, then import the directory* — rejected: it writes untrusted names
  and content to disk (path traversal, links, disk space) and needs a writable location in a read-only
  container. **Stream everything, names as labels only** — chosen; archives are read sequentially (twice, 0069).
- *Recognize compression by file extension* — rejected (`.tgz`, no extension and misnamed files go wrong) for
  **content (magic bytes)**; the extension is only a hint for parsers.
- *Any nesting of archives and gzip* — rejected (unbounded work per byte of input, more paths to get wrong) for
  **one gzip layer per file, tar archives at the top level and in the root directory tree, never inside another
  archive**.
- *Following links and special files* — rejected (a link in a copied `/var/log` could point anywhere the
  container can read, and a FIFO blocks the open): they are listed as not imported.
- *Opening below the root with `O_NOFOLLOW` only* — rejected (it protects only the last component; a directory
  swapped for a link higher up still redirects the read), for resolving every component without following links
  below one root.
- *Counting the input against the entry limit after the scan* — rejected (the scan's list and a directory's
  entries grow without bound before the check, e.g. a tar.gz of millions of tiny entries) for **counting while
  scanning**, stopping at the first entry over the limit.
- *Parser interface inside the importer* — rejected for a shared parsing namespace that both the importer and
  the parsers depend on, so the agent can reuse the parsers for log shipping (#37). *A global registry filled
  by static initializers* — rejected (registration order, the tie-breaker, depends on file names and imports,
  tests share global state, a parser is active by being linked) for **an explicit list** passed to the registry,
  in priority order, which tests replace with their own. *First parser whose `Detect` returns true* and
  *ambiguity as an error* — rejected (a generic raw-line fallback placed early shadows specific parsers; the
  overlapping name patterns of `syslog` and `kern.log` would fail hard) for **a confidence per parser, highest
  wins, ties by registration order** (a content signature beats a name match; generic parsers claim files
  weakly).

## Decision

Option 2. `Vandox.Core.IO` (`SecureRoot`, `FileProbe`, `OpenHow`) opens everything below the import root
through these calls; links and special files (FIFO, device, socket) are listed and never followed or opened
as data; names and paths are labels only.

Guarantees, unchanged from the Go version:

- **Nothing is extracted or written.** Entry names and relative paths are cleaned and used only as labels in the
  summary (quoted), in logs (quoted attribute values, because the JSON handler writes C1 and format characters
  raw; 0072) and in `import_files.name` (invalid UTF-8 replaced, cut to 1,024 bytes). A path or entry name longer
  than 1,024 bytes is listed as failed.
- **Root:** the operator names it and it is resolved once and checked without following the last link; a
  directory is walked recursively in lexical order, a regular file is one input file; anything else (FIFO,
  socket, device, missing) is an error of the run and is never opened.
- **Format by content:** gzip magic is decompressed (all members), one layer only (gzip inside is listed as
  "compressed twice"); `ustar` at offset 257 is a tar archive; bzip2, xz, zstd, lz4, zip and 7z signatures are
  listed as "unsupported format"; empty content is listed as "empty"; everything else goes to the parser registry,
  and a file no parser claims is listed as "no parser recognized the file". V7 tar without a magic is not
  supported; a `.gz` suffix is removed from the parser's `File.Name` when the file was decompressed.
- **Tar entries:** regular files (including sparse) are read; directories and PAX global headers are not
  listed; links, devices, FIFOs and unknown types are listed; an entry that is itself a tar archive is listed as
  "archive inside an archive (not opened)"; a corrupt header ends the archive, which is listed as failed.
- **Limits:** at most 20,000 entries per run (`ImportLimits.MaxFiles`; every directory entry of any kind and every
  tar header except PAX global headers counts), enforced while scanning: the entry that brings the count over
  the limit ends pass 1 at once, is not read, detected or hashed, and nothing has been written. A head of 4,096
  bytes (`SniffBytes`) for detection, lines of at most 16 KiB (`LogLineReader`), batches of at most 2,000 records
  and the records from at most 4 MiB of input, whichever comes first. No limit on the size of a file or of
  decompressed data: every read checks the cancellation token, so SIGINT/SIGTERM to the import process stops it
  cleanly and it resumes later (0069); a kill leaves the database consistent because each batch is one
  transaction with the import's compare-and-set.
- **Permissions:** the import runs as the container's user (65532) and reads only what that user may read; an
  unreadable file is listed as failed. The README tells the operator to grant read access to 65532 only
  (`chown`/`chmod u+rX` or `setfacl`) and never to make the copy world-readable: a copied `/var/log` holds
  `0640 root:adm` files such as `auth.log`. Modification times are passed in UTC; one outside the range storable
  as Unix nanoseconds (e.g. a forged tar `mtime`) is stored as unknown (0069).
- Every file that is not imported appears in the summary with its outcome and reason; nothing is skipped
  silently.

Parsers: `ILogParser` (`Type`, `Detect(file, head)` returning a confidence `NoMatch < MatchName < MatchContent`,
`Parse(file, stream, emitter, cancellationToken)`) and `ParserRegistry` (`Vandox.Core.LogParsing`) pick the parser
with the highest confidence, the earlier registered one on a tie, or none. The registry refuses null parsers,
duplicate types and types outside `^[a-z][a-z0-9._-]{0,63}$` (also used for `import_files.source_type`). `Parse` is
deterministic for the same content and `File` (resume, 0069, passes a resumed content the `File` of its first
import, so `Parse` may derive a year from the name and modification time), emits records of origin `import` with
UTC capture times, keeps memory bounded independently of the input size, honors cancellation and returns the
emitter's error; skip reasons are fixed texts without input content. The importer validates every record and counts
refused ones as skipped, so a parser bug cannot fail a whole batch. `LogLineReader` is the shared bounded line
reader (lines without `\n`/`\r\n`, cut at 16 KiB at a UTF-8 boundary with a `truncated` flag, the rest discarded,
so a file without newlines cannot grow memory); a scripted test parser is production code for the coverage gate
with its own test. The import runs in two passes with batches that resume by count, keyed by the SHA-256 of the
decompressed content (0069).

## Consequences

- Linux only for the import; other platforms are not supported by the backend image anyway.
- #16–#20 each add a parser type and one line in the explicit list; the importer does not change
  (`.squad/project.md`, *Integration surface*). The order of that list is part of the behavior (ties), so a test
  pins it once there are parsers. A parser that is not deterministic breaks resuming. The interface works on
  streams of whole files; the agent's live shipping (#37) reuses the line-level logic but needs its own entry
  point for a journal cursor.
- The NAS kernel fallback is weaker than `openat2` (the application checks the path, the kernel does not enforce it,
  and a link swapped in between check and open is not caught) and is stated openly in `.squad/project.md`; the
  tests of `SecureRoot` and of the importer run both paths (`SecureRoot.OpenWithoutKernelResolution`).
- A tar entry with a PAX `size` record (files above 8 GiB, or a hostile archive) is refused with a reason: the tar reader
  allocates what extended headers declare, and a guard that mirrored the reader's size handling could be put out of step
  by crafted values, so `TarHeaderGuardStream` trusts only the size field of each header and caps extended headers at 1 MiB.
- Memory stays flat for any file size and number of entries. A gzip bomb or a huge sparse entry is not refused; it
  costs time and stops only by cancellation, and a bomb of **valid lines** fills the storage directory until the
  operator stops it (the README says to watch the progress lines and press Ctrl-C). A size limit can be added if
  it ever matters.
- Rotations compressed with xz, zstd or bzip2, zip files and nested archives are listed, not imported; adding a
  decompressor is a new decision. Inputs with more than 20,000 entries must be split; the error says so before
  anything is written. A symbolic link inside a saved `/var/log` is listed; the operator copies the target instead.
