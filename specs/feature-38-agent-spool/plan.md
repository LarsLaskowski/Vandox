# Plan: Persistent on-disk spool (at least 7 days)

Source: Issue #38 | [spec.md](spec.md)
Status: Revised (2)
Tier: security — the spool creates guards of security area 9 (no link followed, owner and mode checks on its files, nothing written or deleted outside its own files, the size bound, the file limit and the free-space reserve) and reads its own files back under consistency rules.
Security areas touched: 8 (the agent token must never reach the spool), 9 (file writes and paths: the spool's files in `spool.directory`, the meta file, deletion of its own files), 10 (parsing: spool files and the meta file read back, damaged or hostile, through a new single-line entry point of the wire decoder), 12 (new fixed log texts of the spool)

## Problem / root cause

See [spec.md](spec.md), *Problem*. The agent's sink is `scheduler.DiscardSink` (`internal/agent/agent.go:304`), so nothing is kept.

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | Today every record is dropped; the sink assigns sequence numbers | `internal/agent/agent.go:304-307`, `internal/scheduler/sink.go:9-23` | confirmed |
| 2 | `Put` is called concurrently, one runner goroutine per collector, with the agent's context; a sink error makes the run "not kept" | `internal/scheduler/runner.go:349-364`, `scheduler.go:73-80`, `docs/areas/agent.md:88-91,127-129` | confirmed |
| 3 | The `logs` positions are written to the state file in `Kept`, after `Put` accepted the run (`internal/logship/logship.go:261-300`), and the kernel journal position is committed in the next run's prepare (`internal/kernevents/kernevents.go:141-147`); but the boot change (`internal/kernevents/boot.go:62`) and the backup run (`internal/backups/backups.go:198,205,217`) are written by `store.Update` inside `Collect`, before `Put`; the `state` job saves on its own goroutine (`internal/agentstate/agentstate.go:699-720`) | the files and lines named (read by the Devil's Advocate, checked by the Lead) | confirmed |
| 4 | The state file treats only an empty directory as a first start; an absent file beside other entries warns | `internal/agentstate/agentstate.go:199-242` | confirmed |
| 5 | The boot ID is read by `procstats.ReadBootID` from `sys/kernel/random/boot_id` of a `/proc` `fs.FS`; the agent tests' kernel fixture holds that file | `internal/procstats/boottime.go:22,122-140`; `internal/agent/agent_test.go:326,337` | confirmed |
| 6 | No clock offset estimate exists in the agent | `grep -rn clock_offset internal` finds only the wire header | confirmed |
| 7 | The strict decoder answers a list with `invalid value, want a string` and a null with `has no value` before any option rule | `internal/config/decode.go:21-22,235-258` | confirmed |
| 8 | The example `deploy/agent/agent.yaml` must set every key of `AgentKeys()`; `TestAgentKeys` pins the key order | `internal/config/agent_test.go:94-114,195-209` | confirmed |
| 9 | The wire package exports no single-record line codec and no line-limit constant (`marshalLine`, `decodeRecord` unexported; the limit is `wire.DefaultLimits().MaxLineBytes`) | `internal/wire/encode.go:23-37`, `decode.go:500-527`, `wire.go:166-176` | confirmed |
| 10 | `selfstats` has a spool size provider and no age metric | `internal/selfstats/selfstats.go:28-56,103-113` | confirmed |
| 11 | Go 1.27 with `testing/synctest`, already used by the scheduler tests | `go.mod:3`, `internal/scheduler/runner_test.go:13` | confirmed |
| 12 | Records 0110, 0120, 0121 are unreleased; 0028, 0045, 0046 are released (v0.1.0) | `git ls-remote --tags origin` (v0.1.0 only), `git tag --contains` of each record's adding commit | confirmed |
| 13 | `statfs` `f_bavail` times `f_bsize` is the space available to unprivileged users | statfs(2) man page | confirmed |
| 14 | Opening a symbolic link to a directory with `O_DIRECTORY|O_NOFOLLOW` fails with `ENOTDIR`, and a path with a trailing slash follows the link | measured by Security in its plan review | confirmed |
| 15 | Data written with `write(2)` survives `kill -9` of the writer (page cache), but not a power loss before `fsync` | to verify: AC5 hard-kill test (Tester, step 5); power loss is simulated, never measured | unverified |
| 16 | The reference stream is about 170 MB per day encoded, so 7 days fit the 2 GiB default uncompressed | to verify: the AC7 test logs the full-scale bytes per day and the Tester runs the full-scale variant once (`VANDOX_LONG_TESTS=1`, step 5); the Lead writes the figure into the agent area (task 23) | unverified |
| 17 | The scaled AC7 test runs in under 30 s under *Test* with `-race` | to verify: Tester reports its run time in step 5 | unverified |
| 18 | Opening a spool filled to 2 GiB, and one with 1,024 files, each takes at most 5 s on the development host | to verify: `BenchmarkOpen_Full`, `BenchmarkOpen_ManyFiles` (Dev, step 6), recorded in `docs/BENCHMARKS.md` | unverified |
| 19 | The test process runs as root, so a test can `chown` a file to another user (E8 "owned by another user") | to verify: Tester checks `os.Geteuid()` in step 5; without root that one case is skipped with a reason and reported | unverified |
| 20 | `os.Root.OpenFile` with `O_NOFOLLOW` opens a symbolic link whose target lies inside the root | measured by Security in its round-2 plan review (`r.OpenFile("inlink", O_RDWR\|O_NOFOLLOW)` succeeded) | confirmed |
| 21 | `syscall` exports `Openat`, `Unlinkat` (flags 0: removes the entry, never follows), `Renameat` and `Fstat` on linux/amd64, but `Fstatat` only on some architectures (not amd64) and `AT_SYMLINK_NOFOLLOW` nowhere | Go 1.27 `src/syscall/syscall_linux.go:285,334`, `zsyscall_linux_amd64.go:1256`, `syscall_linux_amd64.go:47` (unexported `fstatat`), `syscall_linux_arm64.go:21` | confirmed |
| 22 | `openat` with `O_NOFOLLOW` on a symbolic link as the last element fails with `ELOOP`; with `O_NONBLOCK` a FIFO opens without blocking | open(2) man page; the link case is asserted by `TestOpen_RefusedEntries` (Tester, step 5) | confirmed |

