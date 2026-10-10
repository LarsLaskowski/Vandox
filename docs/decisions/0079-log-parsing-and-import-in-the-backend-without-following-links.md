# 0079: Log parsing and import in the backend: statx/openat2 file access, same limits and guarantees

- **Status:** Accepted
- **Date:** 2026-10-06
- **Area:** Log import
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

Option 2: everything below the import root is opened through `openat2` with beneath-root and no-symlink resolution
(`statx` classifies entries without following links; older kernels use a weaker per-directory fallback), and
archives are streamed, never extracted. The guarantees of the Go version carry over unchanged. Parsers are chosen
by a confidence per parser from an explicit, ordered list. The limits, the handling of formats, archives and
links, and the parser contract are in [Log import](../areas/log-import.md), *Input*, *Safe file access* and
*Parsers*.

## Consequences

- Linux only for the import; other platforms are not supported by the backend image anyway.
- Each new parser adds a type and one line in the explicit list (`.squad/project.md`, *Integration surface*); the
  order of that list is part of the behavior, so a test pins it. A parser that is not deterministic breaks resuming.
- The fallback for kernels without `openat2` is weaker (the application checks the path, the kernel does not enforce
  it, and a link swapped in between check and open is not caught). It is stated openly in `.squad/project.md`, and
  the tests run both paths.
- Memory stays flat for any file size and number of entries. A decompression bomb or a huge sparse entry is not
  refused; it costs time and stops only by cancellation, and a bomb of valid lines fills the storage directory until
  the operator stops it. A size limit can be added if it ever matters.
- Rotations compressed with xz, zstd or bzip2, zip files and nested archives are listed, not imported; adding a
  decompressor is a new decision. Inputs with more than 20,000 entries must be split.
