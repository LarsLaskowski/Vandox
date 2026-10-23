# Plan: Persistent on-disk spool (at least 7 days)

Source: Issue #38 | [spec.md](spec.md)
Status: Revised (4) — M1 settled by the Product Manager; G3 test details decided by the Lead after step 5
Tier: security — the spool creates guards of security area 9 (no link followed, owner and mode checks on its files, nothing written or deleted outside its own files, the size bound, the file limit and the free-space reserve) and reads its own files back under consistency rules.
Security areas touched: 8 (the agent token must never reach the spool), 9 (file writes and paths: the spool's files in `spool.directory`, the meta file, deletion of its own files), 10 (parsing: spool files and the meta file read back, damaged or hostile, through a new single-line entry point of the wire decoder), 12 (new fixed log texts of the spool)

## Problem / root cause

See [spec.md](spec.md), *Problem*. The agent's sink is `scheduler.DiscardSink` (`internal/agent/agent.go:304`), so nothing is kept.

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | Today every record is dropped; the sink assigns sequence numbers | `internal/agent/agent.go:304-307`, `internal/scheduler/sink.go:9-23` | confirmed |
| 2 | `Put` is called concurrently, one runner goroutine per collector, with the agent's context; a sink error makes the run "not kept" | `internal/scheduler/runner.go:349-364`, `scheduler.go:73-80`, `docs/areas/agent.md:88-91,127-129` | confirmed |
| 3 | `logs` positions are committed in `Kept` after `Put`, the kernel journal position in the next run's prepare; the boot change and the backup run are written by `store.Update` inside `Collect`, before `Put`; the `state` job saves on its own goroutine | `internal/logship/logship.go:261-300`, `internal/kernevents/kernevents.go:141-147`, `internal/kernevents/boot.go:62`, `internal/backups/backups.go:198,205,217`, `internal/agentstate/agentstate.go:699-720` | confirmed |
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
| 16 | The reference stream is 209.0 MB per day encoded (544,560 records; not the estimated 170 MB), so 7 days take about 1.49 GB with the spool's framing, about 70 % of the 2 GiB default | `TestReferenceStream_FullScaleDay`, measured by the Tester in step 5; the Lead writes the figure into the agent area (task 23) | confirmed |
| 17 | The scaled AC7 test at 1/80 runs in under 30 s under *Test* with `-race`, and the full-scale variant passes (at 1/16 the generator and the wire codec alone took about 80 s under `-race`: refuted in step 5) | to verify: Dev reports the run time of the first green run in step 6 and runs the full-scale variant once without `-race` (its cost would pass Go's 10-minute limit): `VANDOX_LONG_TESTS=1 go test ./internal/spool -run '^TestSpool_SevenDayOutageFullScale$' -timeout 30m` | unverified |
| 18 | Opening a spool filled to 2 GiB, and one with 1,024 files, each takes at most 5 s on the development host | to verify: `BenchmarkOpen_Full`, `BenchmarkOpen_ManyFiles` (Dev, step 6), recorded in `docs/BENCHMARKS.md` | unverified |
| 19 | The test process runs as root, so a test can `chown` a file to another user (E8 "owned by another user") | to verify: Tester checks `os.Geteuid()` in step 5; without root that one case is skipped with a reason and reported | unverified |
| 20 | `os.Root.OpenFile` with `O_NOFOLLOW` opens a symbolic link whose target lies inside the root | measured by Security in its round-2 plan review (`r.OpenFile("inlink", O_RDWR\|O_NOFOLLOW)` succeeded) | confirmed |
| 21 | `syscall` exports `Openat`, `Unlinkat` (flags 0: never follows), `Renameat` and `Fstat` on linux/amd64, but `Fstatat` not on amd64 and `AT_SYMLINK_NOFOLLOW` nowhere | Go 1.27 `src/syscall/syscall_linux.go:285,334`, `zsyscall_linux_amd64.go:1256`, `syscall_linux_amd64.go:47` (unexported `fstatat`), `syscall_linux_arm64.go:21` | confirmed |
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
| AC7 | `TestSpool_SevenDayOutage` (1/80 scale, always), `TestSpool_SevenDayOutageFullScale` (skipped unless `VANDOX_LONG_TESTS=1`); generator `refStream`, `TestReferenceStream_Shape`, `TestReferenceStream_FullScaleDay` | `internal/spool/outage_test.go`, `internal/spool/refstream_test.go` |
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

The G3 tests are split by topic over `internal/spool/{spool,segment,meta,limits,open,outage,refstream,forge,helpers}_test.go`;
the test names above are binding, the file column only where it names a file outside `internal/spool`.

`TestOpen_RefusedEntries` cases, each for a data-file name (the newest and an older one), `spool-meta.json` and
`spool-meta.json.tmp`: a symbolic link to `agent-state.json` in the same directory (agent-owned, 0600), a symbolic link outside the
directory, a hard link to `agent-state.json`, a FIFO, a directory, mode 0640, another owner (fact 19); the spool then takes a `Put`
and an `Ack`. Each asserts that `agent-state.json` and the outside file keep bytes, size and modification time, that the refused
entry is unchanged (`Lstat`: same link target, same inode), that nothing from it is handed out, and the outcome of AC10 (data names)
or AC13 (meta names). The link to `agent-state.json` is the guard that fails for an open through `os.Root` or without `O_NOFOLLOW`. `TestOpen_TooManyFiles`: 1,100 valid data files; `Open`
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
`syscall.Fstatat` is not used (fact 21); the open-and-`Fstat` check stands in for it (to be confirmed by Security, *Security
review* R1). Removing or renaming a name never touches a link's target, so an entry swapped between check and call costs
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

*Consistency rules (area 10; a break is damage).* Those of spec E6, plus: trailer count = the frames counted; header and trailer
within their sizes; times within 1677-09-21 to 2262-04-11, checked before a gap is built from them; a 20-digit name number parsed
with `strconv.ParseUint(…, 10, 64)` and below 2^63 - 1. At open a damaged file is cut at the last good frame (`ftruncate` on its
descriptor) and sealed; during a read of a sealed file the rest is marked dropped in memory and deleted with the file. Gaps as spec
E6, one per run of unacknowledged numbers, numbers and span as item 6 of *Details decided after the G3 tests*. `err` is one of `checksum mismatch`, `file cut short`, `not the
spool format`, `unknown format version`, `inconsistent value`, `refused entry`, `too many files`.

*Meta file.* JSON, decoded strictly (one object, `version` 1, no unknown key, at most 16 KiB): `agent_id`, `boot_id` of the last
open, `clean`, `next` (the next number at the time of the write; every meta write carries the current value), `acked` (at most 64
ranges). Its rules and the reaction to a break are spec E20 (`err` `inconsistent value`).
Written by temporary file and rename in the directory; synced (file and directory) at open, at close and by the sync loop when dirty;
an acknowledgement rewrites it without a sync (a lost acknowledgement only resends); `acked` numbers at or above a next recovered
after a power loss break E20's rule.

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
1 KiB per gap owed at that moment (counted again after every drop). A hand-over that alone (with its owed gaps and the 32 KiB)
exceeds `spool.max_size` is refused before anything is dropped (E11). Size room: the sum of the data files' sizes + 32 KiB (meta file and its temporary) + `need` at most `spool.max_size`.
Disk room, skipped when `spool.min_free` is 0: `avail >= need && avail-need >= minFree` (no unsigned wrap). While there is no room,
drop the oldest sealed file; when only the active file is left, refuse (E11). Dropping writes one `spool_dropped` gap (source `agent`)
per run of unacknowledged numbers between the acknowledged ranges, `from`/`to` as item 5 below (`to` at least one second after
`from`), numbered before the records (item 4). A write error truncates the active file back to its last good frame on its descriptor, refuses,
opens or extends the episode, logs `spool write failed` once; the next successful Put first appends the episode's `unknown` gap and
logs `spool writes again`. A refusal returns at once. `spool data dropped` (`reason` `size_bound`, `disk_space` or `file_limit`,
`records`) at the first drop of an episode, `spool data no longer dropped` (`records`) at the first Put that fits without dropping.

*Read and Ack.* `Read(after, maxRecords, maxBytes)` returns unacknowledged records above `after` in order, from one context only, at
most the limits (at least one record when the first fits; an empty batch when the first record alone exceeds `maxBytes`), never beyond the last completed frame. `Ack(first, last)` merges the range;
a 65th disjoint range returns `ErrTooManyRanges` and changes nothing; numbers outside the kept range are ignored. A sealed file whose
numbers are all acknowledged is deleted; the active file never. `OldestCapture` returns the oldest unacknowledged record's capture time
(cached, re-read when the oldest moves); `LastSeq` the newest issued number.

*Durability.* At most one fsync is in flight: it copies the active file's descriptor and the dirty meta under the mutex and syncs
outside it, so a slow fsync never blocks `Put`; a caller that finds one running waits for its result (no goroutine pile-up on a hung
disk). The sync loop runs it once a second; `Sync(ctx)` runs or joins it and returns at the context's deadline. `Close` stops the loop,
syncs, writes the meta with `clean` true and syncs it.

*Details decided after the G3 tests (binding; the tests pin them).*

1. Byte layout of version 1, every integer little-endian. Header: `VXSPOOL1`, version u16, agent ID length u8 (1 to 64), agent ID,
   boot ID (36 bytes), clock offset flag u8 (0 or 1), clock offset i64 (0 when the flag is 0), CRC-32C u32 of every header byte
   before it. Frame: payload length u32, CRC-32C u32 of the payload, payload = sequence number u64 and the wire line. Trailer:
   `0xFFFFFFFF` u32, last number u64, record count u64, earliest and latest capture time i64 (Unix nanoseconds), CRC-32C u32 of the
   32 bytes after the marker. The extension is `.spool`: the skeleton's `segmentExt` `.seg` changes.
2. Meta file: `{"version":1,"agent_id":…,"boot_id":…,"clean":…,"next":…,"acked":[{"first":…,"last":…}]}`, every key required.
   Bytes that do not decode as JSON are `not the spool format`, another version `unknown format version`, a missing, unknown or
   repeated key and every E20 break `inconsistent value`.
3. Every write of a data or meta file goes through `writeFile`. A failed meta write returns `spool: meta: <error text>` (from `Open`:
   the start fails, E2; from `Ack`, `Sync`, `Close`: their error) and neither opens nor ends the E9 episode, which only `Put`'s data
   writes do; a failed data write returns `spool: write: <error text>`.
4. A `Put` numbers, contiguously and in this order: the episode's `unknown` gap (when this `Put` ends an episode), its
   `spool_dropped` gaps in the order of their runs, its records. Dropped numbers that form one run across several files of one `Put`
   give one gap; the episode logs one warning. The `unknown` gap spans the earliest to the latest capture time of every record refused
   in the episode (E9, E11, E21), at least one second. Every gap the spool writes has origin `agent`, source `agent` and as capture
   time the spool's clock at the write. The drop lines alternate: warning at an episode's first drop, info at the first `Put` that
   fits without one.
5. The span of an eviction gap (AC8) is exact per run: the earliest and latest capture time of the run's records, dropped gap spans
   included; from the trailer when the run is the whole file, otherwise by scanning the file frame by frame under the mutex (one
   frame buffer, no index kept).
6. Damage gaps (E6, AC10). A run starts at the last good number + 1, or at the name number when no frame is good, and ends at the
   trailer's last number when the trailer is intact and within range, else at the next file's first number − 1; at open the newest
   file without an intact trailer gets no gap for its tail (the backend's sequence detection finds it). Damage found by a read in a
   file without a later file (the active file) ends at the file's last number held in memory, `to` its latest capture time held in
   memory: the spool wrote or checked those itself. A read-time damage gap is written by that `Read`/`OldestCapture` when it fits
   without a drop, else owed to the next `Put`; the file is not cut on disk, so a restart before its deletion finds the damage again
   and writes a second gap for the same numbers and logs the warning again (stated residual: the gap names numbers already named). A frame whose number differs from
   its line's `seq` is damage at the frame's place; the line's number is never used. A file whose name breaks a rule (not its first
   record's number, not above the previous file's last number, at or above 2^63 − 1) gets no gap, and its name never raises the next
   number. Span: `from` the latest capture time of the good frames before the damage, else the previous file's latest; `to` the
   trailer's latest when intact and in range, else the next file's earliest; a missing side is the other one ∓ 1 s, both missing the
   spool's clock; `to` at least one second after `from`. (This replaces "the time span of the good frames", which never covered a
   dropped record.) A trusted data file damaged as a whole is deleted at open once its gap is written; refused entries (E8) and the
   files beyond the 1,026 (E19) are never touched.
7. `Read` may end a piece at a file boundary or go on into the next file of the same context.
8. `Ack` applies to every issued, kept, unacknowledged number of the range, whether a `Read` handed it out or not (the spool tracks no
   reads); numbers never issued (below the oldest, above the newest, inside a jump) are ignored (spec E12).
9. A record-less newest file of the current context is appended to when the next number equals its name number (the max rule raises
   next to it); otherwise it is removed and a file is created under the next number. A record-less file is never sealed. A hand-over
   whose last number is 2^63 − 2 is accepted.

**Single-line wire codec.** `wire.MarshalRecord` is `CheckRecord`'s validation and size check returning the line;
`wire.UnmarshalRecord` runs the decoder's line rules (`keyCheck`, text, depth) and `decodeRecord` on one line.

**agentstate.** `Store.SetBarrier` registers a function `saveLocked` calls first; when it fails the save is not written and logs `agent
state not saved` with `err` `agentstate: save: spool not durable`. `Flush` passes a context of 5 s. Collectors are unchanged: the boot
and backup window is a stated residual decided by the Product Manager (spec E5, *Challenge* M1).

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

The alternatives for storage, a lost counter, acknowledgements, the order of state and records, the file count and file access
inside the directory, with why each lost, are in record 0123. Moving the boot and backup updates behind `Kept` was weighed and left
to the follow-up issue (*Challenge* M1). Wiring order: opening the spool inside `jobs` lost to a new `openStorage` before `jobs`,
because the state file would see a non-empty directory on a first start (E3).

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

internal/spool/spool_test.go, internal/spool/segment_test.go, internal/spool/meta_test.go, internal/spool/limits_test.go,
internal/spool/open_test.go, internal/spool/outage_test.go, internal/spool/refstream_test.go, internal/spool/forge_test.go,
internal/spool/helpers_test.go, internal/wire/encode_test.go,
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
  warning of spec E3), *Kernel events* and *Backup runs* (the E5 residual, naming the follow-up issue by number), *Log shipping*,
  *Gaps*, *Rights*, *Implementation*.