## Acceptance criteria

The feature's criteria are AC1 to AC19 in [spec.md](spec.md).

## Tests per criterion

Names are top-level test functions, table-driven where the cases share a shape. Spool tests use `t.TempDir()`, an injected `Now` and
`Available`, the `writeFile` seam, and `synctest.Test` for the sync loop and the 5-second give-ups.

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `TestPut_AssignsIncreasingSequenceNumbers`, `TestPut_ConcurrentCollectors`, `TestPut_LargestRecordRoundTrips`; `TestMarshalRecord_RoundTrip`, `TestUnmarshalRecord_RejectsDamagedLine`; `TestRun_SpoolKeepsRecords` | `internal/spool/spool_test.go`; `internal/wire/encode_test.go` (both wire tests); `internal/agent/agent_test.go` |
| AC2 | `TestRead_FromSequenceNumber`, `TestRead_Limits`, `TestRead_NeverMixesCaptureContext`, `TestRead_ConcurrentWithPut` | `internal/spool/spool_test.go` |
| AC3 | `TestAck_NeverHandedOutAgain`, `TestAck_SurvivesRestart`, `TestAck_NoEffectCases`, `TestAck_TooManyRanges`, `TestAck_AllAcknowledgedShrinks` | `internal/spool/spool_test.go` |
| AC4 | `TestClose_KeepsRecordsAndContinuesNumbers`, `TestOpen_NewBootKeepsOldBootID`; `TestStore_FlushSyncsBarrierFirst`; `TestRun_StopOrder`, `TestRun_SpoolCloseGivesUp` | `internal/spool/spool_test.go`; `internal/agentstate/agentstate_test.go`; `internal/agent/agent_test.go` |
| AC5 | `TestSpool_HardKill` (re-executes the test binary as a writer, `TestSpool_HardKillHelper`, SIGKILL at least 20 times at varied moments) | `internal/spool/spool_test.go` |
| AC6 | `TestOpen_CutFiles`, `TestOpen_RolledBackDirectory`; `TestStore_SaveWaitsForBarrier` | `internal/spool/segment_test.go` (both open tests); `internal/agentstate/agentstate_test.go` |
| AC7 | `TestSpool_SevenDayOutage` (1/16 scale, always), `TestSpool_SevenDayOutageFullScale` (skipped unless `VANDOX_LONG_TESTS=1`); generator `referenceStream` in the same file | `internal/spool/spool_test.go` |
| AC8 | `TestPut_SizeBoundDropsOldest` (sum of file sizes on disk after every Put), `TestPut_DroppedGapsPerRun`, `TestPut_DroppedGapRecordsFolded`, `TestPut_DropLogLines`, `TestPut_FileLimit`, `TestOpen_RestartAppendsToNewest` | `internal/spool/spool_test.go` |
| AC9 | `TestPut_FreeSpaceReserve` (incl. available below the write size with a non-zero reserve) | `internal/spool/spool_test.go` |
| AC10 | `TestOpen_DamagedData`, `TestOpen_InconsistentValues`, `TestRead_DamagedData`, `TestOpen_RefusedEntries`, `TestOpen_TooManyFiles` | `internal/spool/segment_test.go` |
| AC11 | `TestOpen_ForeignEntriesUntouched`, `TestOpen_ManyForeignEntries` | `internal/spool/spool_test.go` |
| AC12 | `TestPut_WriteFailureEpisode`, `TestPut_LargerThanCap` | `internal/spool/spool_test.go` |
| AC13 | `TestOpen_DirectoryProblems` (incl. the link text, an unclean path, refused meta names, a failing meta write), `TestOpen_FirstStart`; `TestRun_SpoolCannotStart`, `TestRun_FirstStartNoWarning` | `internal/spool/spool_test.go`; `internal/agent/agent_test.go` |
| AC14 | `TestClockBound`, `TestOpen_SequenceAfterLoss` (fresh, removed files, burst of 100,000 in one microsecond then one second on, unclean stop in an earlier boot, kill in the same boot with only an empty newest file), `TestOpen_MetaInconsistent`, `TestPut_SequenceExhausted` | `internal/spool/meta_test.go` |
| AC15 | `TestCollect_SpoolAge` | `internal/selfstats/selfstats_test.go` |
| AC16 | `TestLoadAgent_SpoolSizes` (defaults, valid values, every E1 value with its exact text) and the adapted `TestAgentKeys`, `TestDefaultAgent` | `internal/config/agent_test.go` |
| AC17 | `TestSpool_MemoryBound` (live heap after `runtime.GC`: `spool.max_size` 64 GiB, 1,024 files after simulated reboots, 64 ranges, reads from both ends, 100,000 foreign entries); `BenchmarkOpen_Full`, `BenchmarkOpen_ManyFiles` | `internal/spool/spool_test.go` |
| AC18 | `TestOpen_OtherAgentID` | `internal/spool/meta_test.go` |
| AC19 | `TestRun_SpoolHoldsNoSecret` (runs the agent with a token whose bytes are a sentinel, then searches every spool file) | `internal/agent/agent_test.go` |

