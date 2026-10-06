# 0079: Log parsing and import in the backend: statx/openat2 file access, same limits and guarantees

- **Status:** Accepted
- **Date:** 2026-10-06
- **Source:** Product Manager request: move the backend to .NET 10 with Blazor, keep the agent in Go
- **Supersedes:** [0070](0070-log-parser-interface-and-explicit-registry-in-internal-logparse.md), [0071](0071-log-import-reads-input-without-following-links-or-extracting.md)

## Context

The log parser framework and the import ([0014](0014-log-import-is-a-core-component.md),
[0069](0069-log-import-idempotent-per-file-content-hash-with-resumable-batches.md)) are backend code and move
to C#. Go's `os.Root` gave the import its link- and escape-safe file access; .NET has no equivalent.

## Options considered

1. **Plain `File.Open` plus path checks** — races between check and open; links followed.
2. **P/Invoke to the kernel** — `openat2` with `RESOLVE_BENEATH | RESOLVE_NO_SYMLINKS` where available, `statx`
   to classify entries without following links, and `O_NOFOLLOW`/`O_NONBLOCK` component-wise opens as the
   fallback for kernels without `openat2` (NAS kernels such as 4.4).

## Decision

Option 2. `Vandox.Core.IO` (`SecureRoot`, `FileProbe`, `OpenHow`) opens everything below the import root
through these calls; links and special files (FIFO, device, socket) are listed and never followed or opened
as data; names and paths are labels only. The guarantees of 0071 hold unchanged: nothing is extracted or
written, at most 20,000 entries per run (`ImportLimits.MaxFiles`), paths bounded to 1,024 bytes, one gzip
layer, no nested archives. `ILogParser` and `ParserRegistry` (`Vandox.Core.LogParsing`) pick the parser with
the highest confidence, the earlier registered one on a tie; `LogLineReader` cuts lines at 16 KiB. The
import runs in two passes with batches that resume by count, keyed by the SHA-256 of the decompressed content (0069).

## Consequences

- Linux only for the import; other platforms are not supported by the backend image anyway.
- The NAS kernel fallback is weaker than `openat2` (no kernel-enforced containment) and is stated openly in
  `.squad/project.md`; the tests cover both paths.