- Configuration and secrets: two options.
- Wire format: the age metric, *Batch identity* (clock bound, first number, jumps).

## Documentation updates

Sweep hits: option tables (`docs/areas/configuration-and-secrets.md:40-55`, `README.md:67`, `deploy/agent/agent.yaml`); metric table
(`docs/areas/wire-format.md:143-144`); "only file write" (`docs/ARCHITECTURE.md:64`, `docs/areas/agent.md:1347,1602`,
`.squad/project.md:141`); "until the spool exists" (`docs/ARCHITECTURE.md:14-15`, `docs/areas/agent.md:11-13,130`,
`internal/scheduler/sink.go:17`); "later a full spool" (`docs/areas/agent.md:938,1217`; record 0120 done); the directory's contents
(`deploy/agent/README.md:167-173`); Implementation lists (`docs/areas/agent.md:1681-1695`, `.squad/stack.md`); security areas
(`.squad/project.md:120-160,257`).

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
  window of E5. The Product Manager decided to keep the E5 window as a stated residual with the guarantee's text unchanged; the
  follow-up issue closes it before the agent's first release.
- *A hanging collector never blocks the agent* (0029): unchanged; a stalled disk is out of scope (the watchdog restarts the agent).
- *Agent never makes the memory situation worse* (0025): spool live memory below 4 MiB at the largest configuration (AC17).
- The state file stops being the agent's only write; `docs/ARCHITECTURE.md` and `.squad/project.md` area 9 are updated (tasks 21, 26).
  Record 0045 (released) is amended by 0123, not edited.

