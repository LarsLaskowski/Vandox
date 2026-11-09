# Agent spool

## Scope

The on-disk spool of `vandox-agent`: what it keeps of the records the scheduler hands over, the sequence numbers it issues, its files
and their format, how it reaches them safely, its size bound and what it drops, how it handles damage, durability and the stop, and
its memory and capacity. The sender reads from it and acknowledges into it ([Agent sending](agent-sending.md)); the backend relies on
its sequence numbers and gaps to tell what is missing.

## Guarantees

- The agent keeps collecting while the backend is unreachable and keeps **at least 7 days** of records in the spool (*Capacity*); the
  sender then sends current data first and backfills the spool oldest first ([Agent sending](agent-sending.md)).
- Every record carries a sequence number from one counter per agent that increases strictly, survives restarts and reboots and is
  never reused (*Sequence numbers*). A batch has no identity of its own ([Wire format](wire-format.md), *Batch identity (0045)*).
- The boot ID and the clock offset are kept with each record, and one read never mixes two capture contexts, so a batch holds only
  records of one boot ID and offset.
- The spool is a file-write area of the agent (security area 9): it has a size bound and defined behavior when full (oldest data
  dropped, a gap recorded). Records it refuses to take from the scheduler are its loss to record as a gap, too.
- The spool counts and deletes only its own files: the agent's state file in the same directory ([Agent](agent.md), *Agent state*)
  is never part of its size or of what it drops.

## What is kept