`TestOpen_RefusedEntries` cases, each for a data-file name (the newest and an older one), `spool-meta.json` and
`spool-meta.json.tmp`: a symbolic link to `agent-state.json` in the same directory (a trusted file of the agent's user, mode 0600),
a symbolic link to a file outside the directory, a hard link to `agent-state.json`, a FIFO, a directory, a file with mode 0640,
a file owned by another user (fact 19), all with a spool that then takes a `Put` and an `Ack`. Each asserts that `agent-state.json`
(and the outside file) keep their bytes, size and modification time, that the refused entry still exists unchanged (`Lstat`: still
a link with the same target, still the same inode), that no record from it is handed out, and the outcome of AC10 (data names) or
AC13 (meta names). The link to `agent-state.json` is the guard against opening through `os.Root` or without `O_NOFOLLOW`; a
spool whose open followed it would fail the unchanged-bytes assertion. `TestOpen_TooManyFiles`: 1,100 valid data files; `Open`
keeps the 1,026 with the highest numbers, hands out none of the others, leaves them on disk unread (their bytes unchanged),
logs `spool data damaged` once with `err` `too many files` and the lowest name, and stays within the AC17 memory bound.

## Approach

**Package `internal/spool`** (new), standard library only.

*Files.* Data files `segment-<20 decimal digits of the first sequence number>.spool`, the meta file `spool-meta.json` and its
temporary `spool-meta.json.tmp`; every other name is foreign (E7) and never opened, counted or deleted. `Open` refuses a `Dir` that is
not absolute and clean (`spool: directory: not an absolute, clean path`). It `Lstat`s the directory first (a link: `spool: directory:
symbolic link`; not a directory: `spool: directory: not a directory`; other errors: `spool: directory: <errno text>`), then opens it
with `O_DIRECTORY|O_NOFOLLOW|O_CLOEXEC` and keeps that descriptor; every later open, create, remove and rename is relative to it,
never by path, and only through `syscall.Openat`, `syscall.Unlinkat` and `syscall.Renameat` on a plain name without `/`. **`os.Root`
is forbidden** for every spool file operation: it follows a link that stays inside the directory even with `O_NOFOLLOW` (fact 20).
Files are created with `Openat(dir, name, O_RDWR|O_CREAT|O_EXCL|O_NOFOLLOW|O_CLOEXEC, 0600)`; opened with
`Openat(dir, name, <O_RDONLY or O_RDWR>|O_NOFOLLOW|O_NONBLOCK|O_CLOEXEC, 0)` (a symbolic link fails with `ELOOP`, a FIFO does not
block) and trusted only after `Fstat` on that descriptor shows a regular file of the effective UID without group or other bits and
with exactly one link (`Nlink` 1: a hard link to `agent-state.json` is refused like a symbolic one), else closed and refused (E8).
Writes, truncation, appends and syncs use the descriptor that passed the check (wrapped by `os.NewFile`), never a path. Before an
`Unlinkat` or `Renameat`, the entry is opened read-only as above and checked the same way (an absent entry is fine for the meta
names); a data file is removed only when its device and inode equal those of the trusted descriptor the spool holds for it.
`syscall.Fstatat` is not used: the standard library exports it on arm64 but not on amd64 (fact 21), and the open-and-`Fstat` check
refuses the same entries. Removing or renaming a name never touches a link's target, so an entry swapped between check and call costs
no file outside the spool's own. The meta temporary is removed and the meta file replaced only when each is absent or trusted (E8);
otherwise the meta write fails (`spool: meta: refused entry`). The directory is listed in chunks of 256 names through a duplicate of
its descriptor; `Open` keeps at most `maxFiles` + 2 data-file names (the highest name numbers, a bounded set while listing). Every
other data-file name is damage (E6): never opened, read, counted or deleted, and logged once per `Open` as `spool data damaged`
with `file` the lowest such name and `err` `too many files`; the backend's sequence detection finds their numbers.

*Data file format (version 1).* Header (at most 128 bytes): magic `VXSPOOL1`, version (u16), agent ID (length-prefixed, at most 64
bytes), boot ID (36 bytes), clock offset flag and value (i64), CRC-32C. Frames: payload length (u32, at most
`wire.DefaultLimits().MaxLineBytes` + 8; larger is damage, never an allocation), CRC-32C (Castagnoli) of the payload, payload =
sequence number (u64) and the wire line (`wire.MarshalRecord`). A data file holds one capture context. **Size:** a file is sealed at
`clamp(spool.max_size / 128, 1 MiB, 64 MiB)` or when its context ends, by a trailer frame (length `0xFFFFFFFF`) with the last number,
the record count, the earliest and latest capture time (gap records with their `from` and `to`) and a CRC; the sealed file is synced
before the next is created, the directory after the creation. With `spool.max_size` at most 64 GiB this is at most 128 files by size.
**File limit:** at most 1,024 data files (`maxFiles`); creating one more first drops the oldest as for the size bound, `reason`
`file_limit` (E19). **Restarts:** `Open` appends to the newest data file when its context (agent ID, boot ID, offset) matches and it is
not sealed, so a restart in the same boot adds no file.

*Consistency rules (area 10; a break is damage, E6).* File name number = first frame's number; numbers strictly increasing within a
file and across files in name order; frame number = the `seq` in its wire line; trailer last number = last frame's number and its
count = the frames counted; header and trailer within their sizes; every capture time and gap time within the wire range
(1677-09-21 to 2262-04-11, checked before a gap is built from it); a 20-digit name number parsed with `strconv.ParseUint(…, 10, 64)`
and below 2^63 - 1. A damaged frame and every later frame of that file are never handed out. At open the file is cut at the last
good frame (by `ftruncate` on its descriptor) and sealed; during a read of a sealed file the rest is marked dropped in memory and
deleted with the file. Either way one `spool_dropped` gap per run of unacknowledged numbers from the damaged frame's number (or the last
good number plus 1) to the file's last known number (trailer, else the next file's first number minus 1), with the time span of the good
frames or, when none is usable, the file's modification time; `spool data damaged` once per file, `err` one of `checksum mismatch`,
`file cut short`, `not the spool format`, `unknown format version`, `inconsistent value`, `refused entry`, `too many files`.

