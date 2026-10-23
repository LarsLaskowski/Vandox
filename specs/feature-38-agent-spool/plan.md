# Plan: Persistent on-disk spool (at least 7 days)

Source: Issue #38 | [spec.md](spec.md)
Status: Draft
Tier: security — the spool creates guards of security area 9 (no link followed, owner and mode checks on its files, nothing written or deleted outside its own files, the size bound and the free-space reserve) and reads its own files back through the wire line decoder.
Security areas touched: 9 (file writes and paths: the spool's files in `spool.directory`, the meta file, deletion of its own segments), 10 (parsing: spool files read back, damaged or hostile, through a new single-line entry point of the wire decoder)

## Problem / root cause

See [spec.md](spec.md), *Problem*. The agent's sink is `scheduler.DiscardSink` (`internal/agent/agent.go:304`), so nothing is kept.

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | Today every record is dropped; the sink assigns sequence numbers | `internal/agent/agent.go:304-307`, `internal/scheduler/sink.go:9-23` | confirmed |
| 2 | `Put` is called concurrently, one runner goroutine per collector, with the agent's context; a sink error makes the run "not kept" | `internal/scheduler/runner.go:349-364`, `scheduler.go:73-80`, `docs/areas/agent.md:88-91,127-129` | confirmed |
| 3 | The `logs` collector commits positions once the run is kept and hands them to the state file as an urgent change; kills, boots and backup changes are urgent; a save syncs file and directory | `docs/areas/agent.md:1203-1205,1369-1376`, `internal/agentstate/agentstate.go:273-285,624-655` | confirmed |
| 4 | The state file treats only an empty directory as a first start; an absent file beside other entries warns | `internal/agentstate/agentstate.go:207-242` | confirmed |
| 5 | The boot ID is read by `procstats.ReadBootID` from `sys/kernel/random/boot_id` of a `/proc` `fs.FS`; the agent tests' kernel fixture holds that file | `internal/procstats/boottime.go:22,122-140`; `internal/agent/agent_test.go:326,337` | confirmed |
| 6 | No clock offset estimate exists in the agent | `grep -rn clock_offset internal` finds only the wire header | confirmed |
| 7 | The strict decoder answers a list with `invalid value, want a string` and a null with `has no value` before any option rule (spec E1 corrected) | `internal/config/decode.go:21-22,235-258` | confirmed |
| 8 | The example `deploy/agent/agent.yaml` must set every key of `AgentKeys()`; `TestAgentKeys` pins the key order | `internal/config/agent_test.go:94-114,195-209` | confirmed |
| 9 | The wire package has no exported single-record line codec (`marshalLine`, `decodeRecord` are unexported) | `internal/wire/encode.go:23-37`, `decode.go:500-527` | confirmed |
| 10 | `selfstats` has a spool size provider and no age metric | `internal/selfstats/selfstats.go:28-56,103-113` | confirmed |
| 11 | Go 1.27 with `testing/synctest`, already used by the scheduler tests | `go.mod:3`, `internal/scheduler/runner_test.go:13` | confirmed |
| 12 | Records 0110, 0120, 0121 are unreleased; 0028, 0045, 0046 are released (v0.1.0) | `git ls-remote --tags origin` (v0.1.0 only), `git tag --contains` of each record's adding commit | confirmed |
| 13 | `statfs` `f_bavail` times `f_bsize` is the space available to unprivileged users | statfs(2) man page | confirmed |
| 14 | Data written with `write(2)` survives `kill -9` of the writer (page cache), but not a power loss before `fsync` | to verify: AC5 hard-kill test (Tester, step 5) shows every accepted record after SIGKILL; power loss is simulated, never measured | unverified |
| 15 | The reference stream is about 170 MB per day encoded, so 7 days fit the 2 GiB default uncompressed | to verify: the AC7 test logs the encoded bytes per day (Tester, step 5); the Lead writes the figure into the agent area (task 23) | unverified |
| 16 | The AC7 test (about 1.2 GB written and read back) runs in under 120 s under *Test* with `-race`, and the CI runner has the disk space | to verify: Tester reports its run time in step 5; CI disk in step 11 (orchestrator). Over 120 s goes to the Lead (mode `decide`) | unverified |
| 17 | Opening a spool filled to 2 GiB takes at most 5 s on the development host | to verify: `BenchmarkOpen_Full` (Dev, step 6), recorded in `docs/BENCHMARKS.md` | unverified |
| 18 | The test process runs as root, so a test can `chown` a file to another user (E8 "owned by another user") | to verify: Tester checks `os.Geteuid()` in step 5; without root that one case is skipped with a reason and the Tester reports it | unverified |
| 19 | One spool write path per segment append can be failed in tests without root (root ignores a read-only mode) | the plan adds the seam `writeFile` (below); `internal/wire/export_test.go` shows the pattern | confirmed |

## Acceptance criteria

The feature's criteria are AC1 to AC18 in [spec.md](spec.md).

## Tests per criterion

Go tests are table-driven where the cases share a shape; names below are the top-level functions. All spool tests use `t.TempDir()`, an
injected `Now` and `Available`, and `synctest.Test` for the sync loop and the 5-second give-ups.

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `TestPut_AssignsIncreasingSequenceNumbers`, `TestPut_ConcurrentCollectors`, `TestPut_LargestRecordRoundTrips`; `TestMarshalRecord_RoundTrip`, `TestUnmarshalRecord_RejectsDamagedLine`; `TestRun_SpoolKeepsRecords` | `internal/spool/spool_test.go`; `internal/wire/encode_test.go` (both wire tests); `internal/agent/agent_test.go` |
| AC2 | `TestRead_FromSequenceNumber`, `TestRead_Limits`, `TestRead_NeverMixesCaptureContext`, `TestRead_ConcurrentWithPut` | `internal/spool/spool_test.go` |
| AC3 | `TestAck_NeverHandedOutAgain`, `TestAck_SurvivesRestart`, `TestAck_NoEffectCases`, `TestAck_TooManyRanges`, `TestAck_AllAcknowledgedShrinks` | `internal/spool/spool_test.go` |
| AC4 | `TestClose_KeepsRecordsAndContinuesNumbers`, `TestOpen_NewBootKeepsOldBootID`; `TestStore_FlushSyncsBarrierFirst`; `TestRun_StopOrder`, `TestRun_SpoolCloseGivesUp` | `internal/spool/spool_test.go`; `internal/agentstate/agentstate_test.go`; `internal/agent/agent_test.go` |
| AC5 | `TestSpool_HardKill` (re-executes the test binary as a writer, `TestSpool_HardKillHelper`, kills it with SIGKILL at least 20 times at varied moments) | `internal/spool/spool_test.go` |
| AC6 | `TestOpen_CutFiles`, `TestOpen_RolledBackDirectory`; `TestStore_SaveWaitsForBarrier` | `internal/spool/segment_test.go` (both open tests); `internal/agentstate/agentstate_test.go` |
| AC7 | `TestSpool_SevenDayOutage` (reference stream generator `referenceStream` in the same file) | `internal/spool/spool_test.go` |
| AC8 | `TestPut_SizeBoundDropsOldest`, `TestPut_DroppedGapsPerRun`, `TestPut_DroppedGapRecordsFolded`, `TestPut_DropLogLines` | `internal/spool/spool_test.go` |
| AC9 | `TestPut_FreeSpaceReserve` | `internal/spool/spool_test.go` |
| AC10 | `TestOpen_DamagedData`, `TestRead_DamagedData`, `TestOpen_RefusedEntries` | `internal/spool/segment_test.go` |
| AC11 | `TestOpen_ForeignEntriesUntouched`, `TestOpen_ManyForeignEntries` | `internal/spool/spool_test.go` |
| AC12 | `TestPut_WriteFailureEpisode`, `TestPut_LargerThanCap` | `internal/spool/spool_test.go` |
| AC13 | `TestOpen_DirectoryProblems`, `TestOpen_FirstStart`; `TestRun_SpoolCannotStart`, `TestRun_FirstStartNoWarning` | `internal/spool/spool_test.go`; `internal/agent/agent_test.go` |
| AC14 | `TestClockBound`, `TestOpen_SequenceAfterLoss` (fresh, removed segments, burst of 100,000 in one microsecond then one second on, unclean stop in an earlier boot, upper limit) | `internal/spool/meta_test.go` |
| AC15 | `TestCollect_SpoolAge` | `internal/selfstats/selfstats_test.go` |
| AC16 | `TestLoadAgent_SpoolSizes` (defaults, valid values, every E1 value with its exact error text) and the adapted `TestAgentKeys`, `TestDefaultAgent` | `internal/config/agent_test.go` |
| AC17 | `TestSpool_MemoryBound` (live heap after `runtime.GC` with a full spool, 64 ranges and 100,000 foreign entries); `BenchmarkOpen_Full` | `internal/spool/spool_test.go` |
| AC18 | `TestOpen_OtherAgentID` | `internal/spool/meta_test.go` |

## Approach

**Package `internal/spool`** (new), standard library only.

*Files in `spool.directory`.* Segments `segment-<20 decimal digits of the first sequence number>.spool`; one meta file `spool-meta.json`
and its temporary `spool-meta.json.tmp`. Nothing else is opened, counted or deleted (`agent-state.json` and every other name are
foreign, E7). Every file is created with `O_CREAT|O_EXCL|O_NOFOLLOW`, mode 0600; every open uses `O_NOFOLLOW|O_NONBLOCK` and is
trusted only after `fstat` shows a regular file of the effective UID without group or other bits (as `agentstate.readTrusted`), else
it is a refused entry (E8): never read, written, renamed or deleted, not counted. The directory is opened with
`O_DIRECTORY|O_NOFOLLOW` (only its last element, E2) and listed in chunks of 256 names, keeping only names of the two patterns.

*Segment format (version 1).* Header: magic `VXSPOOL1`, version (u16), agent ID (length-prefixed, at most 64 bytes), boot ID (36
bytes), clock offset flag and value (i64), CRC-32C of the header. Then frames: payload length (u32, at most `wire.MaxLineBytes`
plus 8), CRC-32C (Castagnoli) of the payload, payload = sequence number (u64) and the record's wire line (`wire.MarshalRecord`).
A segment holds one capture context (agent ID, boot ID, offset); a new context starts a new segment. A segment is sealed when it
reaches the segment size `min(64 MiB, spool.max_size / 32)` or its context ends: a trailer frame (flagged by length `0xFFFFFFFF`) with
the last sequence number, the record count, the earliest and latest capture time (gap records count with their `from` and `to`), and
its CRC; the sealed file is synced before the next segment is created, and the directory after the creation.

*Meta file.* JSON, decoded strictly (one object, `version` 1, no unknown key, at most 16 KiB): `agent_id`, `boot_id` of the last
open, `clean` (true only after a clean close), `next` (a floor for the next sequence number), `acked` (at most 64 ranges `[first,
last]` above the oldest unacknowledged record). Written as `agentstate.write` does: remove the left-over temporary, create it
exclusively, write, rename over the meta file; synced (file and directory) at open, at close and by the sync loop when dirty; an
acknowledgement rewrites it without syncing (a lost acknowledgement only resends).

*Open (`Open`).* Check the directory (E2; the write check is the meta write); list it; read each segment's header and, for a sealed
one, its trailer only; scan the newest segment and any segment without a valid trailer frame by frame, cutting it at the first frame
whose length, CRC or wire line fails (E6, truncating the file to the last good frame) and sealing it. Segments of another agent ID are
deleted with `spool records of another agent dropped` (count from trailers or the scan, E18). The next number is the larger of `next`,
the last good sequence number plus 1 and — on a fresh spool (no meta, or meta of another agent ID, or a damaged meta), and when the
meta is not `clean` and its `boot_id` differs from the current one — `ClockBound(now)` = microseconds since the epoch plus 1,000,000.
Then the meta is written with `clean` false and the current boot ID. A fresh spool logs `spool created`. Open builds no per-record
index: per segment it keeps first and last number, size, context, the trailer's times and a sparse index (number and offset every
1 MiB, at most 64 entries) filled lazily on the first read.