The scheduler hands every run's checked records to the spool ([Agent](agent.md), *Scheduling*); a hand-over is accepted whole, when it
returns without an error, or refused whole. Each kept record gets its sequence number, in hand-over order, and is kept as its wire line
with the agent ID, the boot ID the agent read from `/proc/sys/kernel/random/boot_id` at its start (which any user can read) and the
clock offset estimate. No part of the agent measures an offset yet, so every record is kept without one (the header's "unknown"); the
format holds the field. A record reads back unchanged, also one of the largest size the wire format allows. The agent token is never
written to the spool.

## Files

The spool lives in `spool.directory` beside `agent-state.json`. Its own names are the data files
`segment-<the first record's number in 20 decimal digits>.spool`, the meta file `spool-meta.json` and its temporary file
`spool-meta.json.tmp`, all created with mode 0600. Every other entry (`agent-state.json`, `agent-state.json.tmp`, any other file or
directory, up to 100,000 of them within the memory bound) is foreign: never opened, read, counted, overwritten or deleted.

- A **data file** (format version 1, every integer little-endian) starts with a header of at most 128 bytes: `VXSPOOL1`, the version,
  the agent ID (1 to 64 bytes), the boot ID, the clock offset (flag and value) and a CRC-32C. Each record is one frame: the payload's
  length (at most the wire format's line limit plus 8 bytes, checked before any allocation), the payload's CRC-32C (Castagnoli) and the
  payload, the sequence number and the wire line. A sealed file ends with a summary frame (marker `0xFFFFFFFF`, the last number, the
  record count, the earliest and the latest capture time, for a gap record its `from` and `to`, and a CRC-32C).
- A data file holds the records of one capture context (agent ID, boot ID, clock offset). It is **sealed** — summary written, file
  synced — before the next one is created: when the next hand-over would take it beyond `spool.max_size` / 128, bounded to 1 to 64 MiB
  (16 MiB at the default), or when its context ends; a file grows beyond that size only by a single hand-over that is larger. A start
  in the same boot appends to the newest unsealed file of the context, so restarts alone add no file; a start in a new boot seals it.
- The **meta file** is one JSON object,
  `{"version":1,"agent_id":…,"boot_id":…,"clean":…,"next":…,"acked":[{"first":…,"last":…}]}`, every key required, at most 16 KiB: the
  agent ID and the boot ID of the last start, whether the spool was closed cleanly, the next sequence number at the time of the write
  and at most 65 acknowledged ranges (64 above the oldest unacknowledged record and one that reaches down to it). It is replaced
  through its temporary file and a rename: at the start (synced, `clean` false), at every acknowledgement (not synced: a lost
  acknowledgement only makes records be sent again) and at the close (synced, `clean` true).

## File access (security area 9)

- `spool.directory` must be absolute and clean and its last element must not be a symbolic link (links above it are followed as
  configured). The spool opens it with `O_DIRECTORY|O_NOFOLLOW`, keeps the descriptor and never creates the directory (the installation
  does, [Agent installation](agent-installation.md)). Every later open, create, remove and rename is relative to that descriptor on a
  plain name (`openat`, `unlinkat`, `renameat`), and the free-space check reads the file system of the same descriptor (`fstatfs`),
  never by path and never through a facility that follows a symbolic link whose target stays inside the directory.
- A file is opened with `O_NOFOLLOW` and `O_NONBLOCK` (a link fails, a FIFO does not block) and trusted only when `fstat` on that
  descriptor shows a regular file of the agent's effective user without any group or other permission bit and with exactly one link; it
  is created exclusively. Because the check runs on the opened descriptor, an entry that is no regular file is opened before it is
  refused and closed at once, never read or written: a device node placed under a spool name (which needs `CAP_MKNOD`) reaches its
  driver's open.
- A name is removed or renamed only after the same check, a data file only when its device and inode are those of the file the spool
  holds, so a link or an entry swapped in between never makes the spool write or delete anything but its own files. An entry the check
  refuses is never followed, written, renamed or deleted: in place of a data file it is damage (*Damaged data*); in place of the meta
  file or its temporary file the meta file cannot be written, so the start fails and at run time an acknowledgement, a sync or the
  close returns `spool: meta: refused entry`.

## Start

The agent opens the state file first, then reads the boot ID and opens the spool. A spool that cannot be opened makes the agent log the
error line `agent cannot start` with one of these errors and exit with 1 before it notifies systemd (`READY=1`), so `systemctl status`
shows the failure; a failed start is retried only by the unit's `Restart=` ([Agent installation](agent-installation.md)). No error
names a path.

| Error | Cause |
| ----- | ----- |
| `spool: boot id: not readable`, `spool: boot id: not a lower-case UUID` | `/proc/sys/kernel/random/boot_id` missing or unreadable, or not a lower-case UUID |
| `spool: directory: not an absolute, clean path` | the path is relative or not clean (the configuration already refuses such a path) |
| `spool: directory: symbolic link` | the last element of `spool.directory` is a symbolic link |
| `spool: directory: not a directory` | `spool.directory` is a file or another entry that is not a directory |
| `spool: directory: <system error text>` | the directory cannot be examined or opened, such as `no such file or directory` |
| `spool: meta: refused entry` | a refused entry in place of `spool-meta.json` or `spool-meta.json.tmp` |
| `spool: meta: <system error text>` | the meta file cannot be read or written, such as `permission denied` (a directory the agent cannot write) or `read-only file system` |
| `spool: read: <system error text>`, `spool: write: <system error text>` | listing the directory, reading a data file, or cutting and sealing one at the start failed |

The directory is listed in chunks of 256 names and at most the 1,026 data files with the highest numbers are kept; a sealed file is
checked by its header, its summary and its first record, while the newest file and every file without a valid summary are read frame by
frame. A file without a summary is cut at its last good frame and sealed, unless it is the newest file of the current context, below
the sealing size and ends cleanly or in a torn frame: that one is appended to. A start that finds no meta file and no data file logs
the info `spool created`; a directory that holds only foreign entries, `agent-state.json` among them, counts as empty, so a first start
logs no warning from the spool or the state file.

## Sequence numbers

Numbers increase strictly and are never issued twice under one agent ID. The next number at the start is the largest of the meta
file's `next`, the newest data file's name number and the last good number plus 1. On a fresh spool (no meta file, a damaged one, or
one of another agent ID), after an unclean stop in an earlier boot (the meta file is not `clean` and names another boot, so a power
loss may have rolled the counter back) and when the newest data file ends in damage whose numbers cannot be known, the **clock bound**
is taken as well: the wall clock's microseconds since the Unix epoch plus 1,000,000. The agent issues far fewer than 1,000,000 numbers a
second, so a number is never more than 1,000,000 ahead of the clock's microseconds when it is issued, and a later fresh start lies above
it. After a clean stop and after a `kill -9` in the same boot the numbers go on directly after the last one issued; an agent's first
number is large, and a fresh start or a crash leaves a jump ([Wire format](wire-format.md), *Batch identity (0045)*). The gaps the spool
writes take numbers too, before the records of the hand-over that carries them. A hand-over whose numbers would reach 2^63 - 1, the
largest the backend stores, is refused with `spool: sequence numbers exhausted` as a write failure (*Write failures*). Stated residual:
a clock set back by more than the time between a lost spool and a fresh one can make new numbers collide with stored ones.

## Reading and acknowledging

- A **read** takes a number (`after`; 0 reads from the oldest), a record limit and a byte limit and returns the kept, unacknowledged
  records with numbers above it in sequence order, at most that many records and bytes of their wire lines (the line feed not counted),
  together with their agent ID, boot ID and clock offset: one result never mixes two capture contexts and never holds a record whose
  write has not completed. A result may end at a file boundary or go on into the next file of the same context. It holds at least one
  record when the first fits; it is empty when the first record alone exceeds the byte limit (and when a limit is 0 or less), so a
  caller passes a byte limit of at least the wire format's line limit (1 MiB) to always make progress. A record is handed out by every
  read that reaches it until it is acknowledged.
- An **acknowledgement** of a range (`first`, `last`) acknowledges every issued, kept, unacknowledged number of the range, whether a
  read handed it out or not (the spool does not track reads); numbers never issued (below the oldest, above the newest, inside a jump),
  already acknowledged or dropped change nothing, and so does a range with `first` 0 or above `last`. The spool keeps at most 64
  disjoint acknowledged ranges above its oldest unacknowledged record: an acknowledgement that would make a 65th is refused with
  `spool: more than 64 acknowledged ranges` and changes nothing; its records stay readable and are acknowledged again later (a resend
  is idempotent at the backend).
- Acknowledged records are never handed out again, also after a restart. A sealed file whose numbers are all acknowledged is deleted;
  the file being written to is never deleted, so once every record is acknowledged the spool holds at most that file and the meta file,
  at most one sixteenth of `spool.max_size`.

## Size bound, free space and file limit

`agent_spool_bytes` is the size of the spool's own files on disk (the data files, headers and summaries included, and the meta file).
Before every write the spool checks its room:

- the data files plus 32 KiB for the meta file and its temporary file plus the write (its frames, a header and a summary when a file is
  created or sealed, and 1 KiB for each gap record it owes) must stay within `spool.max_size`;
- unless `spool.min_free` is 0, the space available to unprivileged users on the file system (`fstatfs` on the directory's descriptor,
  `f_bavail` times `f_bsize`, so it stays the file system the spool writes into even if the path is renamed or replaced) must be at
  least the write, and what remains after it at least `spool.min_free`, compared without overflow;
- a new data file may not make more than 1,024.

While there is no room the spool drops its oldest data file whole and checks again; the file being written to is never dropped, and
when only it is left the hand-over is refused (`spool: no room for the records`, a write failure). A hand-over that alone, with the gaps
it owes and the 32 KiB, exceeds `spool.max_size` is refused before anything is dropped. A full spool therefore never refuses records:
it drops its oldest. The sizes are added saturating at 2^63 - 1 and a sum that reaches it counts as no room (`size_bound`), so an
oversized file, such as a planted sparse file, is dropped like any other oldest file; `agent_spool_bytes` saturates the same way. The
oldest-first order stays: a planted oversized file with a higher number is dropped only after every older file, so the older real
records become `spool_dropped` gaps.

- Every drop owes one `gap` with cause `spool_dropped` per run of unacknowledged numbers of the dropped file (acknowledged numbers split
  runs; runs that join across the files one hand-over drops give one gap): origin and source `agent`, `first_seq` and `last_seq` the
  run's lowest and highest number, `from` and `to` the earliest and latest capture time of the run's records, the spans of dropped gap
  records included (from the summary when the run is the whole file, else read from the file), `to` at least one second after `from`,
  and the spool's clock at the write as its capture time. The gaps are written in the same write as the hand-over, before its records.
  Stated residual: a gap's sequence range is exact, while its time span is the union of the dropped times and may include moments of
  which other records were kept.
- The warning `spool data dropped` (`reason`: `size_bound`, `disk_space` or `file_limit`, the reason of the first drop; `records`: the
  records that hand-over dropped) is logged at the first drop of an episode, the info `spool data no longer dropped` (`records`: the
  episode's total) at the first hand-over that fits without a drop.

## Write failures

A write that fails (no space left, an I/O error, the file system remounted read-only) is cut back to the last good frame and the
hand-over is refused at once with `spool: write: <system error text>`, so the scheduler logs `sink failed` and the run is not kept; the
refusal never blocks the watchdog. Unlike at the start the agent keeps running, because the failure may pass. The spool logs the
warning `spool write failed` (`err`) once per episode; a hand-over refused for want of room or numbers belongs to the same episode. The
first hand-over that is written again first writes one `gap` with cause `unknown`, source `agent` and no sequence range, from the
earliest to the latest capture time of every record refused in the episode (at least one second), and logs the info `spool writes
again`. A gap still owed when the agent stops is lost with it; the backend's time-gap detection
([Detection, alerts and reports](detection-and-reports.md), *Live data and backfill*) covers that span.

## Damaged data (security area 10)

Values read back from the spool's files are held to consistency rules before they are used: the header's, each frame's and the
summary's checksum; the format version; a frame length within the bound above; a header boot ID that is a lower-case UUID; a name
number that is the number of the file's first record, above the previous file's last number and below 2^63 - 1; record numbers that
increase strictly within and across files; a frame number equal to the `seq` of its line; a line that passes the wire decoder's line
rules; a summary that agrees with the last record and the count; times within 1677-09-21 to 2262-04-11. A break is damage, never a
value, and never a crash, an unbounded allocation or a hang:

- The damaged record and every later record of its file are never handed out; the records before it and all other files stay
  readable. The warning `spool data damaged` (`file`: the file's name as the spool listed it; `err`: `checksum mismatch`, `file cut
  short`, `not the spool format`, `unknown format version`, `inconsistent value`, `refused entry` or `too many files`) is logged once
  per file and start.
- A `spool_dropped` gap is written per run of unacknowledged dropped numbers where they can be known: from the last good number plus 1
  (the name number when no record is good) to the summary's last number when the summary is intact and in range, else to the next
  file's first number minus 1; `from` the latest capture time of the good records before the damage (else the previous file's latest),
  `to` the summary's latest (else the next file's earliest), a missing side one second from the other, both missing the spool's clock,
  `to` at least one second after `from`. A gap is never built from a damaged value. The newest file without an intact summary gets no
  gap for its tail, and a file whose name breaks a rule gets none and does not raise the next number: the backend's sequence detection
  finds those numbers.
- At the start a file with good records is cut at the last good frame and sealed; a file damaged as a whole (a header that is missing
  or broken, such as a file a crash left right after its creation, or no good record) is deleted once its gap is written. A torn tail —
  an incomplete last frame of the newest file, from a stop during a write — is cut with the warning (`file cut short`) and no gap: the
  hand-over it belonged to was never accepted. Refused entries and the data files beyond the 1,026 are never touched; the latter are
  logged once per start (`file`: the lowest such name, `err` `too many files`).
- Damage found by a read (a sender read, or the age read of the self-metrics) ends that file's records in memory; its gap is written by
  that read when it fits without a drop, else by the next hand-over. In the file being written to, the run ends at the last number and
  the latest time the spool holds in memory, and the next hand-over starts a new file. The file is not cut on disk and is deleted once
  its good records are acknowledged or dropped. Stated residual: a restart before that finds the damage again, logs it again and writes
  a second gap for the same numbers.
- A meta file that is not JSON (`not the spool format`), of another version (`unknown format version`), with a missing, unknown or
  repeated key, more than 65 ranges, a range that is not sorted, overlaps another, starts at 0, has its first number above its last or
  reaches `next`, or a `next` at or above 2^63 - 1 (`inconsistent value`) is damaged (`file` `spool-meta.json`): its acknowledgements are
  discarded, so those records are handed out again, and the next number is taken as on a fresh spool.

**Another agent ID.** When `agent_id` changed, the start deletes every data file of another agent ID, writes no gap for it (a gap sent
under the new ID would describe another agent's data) and logs the warning `spool records of another agent dropped` (`records`: their
count); the new ID's numbers start as on a fresh spool.

## Durability and the stop

- A hand-over is written with one `write` and no `fsync`, so an accepted record survives a `kill -9` (the page cache keeps it). A
  hand-over is written even when the caller's context is already cancelled, so the records of a run that ends while the agent stops are
  kept; the context only accompanies the spool's log lines.
- Once a second, when something was written, the spool syncs the file being written to, the meta file and the directory; at most one
  sync is in flight, outside the spool's lock, so a slow sync never holds up a hand-over, and a caller that finds one running waits for
  its result. An OS crash or power loss therefore loses at most the records of about the last second, never hands out a damaged record
  and never issues a number twice; the backend sees the loss as a sequence gap
  ([Detection, alerts and reports](detection-and-reports.md), *Live data and backfill*), and the `kernel` and `logs` collectors read
  their entries again because every state save first makes the spool durable ([Agent](agent.md), *Agent state*). The boot and the open
  backup run are written to the state only after the run's records are kept, so that sync covers them too.
- At the stop, after the scheduler has stopped and the state file's last save, the spool is closed: synced, the meta file written with
  `clean` true and synced. A close that fails or does not end within 5 seconds is given up with the warning `spool not closed`, and the
  agent exits as usual.
- A disk whose writes hang is not covered: a hand-over that blocks holds up its collector, the watchdog is withheld and systemd restarts
  the agent.

## Memory

The spool's live memory stays below 4 MiB, apart from the records of a read or hand-over in progress, at every `spool.max_size` up to
64 GiB, with 1,024 data files, 64 acknowledged ranges and 100,000 foreign entries: under 256 bytes per data file (numbers, size,
context, summary times), a sparse index (a number every file size / 64 bytes) for at most the two files the two read positions are in,
and one frame buffer; it does not grow with the number of records. The two read positions are one for each sender stream; the age read
of the self-metrics (once a minute) takes a position as well, so it can replace one, and the stream whose position was replaced finds
its place again through the sparse index, which costs reading time, never correctness. Opening a spool filled to the default cap takes
26.2 ms and one of 1,024 files 15.9 ms on the development host, against a bound of 5 seconds; on the production server (1 core, AMD EPYC) 55.1 ms and 26.1 ms ([BENCHMARKS](../BENCHMARKS.md), *Spool
open*).

## Capacity

The default `spool.max_size` holds 7 days of the **reference stream**, an estimate of the server that the tests generate from a fixed
seed, every record with varying content: per day 2,880 `system` samples of 110 metrics; 1,440 `filesystem` samples of 7 metrics,
`clock` samples of 3 and `self` samples of 5; 1,440 process snapshots of 50 processes with command lines of 60 to 200 bytes and 100
programs; 1,440 connection snapshots of 30 listeners, 40 owning processes and 25 remotes; 2,880 MariaDB status records of 60 status
counters, 15 variables and 8 threads; 400 service states; 200,000 log lines of 40 to 400 bytes shaped like the lines of
`testdata/logs/` and `testdata/journal/`. That is 544,560 records and **209.0 MB per day** as uncompressed JSON lines, so 7 days take
about 1.49 GB with the spool's framing, about 70 % of the 2 GiB default.

- The full-scale 7-day run (opt-in, without the race detector; 109.6 s on the development host, 2026-10-23) kept all 3,811,920 records,
  held 1,576,818,758 of 2,147,483,648 bytes after the 7 days (73 %, the final 16-digit sequence numbers, headers and summaries
  included), dropped nothing, wrote no `spool_dropped` gap and read every record back once in order. Every test run checks the same at
  one eightieth of every daily count.
- On the production server, without sending (nothing acknowledged), the spool grew by about 157 MB a day, derived from the spacing of
  the sealed files (one of about 16.76 MB every 2 h 33 min, 2026-10-28 to 2026-10-30; the total gives 158 MB), about 70 % of the
  reference stream's 225 MB, so the default 2 GiB holds about 13.7 days ([BENCHMARKS](../BENCHMARKS.md), *Agent footprint*).
- Under sustained memory pressure the agent records much more, so less than 7 days may fit and the cap drops the oldest with gaps
  ([Agent](agent.md), *Memory pressure mode*).

## Logging

Through the agent's logger with `component=spool`, one line per change and never a path, a record's content or a system error text
other than the `errno` text of `spool write failed`: info `spool created`; warning `spool data dropped` and info `spool data no longer
dropped`; warning `spool write failed` and info `spool writes again`; warning `spool data damaged`; warning `spool records of another
agent dropped`; warning `spool not closed`.

## Related decisions

- [0045](../decisions/0045-batch-identified-by-agent-id-and-record-sequence-numbers.md) — why a spool of at least 7 days and
  per-record sequence numbers.
- [0123](../decisions/0123-agent-spool-in-uncompressed-segment-files-with-checksummed-frames-and-a-bounded-file-count.md) — why uncompressed
  segment files of checksummed frames, bounded file size and count.
- [0171](../decisions/0171-a-fresh-spool-starts-its-sequence-numbers-above-a-clock-bound.md) — why a fresh spool's numbers start from the
  clock bound.
- [0172](../decisions/0172-the-spool-keeps-a-bounded-number-of-acknowledged-ranges.md) — why at most 64 acknowledged ranges.
- [0173](../decisions/0173-the-spool-opens-its-files-relative-to-its-directorys-descriptor.md) — why files are reached only relative to the
  directory's descriptor.
- [0174](../decisions/0174-spool-file-sizes-add-up-saturating-and-a-saturated-sum-is-no-room.md) — why file sizes add up saturating.
- [0175](../decisions/0175-the-state-file-syncs-the-spool-before-every-save.md) — why the state file waits for the spool's sync.
- [0028](../decisions/0028-data-gaps-are-always-recorded.md) — why every drop and refusal is a recorded gap.

## Not here

- The scheduler, the state file and the stop order: [Agent](agent.md).
- Sending and acknowledging batches: [Agent sending](agent-sending.md).
- `spool.directory`, `spool.max_size` and `spool.min_free` with their ranges and error texts: [Configuration options](configuration-options.md).
- The directory's creation and owner: [Agent installation](agent-installation.md); the agent's file rights: [Agent rights](agent-rights.md).
- The record and line format: [Wire format](wire-format.md), [Wire format records](wire-format-records.md); how the backend detects
  sequence and time gaps: [Detection, alerts and reports](detection-and-reports.md).

## Implementation

`internal/spool` (`Open`, `Put`, `Read`, `Ack`, `Sync`, `Close`; data and meta files, sequence numbers, reading, acknowledgements,
drops and damage; it uses `wire.MarshalRecord` and `wire.UnmarshalRecord` and the `syscall` calls `Openat`, `Unlinkat`, `Renameat` and
`Fstatfs`, never Go's `os.Root`, which follows a symbolic link whose target stays inside the directory). Its tests generate the
reference stream and run the hard-kill and 7-day tests: `TestReferenceStream_FullScaleDay`, `TestSpool_SevenDayOutageFullScale` (with
`VANDOX_LONG_TESTS=1`), `TestSpool_SevenDayOutage`, `TestSpool_MemoryBound`.
