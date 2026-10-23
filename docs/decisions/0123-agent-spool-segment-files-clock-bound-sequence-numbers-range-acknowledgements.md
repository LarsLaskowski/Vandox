# 0123: Agent spool in uncompressed segment files with checksummed frames, clock-bound sequence numbers and range acknowledgements

- **Status:** Proposed
- **Date:** 2026-10-23
- **Area:** Agent
- **Source:** Issue #38
- **Supersedes:** 0045 (amends it: a lost spool no longer needs a new agent ID, because a fresh spool's sequence numbers start above the clock bound)

## Context

The agent must keep every record on the server's disk until the backend acknowledges it, at least 7 days at the reference stream, without
filling the disk or growing its memory (record 0025), and record as a gap whatever it drops (0028). Records need a strictly increasing
sequence number that is never reused (0045), and a batch holds records of one boot ID and clock offset estimate (0046). The agent's state
file commits log and kernel positions that must never be durable before the records they cover. The sender (#39) needs current data first
and the backlog in order. The rules are in the [Agent](../areas/agent.md) area (*Spool and sending*).

## Options considered

Storage:

1. **An embedded store (bbolt, SQLite)** — transactions for free; a new dependency, bbolt maps the whole file into memory (page cache
   counted against the agent), SQLite needs cgo or a large pure-Go port.
2. **Compressed segments (gzip or zstd per segment)** — about a fifth of the disk; a torn compressed tail loses the rest of its
   block, and compression costs CPU on a 2 GB server; at the reference stream 7 days fit uncompressed in the 2 GiB default.
3. **One file per hand-over** — simple atomicity; hundreds of thousands of files a week, an fsync per file.
4. **Append-only segment files of length- and CRC-32C-framed wire lines, a trailer when sealed, one small meta file** (chosen) — standard
   library only; a torn tail is cut at the first bad frame; start-up reads only trailers and the newest segment.

Sequence numbers after a lost or rolled-back counter:

1. **Refuse to start** — monitoring stops until someone wipes the agent ID.
2. **A counter epoch in the batch header** — a wire minor version and backend work in #40/#41.
3. **The clock bound** (chosen) — microseconds since the epoch plus 1,000,000, used on a fresh spool and after an unclean stop in an
   earlier boot; safe unless the clock was set back, at the price of a large first number and a jump after a crash.

Acknowledgements:

1. **Only a contiguous prefix** — #39 could not acknowledge current data before the backlog.
2. **A bitmap per segment** — memory and persisted state grow with the records.
3. **At most 64 disjoint ranges above the oldest unacknowledged record** (chosen) — enough for a current and a backfill stream;
   one more is refused and the records are sent again (idempotent).

File count:

1. **A fixed file size of 64 MiB without a limit** — thousands of files at large caps, and a crash loop with reboots adds a file
   per start: memory and start-up grow without bound.
2. **A fixed number of files whose size grows with the cap** — a start-up scan of the newest file grows with the cap.
3. **A file size of `spool.max_size` / 128 within 1 to 64 MiB, a cap of at most 64 GiB, appending after a restart in the same
   boot, and a hard limit of 1,024 files that drops the oldest like the size bound** (chosen) — memory and start-up stay bounded at
   every configuration.

Order of state and records:

1. **An fsync per hand-over** — the I/O cost every 2 seconds per collector.
2. **Positions stored inside the spool** (0121's exact delivery) — a larger change to three collectors.
3. **The state file syncs the spool before every save** (chosen) — a periodic sync every second, and the barrier makes the records
   durable before any position that covers them. It does not cover the boot and backup state the `kernel` and `backups` collectors
   write inside their run, before the hand-over; moving those behind the scheduler's `Kept` was left to a follow-up issue.

## Decision

The spool is option 4 for storage, the clock bound for a lost counter, at most 64 acknowledged ranges, option 3 for the file count,
and a spool sync as a barrier before every state save. Values read back from its files are held to consistency rules, and a break is
damage, never a number. Its size bound and free-space reserve drop the oldest segments with `spool_dropped` gaps.

## Consequences

- Disk use is uncompressed (about 1.2 GB for 7 days of the reference stream); a server with more volume needs a larger
  `spool.max_size` or a later compressed format (the format carries a version).
- An agent's first sequence number is large, and an unclean stop in an earlier boot leaves a jump that #41 reports as
  `sequence_missing`; numbers below an agent's first are never reported.
- A clock set back before a reinstall can make new numbers collide with stored ones (stated residual).
- Without a sender the running agent fills its spool to the cap and then drops the oldest with gaps; it is not released before #39.
- A power loss right after a state save that fell between a run's boot or backup change and its hand-over can lose that `boot` or
  `backup_run` record (stated residual until the follow-up).
- `spool.max_size` is at most 64 GiB.