*Put.* Under the spool's mutex: number the records, encode each frame, check room (size bound: total size plus the frames plus room
for at most 65 gaps of 1 KiB; disk: `Available(dir)` minus that is at least `spool.min_free`, skipped when it is 0); while there is
no room, drop the oldest sealed segment (E10, S6, S7); when only the active segment is left and there is still no room, refuse (E11).
Dropping writes one `spool_dropped` gap (source `agent`, origin agent) per run of unacknowledged numbers of the dropped segment between
the acknowledged ranges, `from`/`to` the trailer's earliest and latest time (`to` at least one second after `from`), `captured_at` now;
the gaps are appended before the records, numbered first. A write error (through the seam `writeFile`) truncates the active segment
back to its last good frame, refuses the hand-over, opens or extends the episode (earliest and latest capture time, record count) and
logs `spool write failed` once; the next successful Put first appends the episode's `unknown` gap and logs `spool writes again`. A
refusal returns at once. Episode lines: `spool data dropped` (`reason`, `records`) at the first drop of an episode, `spool data no
longer dropped` (`records`) at the first Put that fits without dropping.

*Read and Ack.* `Read(after, maxRecords, maxBytes)` returns the unacknowledged records with numbers above `after` in order, from one
segment context only (it stops at a context change), at most the limits (at least one record when the first fits `maxBytes`), never
beyond the last completed frame. `Ack(first, last)` merges the range into `acked`; a range that would make a 65th disjoint range
returns `ErrTooManyRanges` and changes nothing; numbers outside the kept range are ignored. When the oldest unacknowledged number passes
a sealed segment, it is deleted; the active segment is never deleted. `OldestCapture` returns the capture time of the oldest
unacknowledged record (cached, re-read when the oldest moves). `LastSeq` returns the newest issued number (for #39's current stream).

*Durability.* A goroutine started by `Open` syncs the active segment and a dirty meta once a second (`synctest`-testable). `Sync`
does the same at once, honors its context (the fsync runs on and the call returns at the deadline). `Close` stops the loop, syncs,
seals nothing, writes the meta with `clean` true and syncs it.

**Single-line wire codec.** `wire.MarshalRecord` is `CheckRecord`'s validation and size check returning the line;
`wire.UnmarshalRecord` runs the decoder's line rules (`keyCheck`, text, depth) and `decodeRecord` on one line, so a spool line is held to
the same rules as a batch line (area 10).

**agentstate.** `Store.SetBarrier` registers a function that `saveLocked` calls first; when it fails, the save is not written and logs
`agent state not saved` with `err` `agentstate: save: spool not durable` (fixed text). `Flush` passes a context of 5 s.

**agent.** `Run`: load config; `openStorage` opens the state store first (so the first-start check sees an empty directory, E3), then —
unless `opts.Sink` is set (tests) — reads the boot ID from `opts.Kernel.Proc` or `/proc` and opens the spool in `opts.StateDir` or
`spool.directory`; an error ends with `agent cannot start` and exit 1 before `READY=1` (E2, E15: `spool: boot id: not readable` or
`spool: boot id: not a lower-case UUID`). The store's barrier is `sp.Sync`; `opts.Self.Spool` and `opts.Self.SpoolAge` take the spool
when unset. Stop: `wg.Wait()`, `store.Flush` (its barrier syncs the spool first), `closeSpool` (waits at most 5 s, else `spool not
closed`). Spool start errors: `spool: directory: not a directory`, `spool: directory: symbolic link`, `spool: directory: <errno text>`,
`spool: meta: <errno text>`; never a path.

**config.** `spool.max_size` and `spool.min_free` as strings, parsed by `parseSize` (one to five ASCII digits, then exactly `MiB` or
`GiB`; product checked against the bounds before conversion) into `Spool.MaxBytes`, `Spool.MinFreeBytes`; error texts as spec E1.

**selfstats.** `SpoolAgeReporter` and `MetricSpoolAge`: age = run start minus the oldest capture time, 0 when none or negative.

Layout: the new output is log lines and error texts whose wording the spec fixes; their attributes are key=value as every agent line.

## Options considered

1. Storage: an embedded store (bbolt, SQLite), compressed segments, one file per hand-over, or framed uncompressed segments (chosen) —
   reasons in record 0123.
2. A lost counter: refusing to start, a counter epoch in the wire header, or the clock bound (chosen) — record 0123.
3. Acknowledgements: prefix only, a bitmap, or at most 64 ranges (chosen) — record 0123.
4. Order of state and records: an fsync per hand-over, positions inside the spool, or a spool sync as barrier before every state save
   (chosen) — record 0123.
5. Segment metadata: scanning every segment at start (violates the 5 s start-up for 2 GiB), a separate index file (a second file to
   keep consistent), or a trailer per sealed segment (chosen).
6. Wiring order: opening the spool inside `jobs` (the state file would then see a non-empty directory on a first start, E3) or a new
   `openStorage` before `jobs` (chosen).

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Go agent | `internal/spool/spool.go` | new: `Spool`, `Options`, `Batch`, `Open`, Put, Read, Ack, Sync, Close, providers, eviction, episodes |
| Go agent | `internal/spool/segment.go` | new: segment header, frames, trailer, scan and cut |
| Go agent | `internal/spool/meta.go` | new: meta file, `ClockBound` |
| Go agent | `internal/spool/export_test.go` | new: the `writeFile` seam for tests |
| Go agent | `internal/wire/encode.go`, `internal/wire/decode.go` | `MarshalRecord`, `UnmarshalRecord` |
| Go agent | `internal/config/agent.go`, `internal/config/config.go` | `Spool` fields, defaults, keys, `parseSize`, validation |
| Go agent | `internal/selfstats/selfstats.go` | age provider and metric |
| Go agent | `internal/agentstate/agentstate.go` | barrier |
| Go agent | `internal/agent/agent.go` | `openStorage`, `jobs` and `newScheduler` signatures, stop order, `closeSpool`, `StateDir` doc |
| Go agent | `internal/scheduler/sink.go` | `DiscardSink` doc: test use only |
| Docs/deploy | `deploy/agent/agent.yaml`, `deploy/agent/README.md`, `README.md`, `.squad/project.md`, `docs/BENCHMARKS.md` | Dev (tasks 19–22) |

## Signatures (for the Dev's skeleton)

Package spool:

- `type Spool struct`
- `type Options struct` with the fields Dir, AgentID, BootID (strings), MaxBytes, MinFreeBytes (int64), SyncInterval (time.Duration; 0 means one second), Now (a function returning time.Time; nil means time.Now) and Available (a function of the directory returning the available bytes as uint64 and an error; nil means statfs f_bavail times f_bsize)
- `type Batch struct { AgentID string; BootID string; ClockOffsetNS *int64; Records []model.Record }`
- `const MaxAckRanges = 64`
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
set neither `Sink` nor a boot ID fixture now open a real spool in their `StateDir`: the kernel fixture's `/proc` holds the boot ID (fact
5), so they keep working.