*Meta file.* JSON, decoded strictly (one object, `version` 1, no unknown key, at most 16 KiB): `agent_id`, `boot_id` of the last
open, `clean`, `next` (the next number at the time of the write; every meta write carries the current value), `acked` (at most 64
ranges). Rules (E20): ranges sorted, disjoint, `first <= last`, all below `next`; `next` below 2^63 - 1. A break: `spool data damaged`
with `file` `spool-meta.json`, `err` `inconsistent value`; the acknowledgements are discarded and the counter is set as on a fresh spool.
Written by temporary file and rename in the directory; synced (file and directory) at open, at close and by the sync loop when dirty;
an acknowledgement rewrites it without a sync (a lost acknowledgement only resends). When the meta was persisted while data files were
rolled back by a power loss, `acked` numbers at or above the recovered next are dropped by the rule above.

*Open.* Directory checks; read each data file's header and, for a sealed file, its trailer; scan the newest file and any file without
a valid trailer frame by frame. Files of another agent ID are deleted with `spool records of another agent dropped` (E18). The next
number is the largest of `next`, the newest file's name number, the last good number plus 1 and — on a fresh spool (no meta, a damaged
meta, or a meta of another agent ID), and when the meta is not `clean` and its `boot_id` differs from the current one —
`ClockBound(now)` (microseconds since the epoch plus 1,000,000). Then the meta is written with `clean` false and the current boot ID; a
fresh spool logs `spool created`. Memory per data file: first and last number, size, context, trailer times (under 256 bytes); a sparse
index (number and offset every file size / 64) is built for at most two files at a time, the ones the two read positions are in, and
released when a read moves on.

*Room and Put.* Under the spool's mutex: number the records (refuse with `spool: sequence numbers exhausted` before a number would
reach 2^63 - 1, E21), encode the frames, compute `need` = the frames + a new header and a trailer when a file is created or sealed +
65 gaps of 1 KiB. Size room: the sum of the data files' sizes + 32 KiB (meta file and its temporary) + `need` at most `spool.max_size`.
Disk room, skipped when `spool.min_free` is 0: `avail >= need && avail-need >= minFree` (no unsigned wrap). While there is no room,
drop the oldest sealed file; when only the active file is left, refuse (E11). Dropping writes one `spool_dropped` gap (source `agent`)
per run of unacknowledged numbers between the acknowledged ranges, `from`/`to` the trailer's times (`to` at least one second after
`from`), appended before the records. A write error truncates the active file back to its last good frame on its descriptor, refuses,
opens or extends the episode, logs `spool write failed` once; the next successful Put first appends the episode's `unknown` gap and
logs `spool writes again`. A refusal returns at once. `spool data dropped` (`reason` `size_bound`, `disk_space` or `file_limit`,
`records`) at the first drop of an episode, `spool data no longer dropped` (`records`) at the first Put that fits without dropping.

