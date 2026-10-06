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

The consumers decide which forms exist: `(*os.File).ReadDir` reports entry types with `Lstat` semantics
and does not follow links (`filepath.WalkDir` does the same but reads each directory completely before its
caller sees an entry); `O_NOFOLLOW` refuses a link only as the last path component, while `os.Root`
opens every component with `openat(…, O_NOFOLLOW)` and resolves a link only when it is relative and stays
inside the root; `os.Open` on a FIFO blocks until a writer appears; `compress/gzip` reads concatenated
members by default and reports header, checksum and trailing-data errors; `archive/tar` resolves PAX and GNU
long-name headers itself, returns PAX global headers as entries, converts `TypeRegA`, expands PAX sparse
files, keeps `TypeGNUSparse`, limits special headers to 1 MiB, and — with `GODEBUG=tarinsecurepath=0` —
returns a valid header together with `tar.ErrInsecurePath` for a non-local name. `slog`'s JSON handler
escapes `"`, `\`, characters below U+0020 and U+2028/U+2029, but writes C1 controls (e.g. U+009B, which
terminals treat like `ESC [`), DEL and format characters (e.g. U+202E) raw. Logrotate on Ubuntu compresses with
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

Opening below the root, against a swap between listing and opening:

- x. **Paths with `O_NOFOLLOW`** — protects only the last component; a directory swapped for a link
  higher up still redirects the read outside the root. Rejected (security plan review).
- y. **Own `openat` walk with directory descriptors** — exact, but needs `golang.org/x/sys/unix` as a direct
  dependency or per-OS `syscall` code.
- z. **One `os.Root` for the run** — standard library; every component is opened without following and a
  link is resolved only inside the root, so nothing outside the root is ever read; a swapped in-root link
  can make another input file be read under the listed name.

Counting the input against the entry limit:

- after the scan — simple, but the scan's list and a directory's entries grow without bound before the
  check (a tar.gz of millions of tiny entries). Rejected (security plan review).
- **while scanning**, stopping at the first entry over the limit.

## Decision

Options 2, b, ii and z, the limit counted while scanning; links and special files are listed, never
followed or opened.

- **Root**: resolved once with `filepath.EvalSymlinks` (the operator named it) and checked with `Lstat`; a
  directory is opened as an `os.Root` and walked recursively in lexical order; a regular file is one input
  file, opened through an `os.Root` of the directory holding it (which must be readable); anything else
  (FIFO, socket, device, missing) is an error of the run and is never opened.
- **Below the root nothing is followed when listing**: directories are read with `(*os.File).ReadDir`; a
  symbolic link (file or directory) is listed as "symbolic link (not followed)"; a FIFO, socket or device
  as "not a regular file"; an unreadable directory or file as failed with the OS error. Every open goes
  through the run's `os.Root`: files with `openRegular` (`O_RDONLY|O_NONBLOCK`, then `Fstat` on the
  descriptor must report a regular file), directories with `openDir` (`O_RDONLY|O_DIRECTORY|O_NONBLOCK`).
  So a swap of **any** path component between listing and opening can neither redirect the read outside
  the root nor block; at most a swapped link inside the root makes another input file be read under the
  listed name. (`openRegular` and `openDir` are unix-only; elsewhere they return `errors.ErrUnsupported`.)
- **Nothing is extracted.** Entry names and relative paths are cleaned (`path.Clean`, no leading `/`) and used
  only as labels: in the summary (quoted with `%q`), in logs (JSON attributes whose value is
  `strconv.Quote(name)`, because the JSON handler alone writes C1 and format characters raw; 0072) and in
  `import_files.name`
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
  header ends the archive: entries before it are handled, the archive is listed as failed. A header that
  `Next` returns together with `tar.ErrInsecurePath` (`GODEBUG=tarinsecurepath=0`) is handled as a normal
  header, since its name is only a label.
- **Limits**: at most `MaxFiles` = 20,000 entries per run — every directory entry of any kind and every tar
  header except PAX global headers counts. The limit is **enforced while scanning**: directories are read in
  chunks and their entries counted before they are sorted, tar headers are counted before their content is
  touched, and the entry that brings the count to `MaxFiles`+1 ends pass 1 at once with `ErrTooManyFiles` —
  it is not read, detected or hashed, no further header or directory chunk is read, and nothing has been
  written. The scan's memory is thus bounded by `MaxFiles` entries of at most `MaxPathBytes`, whatever the
  input holds. A head of `logparse.SniffBytes` = 4096 bytes; lines of at most 16 KiB
  (`logparse.LineReader`, 0070); batches of at most 2,000 records (`DefaultBatchRecords`) and the records from
  at most 4 MiB of input (`DefaultBatchBytes`), whichever comes first. No limit on the size of a file or of
  decompressed data: every read checks the context, so SIGINT or SIGTERM to the import process (Ctrl-C under
  `docker exec -it`, 0072) stops it cleanly, and it resumes later (0069). `docker stop` signals only the
  container's PID 1 (the service); the import is killed when the container stops, and the per-batch
  transactions (0065) with the import's compare-and-set (0069) keep the database consistent and resumable
  after such a kill — no guarantee here depends on a clean stop.
- **Permissions**: the import runs as the container's user (65532) and reads only what that user may read;
  an unreadable file is listed as failed with the OS error. The README tells the operator to grant read
  access to user 65532 only — `chown -R 65532:65532` plus `chmod -R u+rX`, or `setfacl -R -m u:65532:rX`, on
  `import/<name>` — and never to make the copy world-readable: a copied `/var/log` holds `0640 root:adm`
  files such as `auth.log` and `mail.log`. The import never asks for more rights.
- **Modification times** are passed to `Detect` in UTC as found; one outside the range storable as Unix
  nanoseconds (years 1678–2262, e.g. a forged tar `mtime`) is stored as unknown, so `Parse` receives the zero
  time (0069).
- Every file that is not imported appears in the summary with its outcome and reason; nothing is skipped
  silently.

## Consequences

- No input can make the import write a file, read outside the named root through a link — also not by
  swapping a path component while the import runs —, or block on a FIFO. A swap inside the root can make
  another input file be read under the listed name; its content is input either way.
- Memory stays flat for any file size and any number of entries: the scan's per-file list (bounded by
  `MaxFiles`, enforced while scanning, and `MaxPathBytes`), one head, one line, one batch.
- A gzip bomb or a huge sparse entry is not refused; it costs time and stops only by the context. Once
  parsers exist, a bomb of **valid lines** is also stored and fills `storage.directory` until the operator
  stops it; the README tells the operator to watch the progress lines and press Ctrl-C. A size limit can be
  added if it ever matters.
- Rotations compressed with xz, zstd or bzip2, zip files and nested archives are listed, not imported; adding
  a decompressor (bzip2 is in the standard library, the others are dependencies) is a new decision.
- Inputs with more than 20,000 entries (subdirectories and tar directory entries included) must be split;
  the error says so before anything is written.
- A symbolic link inside a saved `/var/log` (rare; e.g. a link to a log elsewhere) is listed; the operator
  copies the target instead.
