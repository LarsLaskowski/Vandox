# 0071: The log import reads its input as streams without following links or extracting anything; formats are sniffed, one gzip layer, no nested archives, fixed limits

- **Status:** Proposed
- **Date:** 2026-10-06
- **Source:** Issue #15
- **Supersedes:** —

## Context

Issue #15: the input is "a directory or archive (`.tar`, `.tar.gz`, single `.gz` rotations)", processed as
streams. The maintainer saved `/var/log` with rotated `.gz` files, a journal export and the legacy log on the
backend host; the compose file mounts `./import` read-only at `/import` (0060), and the import runs in the
service's container through `docker exec`, inside its 512 MiB memory limit. The content of these files comes
from the monitored server, which may have been compromised, so names and contents are external input
(*Security areas* 9: file writes and paths derived from external input, explicitly the log import; 10:
parsing of external input — an error or a skipped record, never a crash, an unbounded allocation or a hang).

The consumers decide which forms exist: `filepath.WalkDir` reports entries with `Lstat` semantics and does
not follow links; `os.Open` on a FIFO blocks until a writer appears; `compress/gzip` reads concatenated
members by default and reports header, checksum and trailing-data errors; `archive/tar` resolves PAX and GNU
long-name headers itself, returns PAX global headers as entries, converts `TypeRegA`, expands PAX sparse
files, keeps `TypeGNUSparse`, and limits special headers to 1 MiB. Logrotate on Ubuntu compresses with
gzip; other compressors exist.

## Options considered

1. **Extract archives to a temporary directory, then import the directory** — one code path for both; but
   writes untrusted names and content to disk (path traversal, links, disk space) and needs a writable
   location in a read-only container.
2. **Stream everything, names as labels only** — no write at all; archives are read sequentially (twice,
   0069).

Recognizing compression and archives:

- a. **By file extension** — predictable, but `.tgz`, files without extension and misnamed files go wrong.
- b. **By content (magic bytes)** — independent of names; the extension is only a hint for parsers.

Depth:

- i. **Any nesting** (archives in archives, gzip in gzip) — complete, but unbounded work per byte of input and
  more paths to get wrong.
- ii. **One gzip layer per file; tar archives at the top level and in the root directory tree, never inside
  another archive** — covers a saved `/var/log` (rotations in a directory or inside one tar.gz) and a
  directory holding several archives.

Links and special files: follow them (a link in a copied `/var/log` could point anywhere the container can
read) or list them as not imported.

## Decision

Options 2, b and ii; links and special files are listed, never followed or opened.

- **Root**: a directory is walked recursively in lexical order; a regular file is one input file; a symbolic
  link as root is resolved once (the operator named it); anything else (FIFO, socket, device, missing) is an
  error of the run and is never opened.
- **Below the root nothing is followed**: a symbolic link (file or directory) is listed as "symbolic link
  (not followed)"; a FIFO, socket or device as "not a regular file"; an unreadable directory or file as
  failed with the OS error. Files are opened with `openRegular`: `O_RDONLY|O_NOFOLLOW|O_NONBLOCK|O_CLOEXEC`,
  then `Fstat` on the descriptor must report a regular file, so a swap between listing and opening can
  neither redirect the read nor block. (`openRegular` is unix-only; elsewhere it returns
  `errors.ErrUnsupported`.)
- **Nothing is extracted.** Entry names and relative paths are cleaned (`path.Clean`, no leading `/`) and used
  only as labels: in the summary (quoted with `%q`), in logs (JSON attributes) and in `import_files.name`
  (invalid UTF-8 replaced, cut to 1024 bytes). A path or entry name longer than `MaxPathBytes` (1024 bytes)
  is listed as failed.
- **Sniffing** (`sniffFormat`, on the first bytes): `1f 8b 08` is gzip and is decompressed (all members), one
  layer only — gzip again inside is listed as "compressed twice"; `ustar\x0000` or `ustar  \x00` at offset
  257 is a tar archive; bzip2, xz, zstd, lz4, zip and 7z signatures are listed as "unsupported format:
  <name>"; empty content is listed as "empty"; everything else goes to the parser registry (0070), and a file
  no parser claims is listed as "no parser recognized the file". Tar without a magic (V7) is not supported.
  A file name ending in `.gz` loses that suffix in the parser's `File.Name` when it was decompressed.
- **Tar entries**: regular files (including converted `TypeRegA` and PAX sparse files) are read; directories
  and PAX global headers are not listed; symbolic links, hard links (whose content is the linked entry),
  character and block devices, FIFOs, `TypeCont`, `TypeGNUSparse` and unknown types are listed; an entry
  whose content is itself a tar archive is listed as "archive inside an archive (not opened)". A corrupt
  header ends the archive: entries before it are handled, the archive is listed as failed.
- **Limits**: at most `MaxFiles` = 20,000 files per run (counted in pass 1, before anything is written;
  more is an error of the run); a head of `logparse.SniffBytes` = 4096 bytes; lines of at most 16 KiB
  (`logparse.LineReader`, 0070); batches of at most 2,000 records (`DefaultBatchRecords`) and the records from
  at most 4 MiB of input (`DefaultBatchBytes`), whichever comes first. No limit on the size of a file or of
  decompressed data: every read checks the context, so the operator can stop an import (Ctrl-C,
  `docker stop`) and resume it later (0069).
- Every file that is not imported appears in the summary with its outcome and reason; nothing is skipped
  silently.

## Consequences

- No input can make the import write a file, read outside the named root through a link, or block on a FIFO.
- Memory stays flat for any file size: the scan's per-file list (bounded by `MaxFiles` and `MaxPathBytes`),
  one head, one line, one batch.
- A gzip bomb or a huge sparse entry is not refused; it costs time and stops only by the context. A size
  limit can be added if it ever matters.
- Rotations compressed with xz, zstd or bzip2, zip files and nested archives are listed, not imported; adding
  a decompressor (bzip2 is in the standard library, the others are dependencies) is a new decision.
- Inputs with more than 20,000 files must be split; the error says so before anything is written.
- A symbolic link inside a saved `/var/log` (rare; e.g. a link to a log elsewhere) is listed; the operator
  copies the target instead.