*Read and Ack.* `Read(after, maxRecords, maxBytes)` returns unacknowledged records above `after` in order, from one context only, at
most the limits (at least one record when the first fits), never beyond the last completed frame. `Ack(first, last)` merges the range;
a 65th disjoint range returns `ErrTooManyRanges` and changes nothing; numbers outside the kept range are ignored. A sealed file whose
numbers are all acknowledged is deleted; the active file never. `OldestCapture` returns the oldest unacknowledged record's capture time
(cached, re-read when the oldest moves); `LastSeq` the newest issued number.

*Durability.* At most one fsync is in flight: it copies the active file's descriptor and the dirty meta under the mutex and syncs
outside it, so a slow fsync never blocks `Put`; a caller that finds one running waits for its result (no goroutine pile-up on a hung
disk). The sync loop runs it once a second; `Sync(ctx)` runs or joins it and returns at the context's deadline. `Close` stops the loop,
syncs, writes the meta with `clean` true and syncs it.

**Single-line wire codec.** `wire.MarshalRecord` is `CheckRecord`'s validation and size check returning the line;
`wire.UnmarshalRecord` runs the decoder's line rules (`keyCheck`, text, depth) and `decodeRecord` on one line.

**agentstate.** `Store.SetBarrier` registers a function `saveLocked` calls first; when it fails the save is not written and logs `agent
state not saved` with `err` `agentstate: save: spool not durable`. `Flush` passes a context of 5 s. Collectors are unchanged: the boot
and backup window is a stated residual (spec E5, *Challenge* M1).

**agent.** `Run`: load config; `openStorage` opens the state store first (E3), then — unless `opts.Sink` is set (tests) — reads the boot
ID from `opts.Kernel.Proc` or `/proc` (`spool: boot id: not readable`, `spool: boot id: not a lower-case UUID`) and opens the spool in
`opts.StateDir` or `spool.directory`; an error ends with `agent cannot start` and exit 1 before `READY=1`. The store's barrier is
`sp.Sync`; `opts.Self.Spool` and `opts.Self.SpoolAge` take the spool when unset. Stop: `wg.Wait()`, `store.Flush` (barrier first),
`closeSpool` (at most 5 s, else `spool not closed`).

**config.** `spool.max_size` and `spool.min_free` as strings, parsed by `parseSize` (one to five ASCII digits, then exactly `MiB` or
`GiB`; checked against the bounds before the multiplication) into `Spool.MaxBytes`, `Spool.MinFreeBytes`; bounds 64 MiB to 64 GiB and
0 to 1024 GiB; error texts as spec E1.

**selfstats.** `SpoolAgeReporter` and `MetricSpoolAge`: age = run start minus the oldest capture time, 0 when none or negative.

Layout: the new output is log lines and error texts whose wording the spec and this plan fix; attributes are key=value as every
agent line.

## Options considered

1. Storage: an embedded store (bbolt, SQLite), compressed segments, one file per hand-over, or framed uncompressed data files (chosen) —
   record 0123.
2. A lost counter: refusing to start, a counter epoch in the wire header, or the clock bound (chosen) — record 0123.
3. Acknowledgements: prefix only, a bitmap, or at most 64 ranges (chosen) — record 0123.
4. Order of state and records: an fsync per hand-over, positions inside the spool, or a spool sync as barrier before every state save
   (chosen) — record 0123. Moving the boot and backup updates behind `Kept` was weighed and left for later (*Challenge* M1).
5. Bounding the file count: a fixed size of 64 MiB with no limit (16,384 files at the old 1 TiB cap), a fixed number of files whose
   size grows with the cap (unbounded file size for start-up scans), or a size of `max_size / 128` clamped to 1–64 MiB, a 64 GiB cap,
   appending after a restart and a hard limit of 1,024 files (chosen) — record 0123.
6. File access relative to the directory: `os.Root` (follows in-directory links despite `O_NOFOLLOW`, fact 20),
   `golang.org/x/sys/unix` for `Fstatat` and `AT_SYMLINK_NOFOLLOW` (a new direct dependency for one check), or `syscall.Openat`,
   `Unlinkat`, `Renameat` with an open-and-`Fstat` check (chosen) — record 0123.
