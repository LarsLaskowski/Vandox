# Plan: Kernel events, reboots and backup runs

Source: Issue #36 | [spec.md](spec.md)
Status: Revised (2)
Tier: security — the change creates guards against an attacker (only kernel-transport entries with exactly one facility `0` count as kills, a bounded parser of a binary format read from disk, a state file refused unless it is the agent's own) and the agent's first file write.
Security areas touched: 10 Parsing of external input (`internal/journal`, the kill-line parser in `internal/kernevents`, `internal/agentstate` reading its file, `/proc/uptime` and `boot_id`, the option `backups.processes`, the new wire fields and kind in both decoders); 9 File writes (`internal/agentstate`: the state file in `spool.directory`); 12 Logging and display of external data (victim names and backup commands as record text, never logged; the committed fixture); 6 Agent privileges (the group `systemd-journal` gets its first user, no new right; `deploy/agent/README.md`)

## Problem / root cause

The feature is specified in [spec.md](spec.md) (S1 to S13, E1 to E17, AC1 to AC20).

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | `kernel_event` with `oom_kill` and `boot` exists in Go and C#; nothing produces it | `internal/model/kernel.go`, `src/Vandox.Core/Model/KernelEvent.cs`; no producer under `internal/` | confirmed |
| 2 | Wire 1.1 is unreleased; optional fields and a new kind extend it in place | `git ls-remote --tags origin`: only `v0.1.0`; wire format area *Versioning* | confirmed |
| 3 | Storage keeps kinds other than metric and log line as JSON; a new kind needs `RecordQueries._kinds`, no migration | `docs/areas/storage.md` row `records`; `src/Vandox.Storage/RecordQueries.cs:28-37`; no `CHECK` on `kind` | confirmed |
| 4 | The scheduler cannot start an extra run; an abandoned run blocks new starts; a dropped result is invisible to the collector | `internal/scheduler/runner.go:172-256` (`cycle`, `pending`, `finish`) | confirmed |
| 5 | `processes` and `connections` take a full snapshot on `run.Pressure` | `internal/procstats/procstats.go:125`, `internal/connstats/connstats.go:125` | confirmed |
| 6 | Server journal: systemd `249.11-0ubuntu3.23`, kernel `5.15.0-191-generic`, header 256 bytes, flags `KEYED-HASH`+`COMPRESSED-ZSTD`, not `COMPACT` | `journalctl --file=<real> --header` and the boot entries | confirmed |
| 7 | Non-compact object layout (object header 16 bytes, data payload at 64, entry items of 16 bytes, 8-byte alignment) | systemd `JOURNAL_FILE_FORMAT.md`; read-only scanner `scratchpad/tools/jscan.py` counts exactly the objects `journalctl --header` reports | confirmed |
| 8 | No kernel entry references a compressed object; kill lines are 159 to 171 bytes | the scanner on both files: 3,563 kernel entries, 0 with a compressed item | confirmed |
| 9 | Kernel-transport entries with facility 3 or 5 exist (userspace writes to `/dev/kmsg`) | `journalctl -o export _TRANSPORT=kernel`: 3,342 facility 0, 220 facility 3, 1 facility 5 | confirmed |
| 10 | The kernel never gives facility 0 to a write to `/dev/kmsg` | Security read `devkmsg_write` in `kernel/printk/printk.c` v5.15 (facility forced to 1; same in v6.8) | confirmed |
| 11 | The 5.15 kill line format ends in `UID:<n> pgtables:<n>kB oom_score_adj:<n>` | the 8 real kill lines | confirmed |
| 12 | The cgroup and allocating-task reasons use the same format | to verify: Tester reads `__oom_kill_process` and callers in `mm/oom_kill.c`, `mm/memcontrol.c` v5.15 (step 5) | unverified |
| 13 | The keyed hash is SipHash-2-4 of the payload keyed with the file ID | to verify: Dev, step 6, `TestFile_RealServerJournals` finds `_TRANSPORT=kernel` through the hash table of both real files | unverified |
| 14 | journald's "Missed N kernel messages" entry has `MESSAGE_ID=e9bf28e6e834481bb6f48f548ad13606` on the driver transport | to verify: Dev reads `journald-kmsg.c` and `sd-messages.h` at systemd v249 (step 6); not in the real files | unverified |
| 15 | journald compresses only fields from 512 bytes on | to verify: Dev reads `COMPRESSION_SIZE_THRESHOLD_DEFAULT` at systemd v249 (step 6) | unverified |
| 16 | No reboot on 2 Oct 2026; `pzstd` and `mariadbd` were killed; next reboot 21 Oct 16:28:27 UTC | `journalctl --file=... --list-boots` and the kill entries; the issue's account is refuted (spec *Problem*) | refuted |
| 17 | `journalctl` 255 here verifies and exports the non-compact layout | rows 6 and 9 read with it; `--verify` passes the prototype `scratchpad/fixture/oom.journal` | confirmed |
| 18 | The server's journal files are readable by `systemd-journal` | to verify: #42 checks `ls -ln /var/log/journal/<machine-id>` on the server | unverified |
| 19 | The kernel's printk clock drifts against `CLOCK_MONOTONIC` and real time | Devil's Advocate M1 against `audit(...)` stamps; spot check here: the 2 Oct `mariadbd` kill has receipt monotonic minus source stamp = 4.30 s at uptime 128,205 s (about 33.5 µs/s) | confirmed |
| 20 | `/proc/uptime` equals the journal's `__MONOTONIC_TIMESTAMP` clock on a server that never suspends | to verify: Tester compares `/proc/uptime` with the `__MONOTONIC_TIMESTAMP` of a fresh `logger` entry (scratch measurement, step 5), and records the facility a `<0>` write to `/dev/kmsg` gets | unverified |
| 21 | Entry arrays double in size (a chain has at most 64 arrays); new data objects are linked at their hash chain's end (offsets strictly increase) | systemd `journal-file.c` (`scratchpad/src/`) lines 1837-1843 and 1254-1265; the file is append-only | confirmed |
| 22 | An unclean close renames the file to `*.journal~`, and the next file gets a new seqnum ID | systemd v249 `journal-file.c` 589-593 and 3836-3861, `journald-server.c` 281-283, `journal-file.c` 443-446 (Devil's Advocate M2) | confirmed |
| 23 | Seqnums are not dense | archived file: 73,265 entries over seqnums 1 to 30,218,745; online file: 72,909 over 30,674,914 to 30,747,972 | confirmed |

## Acceptance criteria

AC1 to AC20 of [spec.md](spec.md).

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `TestKernel_LiveKillBecomesEventWithinFiveSeconds`, `TestKernel_CaptureTimeCorrectsKernelClockDrift`, `TestKernel_KillIdentityStableAcrossReadings`; `TestParseKill_RealLines` | `internal/kernevents/kernevents_test.go`; `internal/kernevents/kill_test.go` |
| AC2 | `TestScheduler_TriggerRunsExtraWithinOneSecond`, `TestScheduler_TriggerWaitsForCurrentRun`, `TestScheduler_TriggerKeepsGridWithoutPressure`, `TestScheduler_TriggerReplacesNextRunUnderPressure`; `TestProcesses_TriggeredRunIsFullAndNamesKills`; `TestConnections_TriggeredRunIsFullAndNamesKills`; `TestKernel_LiveKillTriggersBothCollectors` | `internal/scheduler/trigger_test.go`; `internal/procstats/procstats_test.go`; `internal/connstats/connstats_test.go`; `internal/kernevents/kernevents_test.go` |
| AC3 | `TestKernel_StormCoalescesWithinFifteenSeconds`; `TestScheduler_TriggerMergesKillsUpToSixtyFour` | `internal/kernevents/kernevents_test.go`; `internal/scheduler/trigger_test.go` |
| AC4 | `TestParseKill_IgnoresOtherLines`; `TestKernel_OnlyKernelTransportWithFacilityZero` | `internal/kernevents/kill_test.go`; `internal/kernevents/kernevents_test.go` |
| AC5 | `TestParseKill_HostileNames`, `TestParseKill_MissingFields`, `TestParseKill_AllKillForms` | `internal/kernevents/kill_test.go` |
| AC6 | `TestKernel_ResumesAtSavedPositionAcrossBoots`, `TestKernel_ReadsSetAsideFilesAfterUncleanShutdown`, `TestKernel_JournaldRestartStartsNewSeqnumID`, `TestKernel_LostRunRereadsEntries`, `TestKernel_LateOrOtherBootKillsGetNoSnapshot`, `TestKernel_ReversedTimeGapStillMovesPosition` (a span whose missed-messages entry lies between two kernel entries of the same µs and one whose clock steps back: each gap ends 1 µs after its start, every returned record passes `wire.CheckRecord`, the next run's `PreviousLost` is false and the position moves past the span, the span is not returned again), `TestKernel_RefusedRecordBecomesGap` (a record forced to fail the check through an unexported hook: left out, a gap in its place, the position moves on), `TestKernel_RereadCappedAtThree` (four dropped runs in a row: three re-reads, then one gap over the span and reading after it; a dropped run that read nothing does not count; a dropped give-up run gives up the same span again without its kills) | `internal/kernevents/kernevents_test.go` |
| AC7 | `TestKernel_LostPositionRecordsGapAndContinues`, `TestKernel_UndecodableEntryRecordsGap`, `TestKernel_MonotonicStampOutOfRangeIsUnreadable` (`_SOURCE_MONOTONIC_TIMESTAMP` empty, 21 digits, `18446744073709551616`, `9223372036854776` µs, with a sign, a space or a line feed; `__MONOTONIC_TIMESTAMP` above `math.MaxInt64 / 1000`: each a gap, no event, never a negative `kernel_time_ns`), `TestKernel_HangingJournalLosesOnlyItsSample`, `TestKernel_PollWorkIndependentOfEarlierEntries`, `TestKernel_TimeBudgetEachRunGetsFurther`; `TestFile_RefusesUnsupportedLayout`, `TestFile_BoundsEveryOffset`, `TestFile_TornTailIsEndOfFile` | `internal/kernevents/kernevents_test.go`; `internal/journal/journal_test.go` |
| AC8 | `TestKernel_UnreadableJournalLogsOnceAndResumes` | `internal/kernevents/kernevents_test.go` |
| AC9 | `TestBoot_NewBootRecordsPreviousBootAndUptime` | `internal/kernevents/boot_test.go` |
| AC10 | `TestBoot_SameBootRecordsNothing`, `TestBoot_FirstStartWithoutPrevious`, `TestBoot_WithoutStateEveryStart`; `TestOpen_StatusAndWarnings` | `internal/kernevents/boot_test.go`; `internal/agentstate/agentstate_test.go` |
| AC11 | `TestBoot_BadBootIDRecordsNothing` | `internal/kernevents/boot_test.go` |
| AC12 | `TestBackups_StartWithinFifteenSeconds`, `TestBackups_EndAfterSixtySeconds`, `TestBackups_ShortPauseKeepsOneRun`, `TestBackups_SingleSightingGivesStartAndEnd`, `TestBackups_CountsWithoutKeepingProcesses` | `internal/backups/backups_test.go` |
| AC13 | `TestBackups_RestartContinuesRun`, `TestBackups_RunEndedAcrossStopOrReboot` | `internal/backups/backups_test.go` |
| AC14 | `TestLoadAgent_BackupProcesses`; `TestBackups_RulesAreLiteral`; `TestJobs_BackupsOnlyWithRules` | `internal/config/agent_test.go`; `internal/backups/backups_test.go`; `internal/agent/agent_test.go` |
| AC15 | `TestBackupRun_Validate`; `TestSnapshotTrigger_Validate`; `TestKernelEvent_Validate_KillIdentity`; `TestEncodeBatchGolden`; `TestDecoder_FieldErrors` (new cases); C#: `BackupRunValidateRules`, `SnapshotTriggerValidateRules`, `KillRefValidateRules`, `PayloadValidationOomKillIdentity`, `WireContractDecodesGoldenBatch`, `QueryRecordsAcceptsBackupRun` | `internal/model/backup_test.go`, `trigger_test.go`, `kernel_test.go`; `internal/wire/golden_test.go`, `decode_test.go`; `tests/Vandox.Core.Tests/BackupRunTests.cs`, `SnapshotTriggerTests.cs`, `KillRefTests.cs`, `PayloadValidationTests.cs`, `WireContractTests.cs`; `tests/Vandox.Storage.Tests/SqliteStoreQueryTests.cs` |
| AC16 | `TestFile_MemoryDoesNotGrowWithJournalSize`; `TestKernel_BacklogMemoryIsBounded` | `internal/journal/journal_test.go`; `internal/kernevents/kernevents_test.go` |
| AC17 | `TestSave_AtomicAndOwnerOnly`, `TestSaver_WriteRates`, `TestOpen_CorruptOrForeignFileIsMissing` | `internal/agentstate/agentstate_test.go` |
| AC18 | `TestKernel_LogsHoldNoMessageText`; `TestBackups_LogsHoldNoProcessText` | `internal/kernevents/kernevents_test.go`; `internal/backups/backups_test.go` |
| AC19 | `TestScheduler_AbandonedExtraRunRecordsGapAndSkips`, `TestScheduler_FailedExtraRunRecordsGap`, `TestScheduler_TriggerWhileStuckRecordsGap` | `internal/scheduler/trigger_test.go` |
| AC20 | `TestKernel_MissedKernelMessagesRecordGap` | `internal/kernevents/kernevents_test.go` |

Also required, not tied to a criterion: `TestWrite_JournalctlAcceptsFixture` (skips without `journalctl`), `TestFile_RealServerJournals`
and `TestFixture_HoldsNoRealPrivateValue` (both skip unless `VANDOX_REAL_JOURNAL_DIR` is set).

## Approach

**Jobs** (`internal/agent`, after `clock`): `kernel` (interval 2 s, timeout 2 s), `backups` (10 s, 5 s; only with at least one rule),
`state` (10 s, 5 s; returns no records).

**Journal reader** (`internal/journal`). Reads through `io.ReaderAt` into fixed buffers, never `mmap`. Directory
`/var/log/journal/<machine-id>`, else `/run/log/journal/<machine-id>` (machine ID: 32 lower-case hex digits of `/etc/machine-id`, 64 bytes
read). Files: `system.journal`, `system@*.journal` and `system@*.journal~` (set-aside files, read as archived), at most 1,024, listed in
chunks; per file only its header is kept. Layout: signature `LPKSHHRH`, header size 256 to 4,096, `KEYED-HASH` set, no flag outside
`XZ`, `LZ4`, `KEYED-HASH`, `ZSTD`, else `journal: unsupported layout`. Bounds (raw bytes, before any decoding): every offset 8-aligned and
inside `[header_size, min(header_size + arena_size, file size))`; every object at least its fixed part and ending in that range; hash
chains and entry-array chains followed only to strictly larger offsets, entry-array chains at most 64 arrays (fact 21); at most 512
items per entry; a payload read up to 4,096 bytes. A violation is `journal: malformed file`; in an online file it is retried at the next
poll and counts only after three polls on the same entry; in an archived or set-aside file, a torn tail ends the file (entries after it
are not read; a gap to the header's tail time when that is later). Lookup: the data hash table (SipHash-2-4, key the file ID) finds
`_TRANSPORT=kernel`, `SYSLOG_FACILITY=0` and `MESSAGE_ID=e9bf28e6e834481bb6f48f548ad13606`; their entry lists are the input. Position in a
list: bisected by seqnum through the doubling arrays (read the last item of each array, then binary search inside one), and kept as an
in-memory cursor per file between polls, so the work of a poll is a constant number of reads plus the new entries. Per entry only these
items are read in full: the one at the offset of `SYSLOG_FACILITY=0`, items named `SYSLOG_FACILITY`, `MESSAGE`,
`_SOURCE_MONOTONIC_TIMESTAMP`, `_TRANSPORT`; of every other item only the first 64 bytes of its payload, to see its name. Compressed items
are never decompressed. Seqnum differences are never used for counts or gaps (fact 23).

**Kernel collector** (`internal/kernevents`, name `kernel`). First run: the boot check (below). Each run: commit the position kept in
memory by the previous run when `run.PreviousLost` is false, else drop it and read again from the committed position (same identities;
kills already triggered are remembered by identity, the last 64, and not triggered again). Because the collector returns only records
that pass the scheduler's check (*Record check* below), `run.PreviousLost` means a dropped result (timeout, skip, error, sink error),
never a refused record. *Re-read cap:* a counter of re-reads of the same committed position in a row (reset when a run is kept or the
committed position moves); a dropped run that read no entry (its in-memory position equals the committed one) does not count. At the
fourth dropped run in a row, the next run reads nothing of the span from the committed position to the dropped run's in-memory
position: it returns a gap over it (cause `unknown`, from the committed position's time to the time of the last entry read, bounds as
in *Gaps*) and reads on after the span; the committed position moves past it with the usual commit at the following run, so while this
run is dropped too, the next one gives up the same span again (its gap again, never its kills). Then read from the committed position over the
files of its seqnum ID in head-seqnum order, then the files of other seqnum IDs whose head time is not before the position's, in
head-time order, from their heads. A saved seqnum ID without any file gives a gap from the position's time to the first entry after it,
and reading goes on there; no saved position starts at the newest file's tail. A run stops after 1 s of reading (half the timeout,
checked between entries) or 256 kills, whichever first, and returns what it read. An entry counts only with `_TRANSPORT=kernel` and
exactly one `SYSLOG_FACILITY` item whose value is exactly `0`; its `MESSAGE` goes to `parseKill`. Abandoned runs never touch the store:
only the start of the next run commits.

*Record check.* Before returning, `checkRecords` copies each record as the scheduler's `stamp` does (`Origin` agent, `Seq`
`math.MaxInt64`, `CapturedAt` set) and calls `wire.CheckRecord`; a record it refuses is left out and replaced by a gap over the time of
its entry (from the previous kernel entry's time, or the committed position's, to that entry's time, bounds as in *Gaps*). No log line (the gap records the loss;
spec *Logs* adds none). The gaps themselves pass the same check by construction.

*Timestamps.* `__MONOTONIC_TIMESTAMP` is read from the entry object as an unsigned 64-bit number of µs, `_SOURCE_MONOTONIC_TIMESTAMP`
from its item as 1 to 20 ASCII digits `0`-`9`, nothing else (no sign, space or line feed), parsed into a `uint64` with an overflow check;
each value is turned into ns only when it is at most `math.MaxInt64 / 1000` µs. Any other value makes the entry unreadable (a gap as for
an undecodable entry), never a negative or wrapped duration.

*Time.* `lag = __MONOTONIC_TIMESTAMP − _SOURCE_MONOTONIC_TIMESTAMP` of the entry; `drift` = the smallest `lag` among the last 64 kernel
entries of the same boot read in order (the kernel clock's offset, which grows slowly); `delay = max(0, lag − drift)`. Capture time =
`__REALTIME_TIMESTAMP − delay` (never after the receipt). Live: entry boot ID equals `/proc/sys/kernel/random/boot_id` and
`uptime − __MONOTONIC_TIMESTAMP + delay ≤ 60 s` (`uptime` from `/proc/uptime`, fact 20). Identity: entry boot ID, the raw
`_SOURCE_MONOTONIC_TIMESTAMP` in ns (else `__MONOTONIC_TIMESTAMP`), victim PID.

*Triggers.* A live kill calls `Trigger` for `processes` and `connections` at once when no trigger was sent in the last 15 s; otherwise
it is kept (at most 64, then counted) and sent at the first run 15 s after the last trigger.

*Gaps* (source and collector `kernel`, cause `unknown`): a lost saved seqnum ID (above); an unreadable entry or unsupported file (from
the last readable kernel entry's time to the next one's, or to the run's start); a torn tail (above); a missed-messages entry with
`_TRANSPORT=driver` (from the last kernel entry before it to the next one after it); a refused record (*Record check*); a given-up span
(*Re-read cap*). Every gap is valid by construction, `newGap`: when `To` is not after `From` (two entries in the same µs, or a clock set
back between them, spec E10), `To = From + 1 µs`; `From` and `To` are UTC. Logs: warning `kernel messages not readable` (`err`:
`journal: <online|archived>: <fixed reason>`), info `kernel messages readable again`, warning `boot ID not readable`.

**Kill parser** (`parseKill`). Prefix exactly one of `Out of memory: Killed process `, `Memory cgroup out of memory: Killed process `,
`Out of memory (oom_kill_allocating_task): Killed process `; a PID of 1 to 10 digits within 32 bits, above 0; ` (`; the comm up to the
**last** `) total-vm:`; the tail split at `, ` and ` ` into tokens, of which only `anon-rss:<1-20 digits>kB` (times 1,024, overflow
checked) and `oom_score_adj:<-?1-4 digits>` (−1000 to 1000), each at most once, are used. Command: `procstats.CleanText(comm, 1024)`,
`(unnamed)` when empty; `message`: the message cleaned the same way at 16,384 bytes.

**Boots** (first `kernel` run). `boot_id` up to 64 bytes, a lower-case UUID and one line feed; `booted_at` from `procstats.ReadBootTime`.
State boot equal: nothing; different: `boot` event with `previous_boot_id` and `previous_uptime_ns`; none: event without them. Then the
state holds the current boot and uptime.

**State** (`internal/agentstate`). `agent-state.json` in `spool.directory`, which the agent never creates (#42 does). Opened with
`O_RDONLY|O_NOFOLLOW|O_NONBLOCK`; the checks run on `fstat` of that descriptor: a regular file of the agent's effective UID without group
or other permission bits, at most 16 KiB; strict JSON (`version` 1, unknown keys, wrong types, invalid UUIDs or times refused). Status:
absent file in an existing directory with no other entry = first start (no warning); anything else not usable = `agent state not
available` (once). Save: remove a left-over `agent-state.json.tmp`, create it with `O_CREAT|O_EXCL|O_NOFOLLOW` mode 0600, write, `fsync`,
rename, `fsync` the directory; a failure (also a missing directory) logs `agent state not saved` (`err`: the OS error text, no path) once
until a save succeeds. The `state` job saves when a kill, boot or backup change waits and 10 s passed since the last save, else after
50 s (the uptime heartbeat; a position-only change waits for it). The spool (#38) must count and delete only its own files.

**Scheduler** (`internal/scheduler/trigger.go`). `Trigger` merges kills into the runner's pending trigger (at most 64, the rest counted),
wakes it and returns false only for an unknown collector. A waiting runner handles it at once, a running one after its run. With an
abandoned run out: no run, a `collector_timeout` gap from now over one interval of the mode. Otherwise `Run{…, Trigger}` with the outcome
handling of a cycle (gap over `[start, start + interval)`: `unknown` for an error or panic, `collector_timeout` when abandoned, then the
usual skips). Without pressure the grid stays; with pressure and a pressure interval the next due time is `start + pressure interval`.

**Snapshots.** `run.Trigger != nil` lists every process (`processes`) and makes the snapshot full (`connections`); both copy it into
`Trigger` of the record.

**Backup check** (`internal/backups`). Per run: `procstats.ListPIDs`, then `CommandReader.Process` per PID; a rule matches when `command`
equals the process's command or the cleaned command line starts with `cmdline_prefix`. Start: the earliest `started_at` of the first run
with a match, that process's PID and command, the count. Each run with a match sets `last_seen` and `max_processes`; the first without
sets `gone_by`; 60 s after `last_seen` without a match the end is written. The open run lives in the state; at the first run after a start
a stored run of another boot, or one without a match now, is ended (`last_seen` from the state, `gone_by` now), else it continues.

**Records (wire 1.1).** `kernel_event.oom_kill`: optional `boot_id` (UUID) and `kernel_time_ns` (≥ 0), set by the agent.
`process_snapshot`, `connection_snapshot`: optional `trigger` `{"kills":[{"boot_id","kernel_time_ns","victim_pid"}],"kills_omitted":n}`
(1 to 64 distinct kills). Kind `backup_run`, source `backups`: `phase` `start` (`boot_id`, `started_at`, `pid` > 0, `command` short text,
`processes` ≥ 1; `captured_at` = `started_at`) or `end` (`boot_id`, `started_at`, `last_seen_at` ≥ `started_at`, `gone_by` ≥
`last_seen_at`, `max_processes` ≥ 1; `captured_at` = `last_seen_at`), no field of the other phase. Field errors use the existing reasons
plus `not allowed for phase start|end` and `must not be before started_at`.

**Configuration.** `backups.processes`, default `[{command: pzstd}]`, errors as `processes.programs` with the path `backups.processes[<i>]`;
`AgentKeys` appends it.

**Memory bound (B3).** Live memory of the reader and the collector: fixed buffers (one 4,096-byte payload buffer per needed field, item
and array buffers of 8 KiB), at most 1,024 file headers (about 200 KiB), the drift window and trigger lists (64 entries each), and at most
256 kill records per run (each at most about 13 KiB: the message at most 12,288 bytes, since the 4,096-byte raw payload grows at most
threefold by U+FFFD replacement, so the 16,384-byte message cap is never reached, plus the command at most 1,024 bytes; about 3.3 MiB for
256) — **at most 4 MiB in total**, independent of the
journal, the backlog and the entry sizes. `TestFile_MemoryDoesNotGrowWithJournalSize` and `TestKernel_BacklogMemoryIsBounded` assert the
heap in use after a run (`runtime.ReadMemStats` after `runtime.GC`) grows by at most 4 MiB at journals of 1,000 and 100,000 kernel entries.

**Fixtures.** `testdata/journal/kernel.export` (journal export text, the source of truth) holds **real `MESSAGE` lines of exactly these
kinds**: the kill lines of 30 Sep 17:46:23 (`/usr/sbin/amavi`), 2 Oct 04:51:36 (`pzstd`) and 04:59:32 (`mariadbd`), their `invoked
oom-killer:` lines and their `oom_reaper: reaped process` lines, with their real `__REALTIME_TIMESTAMP`, `__MONOTONIC_TIMESTAMP` and
`_SOURCE_MONOTONIC_TIMESTAMP`. Every other entry is hand-made with `VANDOX_FIXTURE=handmade` and synthetic text: a facility-3 and a
facility-1 kernel entry (the latter a full kill line, as `printk.c` makes of `<3>Out of memory: Killed process …`), a `logger -t kernel`
kill line on the syslog transport, a kernel entry with two `SYSLOG_FACILITY` items, a non-kernel entry of 600 bytes stored compressed, a
kernel entry whose `MESSAGE` is stored compressed, a missed-messages entry (fact 14), a kill after 20 days of uptime with 40 s of drift,
entries of a second boot and of a set-aside file with another seqnum ID. Kept fields: `__REALTIME_TIMESTAMP`, `__MONOTONIC_TIMESTAMP`,
`__SEQNUM` (with holes, fact 23), `_BOOT_ID`, `_TRANSPORT`, `SYSLOG_FACILITY`, `SYSLOG_IDENTIFIER`, `PRIORITY`,
`_SOURCE_MONOTONIC_TIMESTAMP`, `MESSAGE`, `MESSAGE_ID`, `_HOSTNAME` (`web-1`), `_MACHINE_ID` (made up); boot IDs are made up; every other
field is dropped. `internal/journal/journaltest` (a non-test package used only by tests) writes journal files in the server's layout
(256-byte header, `KEYED-HASH|COMPRESSED-ZSTD`, non-compact, hash tables, doubling entry arrays; compressed items as zstd frames of raw
blocks). `VANDOX_UPDATE_GOLDEN=1 go test ./internal/journal/journaltest` writes `testdata/journal/system.journal` (online),
`system@00000000000000000000000000000001-0000000000000001-0005f8d1b1a2c3d4.journal` (archived) and
`system@00000000000000000000000000000002-0000000000000001-0005f8d1b1a2c3d5.journal~` (set aside); `TestWrite_JournalctlAcceptsFixture`
compares `journalctl --verify` and `-o export` of them with the export. `TestFixture_HoldsNoRealPrivateValue`, run by the Tester with the
real files before the fixtures are committed, decodes every field of the export and the three files and fails on any value, or substring
of 6 bytes or more, of: `_HOSTNAME`, `_MACHINE_ID`, `_BOOT_ID`, the address in the host name and its parts, the provider domain, every
value of `_SYSTEMD_UNIT`, `_SYSTEMD_USER_UNIT`, `SYSLOG_IDENTIFIER`, `_COMM`, `_EXE` and `_CMDLINE`, and every mail address, domain
name and user name (`user=<x>`, `for <x>`, `<x>@`) found in any `MESSAGE` of the real files; the only exceptions are these public, generic values, each skipped
only where a fixture field equals it exactly (a fixture value that merely contains one is still checked): the comms of the kept lines
(`pzstd`, `mariadbd`, `/usr/sbin/amavi`, `systemd`, `sshd`, `apport`) and the identifiers `kernel` and `systemd-journald`
(`SYSLOG_IDENTIFIER` of the kernel entries and of the missed-messages entry).

**Real files** (never committed or copied into the working tree): kept outside the repository with mode 0700;
`VANDOX_REAL_JOURNAL_DIR=<dir> go test ./internal/journal/... -run Real` (here
`/tmp/claude-0/-home-user-Vandox/58d2c1d7-bdb4-5d0c-8a35-e08f00638a4a/scratchpad/journalzip`). `TestFile_RealServerJournals` asserts counts
only (kernel entries 1,456 and 2,107, 8 kills, no compressed kernel item) and prints no field value. Dev (step 6) and Tester (coverage
step) run it and report the result.

The record fields and log texts above are fixed; no layout is left to the Tester.

## Options considered

Weighed in [0119](../../docs/decisions/0119-kernel-messages-from-the-binary-journal-files-read-in-pure-go-without-decompression.md) (source,
compression, lookup, set-aside files, kill time, fixture) and
[0120](../../docs/decisions/0120-oom-kill-events-triggered-snapshots-boots-from-saved-state-and-backup-runs.md) (extra runs, link, backup
record, state, position commit, default). Also: the kernel collector polls through the scheduler (chosen: deadline, gaps, never two runs;
an abandoned poll records a scheduler gap although its entries are read again later) rather than a free-running follower goroutine (its
own deadline and stall detection to build).

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Go | `internal/model/kernel.go`, `process.go`, `connection.go`, `trigger.go` (new), `backup.go` (new), `model.go`; `internal/wire/decode.go` | identity, `trigger`, `backup_run` and the kind table |
| Go | `internal/scheduler/trigger.go` (new), `scheduler.go`, `runner.go` | `Trigger`, `Run.Trigger`, the extra run |
| Go | `internal/procstats/procstats.go`, `command.go`, `boottime.go`; `internal/connstats/connstats.go` | triggered full snapshots; `ProcessInfo`, `CommandReader.Process`, `ReadBootTime` |
| Go | `internal/journal/` and `internal/journal/journaltest/` (new) | reader; fixture writer |
| Go | `internal/kernevents/`, `internal/agentstate/`, `internal/backups/` (new) | collectors `kernel`, `backups`; state and the `state` job |
| Go | `internal/config/agent.go`, `internal/agent/agent.go` | option; jobs and wiring |
| C# | `src/Vandox.Core/Model/OomKill.cs`, `ProcessSnapshot.cs`, `ConnectionSnapshot.cs`, `KillRef.cs`, `SnapshotTrigger.cs`, `BackupRun.cs` (new), `RecordKind.cs`, `PayloadRegistry.cs`; `src/Vandox.Storage/RecordQueries.cs` | the same fields, kind and rules |
| Data | `testdata/journal/` (new), `testdata/wire/all-kinds.jsonl`, `replaced-text.jsonl` | fixtures, regenerated golden batches |

## Signatures (for the Dev's skeleton)

```go
// internal/model
type KillRef struct {
	BootID     string        `json:"boot_id"`
	KernelTime time.Duration `json:"kernel_time_ns"`
	VictimPID  int32         `json:"victim_pid"`
}
type SnapshotTrigger struct {
	Kills        []KillRef `json:"kills"`
	KillsOmitted uint32    `json:"kills_omitted,omitempty"`
}
const MaxTriggerKills = 64
func (t *SnapshotTrigger) validate(path string) error
// OOMKill gains BootID string `json:"boot_id,omitempty"` and KernelTime *time.Duration `json:"kernel_time_ns,omitempty"`;
// ProcessSnapshot and ConnectionSnapshot gain Trigger *SnapshotTrigger `json:"trigger,omitempty"`.
const KindBackupRun Kind = "backup_run"
type BackupPhase string
const (
	BackupPhaseStart BackupPhase = "start"
	BackupPhaseEnd   BackupPhase = "end"
)
type BackupRun struct {
	Phase        BackupPhase `json:"phase"`
	BootID       string      `json:"boot_id"`
	StartedAt    time.Time   `json:"started_at"`
	PID          int32       `json:"pid,omitempty"`
	Command      string      `json:"command,omitempty"`
	Processes    uint32      `json:"processes,omitempty"`
	LastSeenAt   time.Time   `json:"last_seen_at,omitzero"`
	GoneBy       time.Time   `json:"gone_by,omitzero"`
	MaxProcesses uint32      `json:"max_processes,omitempty"`
}
func (b *BackupRun) Kind() Kind
func (b *BackupRun) Validate() error

// internal/scheduler
type Trigger struct {
	Kills        []model.KillRef
	KillsOmitted uint32
}
// Run gains Trigger *Trigger (set only for an extra run requested through Scheduler.Trigger).
func (s *Scheduler) Trigger(collector string, t Trigger) bool

// internal/procstats
type ProcessInfo struct {
	PID       int32
	Command   string
	Cmdline   string
	StartedAt time.Time
}
func (c *CommandReader) Process(pid int32, boot time.Time) (ProcessInfo, error)
func ReadBootTime(proc fs.FS) (time.Time, error)

// internal/journal
var (
	ErrUnsupported = errors.New("journal: unsupported layout")
	ErrMalformed   = errors.New("journal: malformed file")
)
type Header struct {
	FileID, MachineID, SeqnumID [16]byte
	Online, SetAside             bool
	HeadSeqnum, TailSeqnum       uint64
	HeadRealtime, TailRealtime   time.Time
}
type File struct{ /* unexported */ }
func Open(r io.ReaderAt, size int64, setAside bool) (*File, error)
func (f *File) Header() Header
func (f *File) Refresh(size int64) error
type Data struct{ /* offset, entry count */ }
func (f *File) Lookup(payload []byte) (Data, bool, error)
type Cursor struct{ /* array offset, index, seqnum */ }
func (f *File) Seek(d Data, after uint64) (Cursor, error)
func (f *File) Next(c *Cursor, want []string, e *Entry) (bool, error)
type Entry struct {
	Seqnum    uint64
	Realtime  time.Time
	Monotonic time.Duration
	BootID    [16]byte
	Items     int
	Fields    []Field // only the names in want, values read up to 4,096 bytes into reused buffers
}
type Field struct {
	Name       string
	Value      []byte
	Compressed bool
}
func (e *Entry) Field(name string) ([]Field, bool)
func ListFiles(dir fs.FS) ([]string, error)
func MachineDir(etc fs.FS) (string, error)

// internal/journal/journaltest
type Options struct {
	FileID, MachineID, SeqnumID [16]byte
	Online                       bool
}
func ReadExport(r io.Reader) ([]map[string][]byte, error)
func Write(w io.Writer, entries []map[string][]byte, opts Options) error

// internal/agentstate
const FileName = "agent-state.json"
type State struct {
	Boot    *Boot
	Journal *JournalPosition
	Backup  *OpenRun
}
type Boot struct {
	ID       string
	Uptime   time.Duration
	BootedAt time.Time
}
type JournalPosition struct {
	SeqnumID string
	Seqnum   uint64
	Realtime time.Time
}
type OpenRun struct {
	BootID       string
	StartedAt    time.Time
	LastSeen     time.Time
	MaxProcesses uint32
}
type Status int
const (
	StatusLoaded Status = iota
	StatusFirstStart
	StatusUnavailable
)
type Store struct{ /* unexported */ }
func Open(dir string, log *slog.Logger) (*Store, Status)
func (s *Store) State() State
func (s *Store) Update(urgent bool, fn func(*State)) // urgent: a kill, boot or backup change (10 s); else the 50 s heartbeat
type Saver struct{ /* unexported */ }
func NewSaver(s *Store, proc fs.FS) *Saver
func (v *Saver) Name() string
func (v *Saver) Collect(ctx context.Context, run scheduler.Run) ([]model.Record, error)

// internal/kernevents
const Name = "kernel"
type Sources struct {
	Journal []fs.FS // default os.DirFS("/var/log/journal"), os.DirFS("/run/log/journal")
	Etc     fs.FS   // default os.DirFS("/etc")
	Proc    fs.FS   // default os.DirFS("/proc")
}
type TriggerFunc func(collector string, t scheduler.Trigger) bool
type Kernel struct{ /* unexported */ }
func New(src Sources, store *agentstate.Store, log *slog.Logger) *Kernel
func (k *Kernel) SetTrigger(fn TriggerFunc)
func (k *Kernel) Name() string
func (k *Kernel) Collect(ctx context.Context, run scheduler.Run) ([]model.Record, error)
func parseKill(message []byte) (model.OOMKill, bool)
func newGap(from, to, at time.Time) model.Record                                 // To = From + 1 µs when To is not after From
func checkRecords(records []model.Record, at time.Time, gapFrom func(i int) time.Time) []model.Record // refused → gap
func parseMicros(b []byte) (time.Duration, bool)                                 // 1-20 ASCII digits, overflow checked, ≤ MaxInt64/1000 µs
const maxRereads = 3

// internal/backups
const Name = "backups"
type Rule struct {
	Command       string
	CmdlinePrefix string
}
type Backups struct{ /* unexported */ }
func New(proc fs.FS, rules []Rule, store *agentstate.Store, log *slog.Logger) *Backups
func (b *Backups) Name() string
func (b *Backups) Collect(ctx context.Context, run scheduler.Run) ([]model.Record, error)

// internal/config
type Backups struct {
	Processes []BackupRule `yaml:"processes"`
}
type BackupRule struct {
	Command       string `yaml:"command"`
	CmdlinePrefix string `yaml:"cmdline_prefix"`
}
// Agent gains Backups Backups `yaml:"backups"`.
func checkBackupRules(file string, lines map[string]int, rules []BackupRule) error

// internal/agent: KernelInterval, KernelTimeout = 2 s; BackupsInterval = 10 s, BackupsTimeout = 5 s; StateInterval = 10 s,
// StateTimeout = 5 s; Options gains Kernel kernevents.Sources and StateDir string ("" takes spool.directory).
```

C# (`Vandox.Core.Model`): `KillRef` (`BootId` string, `KernelTimeNs` long, `VictimPid` int, `Validate(string path)`), `SnapshotTrigger`
(`Kills` `List<KillRef>?`, `KillsOmitted` uint, `Validate(string path)`), `BackupRun : IPayload`, `OomKill.BootId` (string, empty when
absent), `OomKill.KernelTimeNs` (long?), `ProcessSnapshot.Trigger` and `ConnectionSnapshot.Trigger` (`SnapshotTrigger?`),
`RecordKind.BackupRun = "backup_run"`. No existing file is rewritten whole.

## Test files

The files of *Tests per criterion*, plus `internal/journal/siphash_test.go` and `internal/journal/journaltest/writer_test.go`; the
golden records go into `allKindRecords` in `internal/wire/encode_test.go`.

Existing test code affected (Tester adapts in step 5; old and new coexist): `internal/config/agent_test.go:96` (`AgentKeys`) and
`TestLoadAgent_Example`; `internal/agent/agent_test.go` `TestJobs_Schedules`; `tests/Vandox.Core.Tests/WireContractTests.cs:89`.

Files no unit test can cover: none.

## Areas

- `docs/areas/agent.md`: new *Kernel events* (with the commit rule, the re-read cap of three, the record check and the gap bounds), *Boots*, *Backup runs*, *Agent state*; additions to *Scheduling* (extra runs),
  *Processes*, *Connections*, *Memory pressure mode*, *Rights*, *Spool and sending* (the spool leaves the state file alone), *Status*,
  *Related decisions*, *Implementation*; residuals: the backup default (`pzstd` only), the filter proves that the kernel wrote the line,
  not that the OOM killer did (N4), `COMPACT` journals are not read.
- `docs/areas/wire-format.md`: `kernel_event` identity, `trigger`, `backup_run`, the producer size contract list, *Versioning*.
- `docs/areas/configuration-and-secrets.md`: `backups.processes`; `spool.directory` also holds the agent's state.

## Documentation updates

| File | Change | Owner |
| ---- | ------ | ----- |
| `docs/areas/agent.md`, `wire-format.md`, `configuration-and-secrets.md` | as *Areas* | Lead |
| `docs/ARCHITECTURE.md` | *Components*: the kernel and backup collectors, the state file, the extra runs; the `internal/` list | Lead |
| `README.md` | *Agent options*: `backups.processes`; `spool.directory` holds the state file | Dev |
| `deploy/agent/agent.yaml` | `backups.processes` at its default | Dev |
| `deploy/agent/README.md` | `systemd-journal` used by `kernel`; the state file; the unit keeps `/var/log/journal` and `/run/log/journal` readable, starts after `systemd-journal-flush.service`, and the installation creates `spool.directory` | Dev |
| `.squad/project.md` | areas 6, 9, 10, 12 for the new packages; *Test doubles* `journald` (implemented) and the state file | Dev |
| `.squad/stack.md` | *Layout*: the new packages | Dev |

Sweep (searched `docs/areas/` and `docs/ARCHITECTURE.md` for enumerations the change extends): wire *Records* kinds, *Producer size
contract* list, *Versioning*; Agent *Status*, *Memory pressure mode* effects, *Rights*, *Implementation*, *Related decisions*;
configuration *Options*; ARCHITECTURE *Components* and package list. `docs/areas/storage.md` lists no kinds.

## Architecture check

- **Hanging collector:** `kernel`, `backups`, `state` are jobs with timeouts; an extra run uses the same runner, never beside an abandoned
  run, under the watchdog's liveness deadline; abandoned `kernel` runs never write the store.
- **No gap unless recorded:** lost spans of kernel messages, lost extra snapshots and lost backup checks are gaps; a run whose records
  were dropped is read again, at most three times in a row, after which its span is a gap, so no position moves past an unkept kill
  without a gap; a record the wire format would refuse becomes a gap in the collector, and every gap is valid by construction.
- **Never a hang:** no record and no gap of the kernel collector can hold its position back for ever (B6).
- **Outbound only, no new right:** no socket, no program, no capability; reads the journal (`systemd-journal`), `/proc`,
  `/etc/machine-id`; one write, the state file.
- **Memory (0025):** at most 4 MiB live for the journal, no `mmap`, extra snapshots bounded and replacing a run under pressure.

## Security considerations

- [ ] Limits apply to raw bytes before decoding (journal payload 4,096, name prefix 64, items 512, arrays per chain 64, files 1,024,
  header 4,096; `boot_id`, `/proc/uptime`, `/etc/machine-id` 64; state 16 KiB); command 1,024 and message 16,384 bytes after U+FFFD
  replacement.
- [ ] Texts reaching a log: `journal: unsupported layout`, `journal: malformed file`, `journal: <online|archived>: <fixed reason>`,
  `agentstate: save: <errno text>`; never a path, field value or message.
- [ ] Kill filter (E1): `_TRANSPORT=kernel` and exactly one `SYSLOG_FACILITY` item equal to `0` (fact 10); residual N4 in the Agent area.
- [ ] Every chain walk is bounded (strictly increasing offsets, 64 arrays) so no file can make a run loop.
- [ ] The state file is trusted only after `fstat` of its `O_NOFOLLOW` descriptor; the agent never creates directories.
- [ ] The position cannot freeze: the collector returns only records that pass `wire.CheckRecord` (a refused one becomes a gap),
  `newGap` makes every gap valid, monotonic stamps are 1 to 20 digits with an overflow check, and re-reads stop after three.

## Decision records

- `docs/decisions/0119-kernel-messages-from-the-binary-journal-files-read-in-pure-go-without-decompression.md` (Proposed)
- `docs/decisions/0120-oom-kill-events-triggered-snapshots-boots-from-saved-state-and-backup-runs.md` (Proposed)

## Challenge

- M1 (kill time and liveness against a drifting kernel clock): accepted. Spot-checked (fact 19). Capture time is the receipt time minus
  the drift-corrected delay; liveness uses `/proc/uptime − __MONOTONIC_TIMESTAMP + delay`; the raw source stamp stays only in the identity.
  Spec S1, S10, E10, AC1, AC6 changed; fixture entry with 20 days of uptime and 40 s of drift; test
  `TestKernel_CaptureTimeCorrectsKernelClockDrift`.
- M2 (`.journal~` files and a new seqnum ID after an unclean close): accepted (fact 22). Set-aside files are read as archived; a torn tail
  ends a file with a gap to its header's tail; files are matched by the saved seqnum ID, then the files of other IDs follow in head-time
  order from their heads; a lost ID gives a gap and reading goes on at the first later entry. Tests
  `TestKernel_ReadsSetAsideFilesAfterUncleanShutdown`, `TestKernel_JournaldRestartStartsNewSeqnumID`, `TestFile_TornTailIsEndOfFile`;
  spec S10, E7, AC6, AC7; record 0119.
- M3 (position moving past dropped records): accepted. The position is committed at the start of the next run only when
  `run.PreviousLost` is false, else read again (at most three times in a row since B6); abandoned runs never write the store. Test `TestKernel_LostRunRereadsEntries`; spec S10,
  AC6; record 0120.
- m1 (time budget): accepted. A run stops after 1 s of reading or 256 kills; `TestKernel_TimeBudgetEachRunGetsFurther` with a slow
  `ReaderAt`.
- m2 (idle poll cost): accepted, as B4: bisection through the doubling arrays and a per-file cursor;
  `TestKernel_PollWorkIndependentOfEarlierEntries`.
- m3 (seqnums not dense): accepted. No count or gap from seqnum differences; fixtures use seqnums with holes (fact 23).
- m4 (state write rate): accepted. `Store.Update(urgent, …)`: only kill, boot and backup changes use the 10-second rate; spec *Disk*.

## Security review

- Row 10: confirmed, now fact 10. The optional measurement (facility of a `<0>` write) is added to fact 20's scratch measurement.
- B1 (private data in the fixture): accepted. Only the listed real `MESSAGE` kinds are kept (kill, `invoked oom-killer:`, `oom_reaper`
  lines of three kills; the `Linux version` line is dropped, it carries a build host); the compressed, facility-1 and facility-3 entries
  are hand-made; `TestFixture_HoldsNoRealPrivateValue` also checks units, comms, executables, command lines, user names, mail addresses
  and domains of the real files.
- B2 (unbounded hash-chain walk): accepted. Hash chains and entry-array chains follow strictly increasing offsets (fact 21), arrays at
  most 64 per chain, else `journal: malformed file`; pinned by `TestFile_BoundsEveryOffset` (a cycle and a backward link).
- B3 (memory bound): accepted. At most 4 MiB live (*Memory bound*); only the needed fields are read in full; both memory tests assert it
  at two sizes; spec *Memory and load* states the number.
- B4 (cost per poll before the position): accepted. Bisection and per-file cursor; per-poll work is a constant number of reads plus the new
  entries; pinned by `TestKernel_PollWorkIndependentOfEarlierEntries`; spec E7, AC7.
- B5 (creating `spool.directory`): accepted. The agent never creates it (#42 does); a save into a missing directory logs `agent state not
  saved`; first start is an absent file in an existing directory without other entries.
- N1: accepted (checks on `fstat` of the opened descriptor). N2: accepted (hand-made facility-1 kill line). N3: accepted (exactly one
  `SYSLOG_FACILITY` item equal to `0`; fixture entry with two items). N4: accepted (Agent area residual; comm to the last `) total-vm:`).
- B6 (a refused record freezes the position; empty or reversed gap bounds; monotonic stamp parsing), round 2: the loop limit was
  reached and the fix changes S10 and AC6 of the approved spec, so the Lead escalated; the Product Manager chose option 1 (fix in the
  collector, scheduler contract unchanged). (a) *Record check* and *Re-read cap* in *Kernel collector*: `PreviousLost` now means a
  dropped result; at most three re-reads, then a gap over the span. (b) `newGap` in *Gaps*. (c) *Timestamps*. Tests
  `TestKernel_ReversedTimeGapStillMovesPosition`, `TestKernel_RefusedRecordBecomesGap`, `TestKernel_RereadCappedAtThree`,
  `TestKernel_MonotonicStampOutOfRangeIsUnreadable`; spec S10, E16, AC6; record 0120 (option 8). Options rejected: a scheduler
  signal telling refused from dropped (changes the contract `processes` and `connections` share), dropping re-reads (gives up AC6).
- N5: accepted (*Memory bound*: about 13 KiB per kill record, about 3.3 MiB for 256). N6: accepted (exact-match exceptions `sshd`,
  `apport`, `kernel`, `systemd-journald` in `TestFixture_HoldsNoRealPrivateValue`).

## Out of scope / follow-ups

As the spec's *Out of scope*. Follow-up issue: "[Agent] Read journal files of the COMPACT layout" (systemd 252 and later; record 0119).