Code a unit test cannot cover: none. The hard-kill helper runs inside the test binary.

## Areas

- Agent: the spool's rules in *Spool and sending*; *Status*, *Scheduling*, *Self-metrics*, *Agent state*, *Kernel events*, *Log
  shipping*, *Gaps*, *Rights*, *Implementation* (spec, *Area documents*).
- Configuration and secrets: two options.
- Wire format: the age metric, *Batch identity* (clock bound, first number, jumps).

## Documentation updates

Sweep done for enumerations the change extends: the option tables (`docs/areas/configuration-and-secrets.md:40-55`, `README.md:67`),
`AgentKeys` and the example `deploy/agent/agent.yaml`, the metric table (`docs/areas/wire-format.md:143-144`), "the agent's only file
write" (`docs/ARCHITECTURE.md:64`, `docs/areas/agent.md:1347,1602`, `.squad/project.md:141`), "until the spool exists"
(`docs/ARCHITECTURE.md:14`, `docs/areas/agent.md:11-13,130`, `internal/scheduler/sink.go:17`), "later a full spool"
(`docs/areas/agent.md:938,1217`, record 0120 — done), *Not implemented* lists (`docs/ARCHITECTURE.md:15`), the spool
directory's contents (`deploy/agent/README.md:167-173`), the Implementation list (`docs/areas/agent.md:1681-1695`, `.squad/stack.md`
*Layout* package list).