7. Wiring order: opening the spool inside `jobs` (the state file would see a non-empty directory on a first start, E3) or a new
   `openStorage` before `jobs` (chosen).

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Go agent | `internal/spool/spool.go` | new: `Spool`, `Options`, `Batch`, `Open`, Put, Read, Ack, Sync, Close, providers, room, drops, episodes |
| Go agent | `internal/spool/segment.go` | new: header, frames, trailer, consistency rules, scan and cut |
| Go agent | `internal/spool/meta.go` | new: meta file and its rules, `ClockBound` |
| Go agent | `internal/spool/export_test.go` | new: the `writeFile` seam for tests |
| Go agent | `internal/wire/encode.go`, `internal/wire/decode.go` | `MarshalRecord`, `UnmarshalRecord` |
| Go agent | `internal/config/agent.go`, `internal/config/config.go` | `Spool` fields, defaults, keys, `parseSize`, validation |
| Go agent | `internal/selfstats/selfstats.go` | age provider and metric |
| Go agent | `internal/agentstate/agentstate.go` | barrier |
| Go agent | `internal/agent/agent.go` | `openStorage`, `jobs` and `newScheduler` signatures, stop order, `closeSpool`, `StateDir` doc |
| Go agent | `internal/scheduler/sink.go` | `DiscardSink` doc: test use only |
| Docs/deploy | `deploy/agent/agent.yaml`, `deploy/agent/README.md`, `README.md`, `.squad/project.md`, `.squad/stack.md`, `docs/BENCHMARKS.md` | Dev (tasks 19–22) |

## Signatures (for the Dev's skeleton)

Package spool:

- `type Spool struct`
- `type Options struct` with the fields Dir, AgentID, BootID (strings), MaxBytes, MinFreeBytes (int64), SyncInterval (time.Duration; 0 means one second), Now (a function returning time.Time; nil means time.Now) and Available (a function of the directory returning the available bytes as uint64 and an error; nil means statfs f_bavail times f_bsize)
- `type Batch struct { AgentID string; BootID string; ClockOffsetNS *int64; Records []model.Record }`
- `const MaxAckRanges = 64`
- `const maxFiles = 1024`
- `var ErrTooManyRanges error` (text "spool: more than 64 acknowledged ranges")
- `func Open(opts Options, log *slog.Logger) (*Spool, error)`
- `Spool.Put(ctx context.Context, records []model.Record) error`
- `Spool.Read(ctx context.Context, after uint64, maxRecords int, maxBytes int64) (Batch, error)`
- `Spool.Ack(ctx context.Context, first, last uint64) error`
- `Spool.LastSeq() uint64`
- `Spool.Sync(ctx context.Context) error`
- `Spool.Close(ctx context.Context) error`
- `Spool.SpoolBytes(ctx context.Context) (int64, error)`
- `Spool.OldestCapture(ctx context.Context) (time.Time, bool, error)`
- `func ClockBound(now time.Time) uint64`
- `writeFile(f *os.File, b []byte, off int64) (int, error)` (an unexported package variable of that function type, the seam the tests replace)

Package wire:

- `func MarshalRecord(r *model.Record) ([]byte, error)`
- `func UnmarshalRecord(line []byte) (model.Record, error)`

Package config:

- `type Spool struct { Directory string; MaxSize string; MinFree string; MaxBytes int64; MinFreeBytes int64 }` (YAML tags max_size, min_free, and "-" for the two parsed fields)
- `func parseSize(text string) (int64, bool)`

Package selfstats:

- `type SpoolAgeReporter interface { OldestCapture(ctx context.Context) (time.Time, bool, error) }`
- `Sources.SpoolAge`
- `const MetricSpoolAge = "agent_spool_age_seconds"`

Package agentstate:

- `Store.SetBarrier(b func(ctx context.Context) error)`

Package agent:

- `func openStorage(opts Options, cfg *config.Agent, log *slog.Logger) (*agentstate.Store, *spool.Spool, error)`
- `func jobs(opts Options, cfg *config.Agent, store *agentstate.Store, log *slog.Logger) ([]scheduler.Job, *sysstats.System, error)`
- `func newScheduler(opts Options, cfg *config.Agent, store *agentstate.Store, sink scheduler.Putter, log *slog.Logger) (*scheduler.Scheduler, error)`
- `func closeSpool(sp *spool.Spool, log *slog.Logger)`

Files the skeleton rewrites: internal/agent/agent.go (Run, jobs, newScheduler; read and not final), internal/config/agent.go
(DefaultAgent, AgentKeys, validate).

## Test files

internal/spool/spool_test.go, internal/spool/segment_test.go, internal/spool/meta_test.go, internal/wire/encode_test.go,
internal/config/agent_test.go, internal/selfstats/selfstats_test.go, internal/agentstate/agentstate_test.go,
internal/agent/agent_test.go.

Existing call sites of changed signatures, adapted by the Tester in step 5: `jobs(` in `internal/agent/agent_test.go:751,807,1217`
(three returns, the store passed in), `newScheduler(` at `:983,1092,1157` (store and sink passed in); `TestAgentKeys` and
`TestDefaultAgent` in `internal/config/agent_test.go` (two new keys after `spool.directory`, new `Spool` fields). The agent tests that
set no `Sink` now open a real spool in their `StateDir`; the kernel fixture's `/proc` holds the boot ID (fact 5).

Code a unit test cannot cover: none. The hard-kill helper runs inside the test binary; the full-scale AC7 test is a second run of the
same code at a larger size, not code of its own.

## Areas

