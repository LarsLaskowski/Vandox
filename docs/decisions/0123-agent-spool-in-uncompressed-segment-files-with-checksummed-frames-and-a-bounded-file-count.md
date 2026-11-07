# 0123: Agent spool in uncompressed segment files with checksummed frames and a bounded file count

- **Status:** Accepted
- **Date:** 2026-10-23
- **Area:** Agent spool
- **Source:** Issue #38
- **Supersedes:** —

## Context

The agent must keep every record on the server's disk until the backend acknowledges it, at least 7 days at the reference stream,
without filling the disk or growing its memory (record 0025), and record as a gap whatever it drops (0028). A crash or power loss can
tear the last write. The server has 2 GB of RAM, and a crash loop with reboots must not make memory or start-up grow without bound.

## Options considered

1. **An embedded store (bbolt, SQLite)** — transactions for free; a new dependency, bbolt maps the whole file into memory (page cache
   counted against the agent), SQLite needs cgo or a large pure-Go port.
2. **Compressed segments (gzip or zstd per segment)** — about a fifth of the disk; a torn compressed tail loses the rest of its block,
   and compression costs CPU on a 2 GB server; at the reference stream 7 days fit uncompressed in the default cap.
3. **One file per hand-over** — simple atomicity; hundreds of thousands of files a week, an fsync per file.
4. **Append-only segment files of length- and CRC-32C-framed wire lines, a trailer when sealed, one small meta file** (chosen) —
   standard library only; a torn tail is cut at the first bad frame; start-up reads the headers, trailers and first records of sealed
   segments and scans only the newest segment and those without a trailer.
5. **A fixed file size of 64 MiB without a limit on the count** — thousands of files at large caps, and a crash loop with reboots adds
   a file per start: memory and start-up grow without bound.
6. **A fixed number of files whose size grows with the cap** — the start-up scan of the newest file grows with the cap.
7. **A file size derived from `spool.max_size` within fixed bounds, a capped `spool.max_size`, appending after a restart in the same
   boot, and a hard limit on the file count that drops the oldest like the size bound** (chosen) — memory and start-up stay bounded at
   every configuration.

## Decision

The spool stores records in option 4's segment files, sized and counted as option 7 says; its size bound and free-space reserve drop
the oldest segments with `spool_dropped` gaps, and values read back from its files are held to consistency rules, a break being damage,
never a number. The format, values and rules are in [Agent spool](../areas/agent-spool.md).

## Consequences

- Disk use is uncompressed (about 1.5 GB for 7 days of the reference stream, measured at 209 MB a day); a server with more volume
  needs a larger `spool.max_size` or a later compressed format (the format carries a version).
- `spool.max_size` has an upper bound, so the file count and the start-up scan stay bounded.
- Without a sender the running agent fills its spool to the cap and then drops the oldest with gaps; it was not released before the
  sender (#39).