## Security considerations

- [ ] Limits on parsed input, all on raw bytes before decoding, and the file guards: as under *Approach* (frame, header, trailer,
  meta size and ranges, name numbers, listing chunks and held names, file limit, `Openat` with `O_NOFOLLOW` and the trust check,
  sparse indexes, `wire.UnmarshalRecord`'s line rules).
- [ ] Texts that reach a user: the start errors under *Approach*, `spool: write: <errno text>`, `spool: sequence numbers exhausted`,
  `spool: meta: refused entry`, `spool data damaged` with the fixed `err` values under *Approach*. No path, record content or system
  error string other than the errno text.

## Decision records

- [`docs/decisions/0123-agent-spool-segment-files-clock-bound-sequence-numbers-range-acknowledgements.md`](../../docs/decisions/0123-agent-spool-segment-files-clock-bound-sequence-numbers-range-acknowledgements.md) (Proposed; amends 0045)

## Challenge

- M1 (boot and backup state written before `Put`). Accepted as a stated residual, decided by the Product Manager (option b of the
  escalation): spec E5 and NFR *Durability* name the window, the guarantee text stays unchanged, the agent area states the residual
  with the follow-up issue (task 23), record 0123 names it under *Consequences*; the follow-up is opened before the pull request and
  merged before the agent's first release. Fixing it here and a permanent exception lost.
- M2 (memory bound at large caps). Accepted: file size, 64 GiB cap, 1,024-file limit (E19), append after restart, two sparse indexes;
  AC17.
- m1 (next after a kill). Accepted: every meta write carries next; `Open` includes the newest name number; AC14.
- m2 (frame number, limit constant). Accepted: frame number equals the line's `seq`; `wire.DefaultLimits().MaxLineBytes`.
- m3 (damage while reading). Accepted: `TestRead_DamagedData`.
- m4 (spurious state warning). Accepted as a stated residual (spec E3); changing the first-start rule would weaken its check.
- m5 (fsync outside the mutex). Accepted: *Durability*.
- m6 (AC7 cost). Accepted: scaled always, full scale with `VANDOX_LONG_TESTS=1` once. Lead decision after step 5 (fact 17 refuted):
  scale 1/80 instead of 1/16. Accepting about 90 s lost to `.squad/stack.md` (*Test time limits*: over a minute is a warning sign);
  skipping the scaled run under the race detector lost because *Test* and CI always run `-race`, so AC7 would never run. 1/80 divides
  every daily count and keeps the file size at its 1 MiB floor, so overhead and the one-sixteenth check scale as at 1/16; the
  capacity claim rests on the full-scale run (fact 17) and the measured bytes per day (fact 16).

## Security review

- B1 (file count), B2 (values read from disk), B3 (free-space underflow), N1 (size accounting), N2 (descriptors, unclean path), N3
  (link text), N4 (meta names), N5 (one fsync), N6 (project.md areas): resolved under *Approach* and task 21, with the tests in
  *Tests per criterion*.
- R1 (round 2: `os.Root` follows in-directory links). Resolved: `os.Root` forbidden; `syscall.Openat` with
  `O_NOFOLLOW|O_NONBLOCK|O_CLOEXEC`, `Unlinkat`/`Renameat` after an open-and-`Fstat` check, `Nlink` 1 (a hard link to
  `agent-state.json` passed round 1's checks), removal only on matching device and inode (facts 20–22). `Fstatat` with
  `AT_SYMLINK_NOFOLLOW` is not available from `syscall` on amd64 (fact 21): **Security confirms this substitution** in its diff review.
- N7 (round 2: listing). Resolved: at most 1,026 data-file names held; `TestOpen_TooManyFiles`.
- M1 note (round 2: silent loss against *No data gaps unless explicitly recorded*). Escalated; the Product Manager chose the stated
  residual with the follow-up issue below (*Challenge* M1).

## Out of scope / follow-ups

As in the spec. Follow-up issue the orchestrator opens before the pull request (final):

- Title: `[Agent] Save boot and backup state only after the run's records are kept`
- Body: The `kernel` collector's boot change (`internal/kernevents/boot.go:62`) and the `backups` collector's run state
  (`internal/backups/backups.go:198,205,217`) are written to the state file inside `Collect`, before the spool accepted the run's
  records. A state save in that moment followed by a power loss within the next sync interval loses the `boot` event or the
  `backup_run` record while the state file already holds them, so they are never recorded again and no gap says so. #38 keeps this
  as a stated residual (spec E5, record 0123, agent area); it breaks the guarantee *No data gaps unless explicitly recorded* and
  must be closed before the agent's first release. Commit both state changes behind the scheduler's `Kept` observer, as the `logs`
  collector does, with tests for a save that falls between the change and the hand-over. Label: `bug`; milestone: the agent's first
  release. Comments on existing issues: #41 (an agent's first sequence
number is large; a jump after a fresh start or a crash is reported as `sequence_missing`, numbers below the first never), #42
(`Restart=on-failure` with `RestartSec=` and start limits that keep retrying a failed start).