- Agent: the spool's rules in *Spool and sending*; *Status*, *Scheduling*, *Self-metrics*, *Agent state* (barrier, the residual
  warning of spec E3), *Kernel events* and *Backup runs* (the E5 residual), *Log shipping*, *Gaps*, *Rights*, *Implementation*.
- Configuration and secrets: two options.
- Wire format: the age metric, *Batch identity* (clock bound, first number, jumps).

## Documentation updates

Sweep done for enumerations the change extends: the option tables (`docs/areas/configuration-and-secrets.md:40-55`, `README.md:67`),
`AgentKeys` and the example `deploy/agent/agent.yaml`, the metric table (`docs/areas/wire-format.md:143-144`), "the agent's only file
write" (`docs/ARCHITECTURE.md:64`, `docs/areas/agent.md:1347,1602`, `.squad/project.md:141`), "until the spool exists"
(`docs/ARCHITECTURE.md:14`, `docs/areas/agent.md:11-13,130`, `internal/scheduler/sink.go:17`), "later a full spool"
(`docs/areas/agent.md:938,1217`, record 0120 — done), *Not implemented* lists (`docs/ARCHITECTURE.md:15`), the spool directory's
contents (`deploy/agent/README.md:167-173`), the Implementation list (`docs/areas/agent.md:1681-1695`, `.squad/stack.md` *Layout*),
security areas 8, 9, 10 and 12 (`.squad/project.md:120-160,257`).

| File | Owner |
| ---- | ----- |
| `docs/areas/agent.md` | Lead (task 23) |
| `docs/areas/configuration-and-secrets.md` | Lead (task 24) |
| `docs/areas/wire-format.md` | Lead (task 25) |
| `docs/ARCHITECTURE.md` | Lead (task 26) |
| `README.md` (two option rows, the directory row) | Dev (task 19) |
| `deploy/agent/agent.yaml`, `deploy/agent/README.md` | Dev (task 20) |
| `.squad/project.md` (area 8: the token is never in the spool, `TestRun_SpoolHoldsNoSecret`; area 9: the spool's files, guards, limits and functions; area 10: `wire.UnmarshalRecord`, the data file and meta readers with their bounds; area 12: the spool's fixed log texts), `.squad/stack.md` (*Layout*: `spool`) | Dev (task 21) |
| `docs/BENCHMARKS.md` (*Spool open*: full and 1,024 files) | Dev (task 22) |
| record 0120 ("full spool") and record 0123 | Lead (done) |

## Architecture check

- *No data gaps unless explicitly recorded* (records 0028, 0045): every drop is a `spool_dropped` or `unknown` gap; stated residuals in
  the spec: a pending gap lost at a stop during a write failure, a counter collision after a clock set back, the boot and backup
  window of E5. Strengthened overall; the E5 window is new behavior that did not exist without a spool.
- *A hanging collector never blocks the agent* (0029): unchanged; a stalled disk is out of scope (the watchdog restarts the agent).
- *Agent never makes the memory situation worse* (0025): spool live memory below 4 MiB at the largest configuration (AC17).
- The state file stops being the agent's only write; `docs/ARCHITECTURE.md` and `.squad/project.md` area 9 are updated (tasks 21, 26).
  Record 0045 (released) is amended by 0123, not edited.

## Security considerations

- [ ] Limits on parsed input, all on raw bytes before decoding: a frame payload at most `wire.DefaultLimits().MaxLineBytes` + 8; a
  header at most 128 bytes; a trailer of fixed size; the meta file at most 16 KiB with at most 64 ranges; a name number of 20 digits
  parsed into 64 bits and below 2^63 - 1; directory listing in chunks of 256, at most 1,026 data-file names held; at most 1,024 data
  files created; every open by `Openat` with `O_NOFOLLOW`, trusted only as a regular, agent-owned, 0600, single-link file; sparse indexes for two files of
  64 entries each; `wire.UnmarshalRecord` applies the decoder's line rules to the raw line.
- [ ] Texts that reach a user: the start errors under *Approach*, `spool: write: <errno text>`, `spool: sequence numbers exhausted`,
  `spool: meta: refused entry`, `spool data damaged` with the fixed `err` values under *Approach*. No path, record content or system
  error string other than the errno text.

## Decision records

- [`docs/decisions/0123-agent-spool-segment-files-clock-bound-sequence-numbers-range-acknowledgements.md`](../../docs/decisions/0123-agent-spool-segment-files-clock-bound-sequence-numbers-range-acknowledgements.md) (Proposed; amends 0045)

## Challenge

- M1 (boot and backup state written before `Put`). Accepted as a stated residual, not fixed: spec E5 and NFR *Durability* now name the
  window, fact 3 is corrected, and the agent area states it (task 23). Moving the two updates behind `Kept` changes two collectors and
  their tests for a window of one sync interval after a save that falls between a run's state change and its hand-over, followed by
  a power loss; the boot stays visible in the batch headers' boot ID. A follow-up issue is proposed (below).
- M2 (memory bound at large caps). Accepted: file size `clamp(max_size / 128, 1 MiB, 64 MiB)`, `spool.max_size` at most 64 GiB, a
  hard limit of 1,024 data files (E19), restarts append, sparse indexes for at most two files; AC17 tests the largest configuration.