| File | Owner |
| ---- | ----- |
| `docs/areas/agent.md` | Lead (task 23) |
| `docs/areas/configuration-and-secrets.md` | Lead (task 24) |
| `docs/areas/wire-format.md` | Lead (task 25) |
| `docs/ARCHITECTURE.md` | Lead (task 26) |
| `README.md` (two option rows, the directory row) | Dev (task 19) |
| `deploy/agent/agent.yaml`, `deploy/agent/README.md` | Dev (task 20) |
| `.squad/project.md` (security area 9: the spool's files, guards and functions; `.squad/stack.md` *Layout*: `spool`) | Dev (task 21) |
| `docs/BENCHMARKS.md` (*Spool open*) | Dev (task 22) |
| record 0120 ("full spool") and record 0123 | Lead (done in this step) |

## Architecture check

- *No data gaps unless explicitly recorded* (records 0028, 0045): every drop is a `spool_dropped` or `unknown` gap; a lost pending gap
  at a stop during a write failure and a lost counter after a clock set back are stated residuals in the spec. Strengthened, not
  weakened.
- *A hanging collector never blocks the agent* (0029): unchanged; a stalled disk is out of scope (the watchdog restarts the agent).
- *Agent never makes the memory situation worse* (0025): spool live memory below 4 MiB (AC17).
- The state file stops being the agent's only write; `docs/ARCHITECTURE.md` and `.squad/project.md` area 9 are updated (tasks 21, 26).
  Record 0045 (released) is amended by 0123, not edited.

## Security considerations

- [ ] Limits on parsed input, all on raw bytes before decoding: a frame payload at most `wire.MaxLineBytes` + 8 (a larger length is
  damage, never an allocation); a header at most 128 bytes; the meta file at most 16 KiB; directory listing in chunks of 256; the
  sparse index at most 64 entries per segment; `wire.UnmarshalRecord` applies the decoder's line rules to the raw line.
- [ ] Texts that reach a user: the start errors above, `spool: write: <errno text>`, `spool data damaged` with `err` one of
  `checksum mismatch`, `file cut short`, `not the spool format`, `unknown format version`, `refused entry`; meta damage logs `file`
  `spool-meta.json`. No path, record content or system error string other than the errno text.

## Decision records

- [`docs/decisions/0123-agent-spool-segment-files-clock-bound-sequence-numbers-range-acknowledgements.md`](../../docs/decisions/0123-agent-spool-segment-files-clock-bound-sequence-numbers-range-acknowledgements.md) (Proposed; amends 0045)

## Out of scope / follow-ups

As in the spec. Follow-ups for the orchestrator to open: none new (#39, #41, #42, #44 already hold what the spec assigns them; the
orchestrator adds a comment to #41 about the first sequence number and the jumps, and to #42 about `Restart=` and the start limits).