- m1 (next after a kill). Accepted: every meta write carries the current next, and `Open` takes the newest file's name number into
  the maximum; AC14 tests the empty-newest-file case.
- m2 (frame number and limit constant). Accepted: frame number must equal the line's `seq` (damage otherwise);
  `wire.DefaultLimits().MaxLineBytes` is used.
- m3 (damage found while reading). Accepted: rest of that file dropped with gaps from the damaged number, logged once, never handed
  out; `TestRead_DamagedData`.
- m4 (spurious state warning). Accepted as a stated residual (spec E3, agent area): the warning is once, after a start that ended
  within its first seconds; changing the state file's first-start rule would weaken its check for other entries.
- m5 (fsync outside the mutex). Accepted: *Durability*.
- m6 (AC7 cost). Accepted and decided now: the scaled test always runs; the full-scale test runs with `VANDOX_LONG_TESTS=1`, once
  by the Tester in step 5, its result in the log and in the agent area.

## Security review

- B1 (unbounded file count). Resolved as M2: hard limit of 1,024 data files with drops (`reason` `file_limit`), appending after a
  restart in the same boot, per-file memory under 256 bytes in the bound, `TestSpool_MemoryBound` and `BenchmarkOpen_ManyFiles` at
  1,024 files left by simulated reboots.
- B2 (values read from disk). Resolved: *Consistency rules* and the meta rules under *Approach*; a break is damage (E6, E20) and never
  yields a number or a gap from a damaged value; E21 refuses a number at 2^63 - 1; tests `TestOpen_InconsistentValues`,
  `TestOpen_MetaInconsistent`, `TestPut_SequenceExhausted`.
- B3 (free-space underflow). Resolved: `avail >= need && avail-need >= minFree`; `TestPut_FreeSpaceReserve` has the case.
- N1 (size accounting). Resolved: `need` counts headers, trailers and gaps; the bound counts the meta file and its temporary;
  AC8 measures the files on disk.
- N2 (descriptors, unclean path). Resolved: every operation through the directory descriptor or the file descriptor that passed
  `fstat`; `Open` refuses an unclean `Dir`.
- N3 (link text). Resolved: `Lstat` first (fact 14); AC13 checks the text.
- N4 (meta names against E8). Resolved: the temporary is removed and the meta replaced only when absent or trusted; spec E8.
- N5 (one fsync in flight). Resolved: *Durability*.
- N6 (project.md areas). Resolved: task 21 covers areas 8, 9, 10 and 12; AC19 with `TestRun_SpoolHoldsNoSecret`.
- R1 (round 2: `os.Root` ignores `O_NOFOLLOW` for in-directory links). Resolved: `os.Root` is forbidden; every open is
  `syscall.Openat` with `O_NOFOLLOW|O_NONBLOCK|O_CLOEXEC`, every removal and rename `Unlinkat`/`Renameat` after an open-and-`Fstat`
  check (facts 20–22). `Fstatat` with `AT_SYMLINK_NOFOLLOW` as asked is not exported by `syscall` on amd64; the open with `O_NOFOLLOW`
  refuses the same entries, and a data file is removed only when its device and inode match the descriptor the spool trusted. While
  re-checking the whole list of accepted forms: a hard link to `agent-state.json` passed every check of round 1 (regular, agent-owned,
  0600) and would have been cut as damage; the trust check now requires one link (`Nlink` 1). `TestOpen_RefusedEntries` covers a
  symbolic and a hard link to `agent-state.json`, a link outside, a FIFO, a directory, a group bit and another owner, for every
  spool name, asserting the target's bytes, size and modification time unchanged and the link not followed or removed.
- N7 (round 2: listing keeps every spool-named entry). Resolved: `Open` holds at most 1,026 data-file names (the highest numbers);
  the rest are damage, never opened or deleted, one warning per `Open` (`err` `too many files`); `TestOpen_TooManyFiles`.
- Note on M1 (round 2): a lost `boot` or `backup_run` record in the E5 window is a loss no gap records, against the guarantee *No
  data gaps unless explicitly recorded*. Decided by the Lead in `decide` mode: escalated to the Product Manager (see the orchestrator's
  log); the follow-up issue below is opened before the pull request either way.

## Out of scope / follow-ups

As in the spec. Follow-up issue for the orchestrator to open: "[Agent] Save boot and backup state only after the run's records are
kept" — body: the `kernel` collector's boot change (`internal/kernevents/boot.go:62`) and the `backups` collector's run state
(`internal/backups/backups.go:198,205,217`) are written to the state file inside `Collect`, before the spool accepted the run's records;
a state save in between followed by a power loss loses the `boot` event or the `backup_run` record (spec E5 residual of #38). Commit
both behind the scheduler's `Kept` observer as the `logs` collector does. Comments on existing issues: #41 (an agent's first sequence
number is large; a jump after a fresh start or a crash is reported as `sequence_missing`, numbers below the first never), #42
(`Restart=on-failure` with `RestartSec=` and start limits that keep retrying a failed start).
