# Plan: Kernel events, reboots and backup runs

Source: Issue #36 | [spec.md](spec.md)
Status: Draft
Tier: security — the change creates guards against an attacker (only kernel-transport entries with facility 0 count as kills, a bounded parser of a binary format read from disk, a state file that is refused unless it is the agent's own) and the agent's first file write.
Security areas touched: 10 Parsing of external input (`internal/journal`, the kill-line parser in `internal/kernevents`, `internal/agentstate` reading its file, `/proc/uptime` and `boot_id`, the option `backups.processes`, the new wire fields and kind in both decoders); 9 File writes (`internal/agentstate`: the state file in `spool.directory`); 12 Logging and display of external data (victim names, backup commands as record text, never logged); 6 Agent privileges (the group `systemd-journal` gets its first user, no new right; `deploy/agent/README.md`)

## Problem / root cause

The feature is specified in [spec.md](spec.md) (S1 to S13, E1 to E17, AC1 to AC20).

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | `kernel_event` with `oom_kill` and `boot` exists in Go and C#; nothing produces it | `internal/model/kernel.go`; `src/Vandox.Core/Model/KernelEvent.cs`; `grep -rn KernelEventOOMKill internal` finds only model and wire tests | confirmed |
| 2 | Wire format 1.1 is unreleased, so new optional fields and a new kind extend it in place | `git ls-remote --tags origin` lists only `v0.1.0`; wire format area *Versioning* | confirmed |
| 3 | Storage keeps every kind other than metric and log line as JSON in `records.data`; adding a kind needs `RecordQueries._kinds` but no migration | `docs/areas/storage.md` row `records`; `src/Vandox.Storage/RecordQueries.cs:28-37`; no `CHECK` on `kind` in `src/Vandox.Storage` | confirmed |
| 4 | The scheduler has no way to start an extra run; a collector is never started while an abandoned run is out | `internal/scheduler/runner.go:108-196` (`loop`, `cycle`, `pending`) | confirmed |
| 5 | `processes` and `connections` take a full snapshot when `run.Pressure` is set | `internal/procstats/procstats.go:125`, `internal/connstats/connstats.go:125` | confirmed |
| 6 | The server's journal: systemd `249.11-0ubuntu3.23`, kernel `5.15.0-191-generic`, header 256 bytes, incompatible flags `0xc` (`KEYED-HASH`, `COMPRESSED-ZSTD`), not `COMPACT`, data hash table 233,016 buckets | `journalctl --file=<real file> --header` on both files; the `systemd 249.11-0ubuntu3.23 running in system mode` and `Linux version 5.15.0-191-generic` entries | confirmed |
| 7 | Non-compact layout: object header 16 bytes; a data object's payload starts at offset 64; an entry object holds seqnum, realtime, monotonic, boot ID, xor hash and items of 16 bytes (offset, hash); objects 8-byte aligned | systemd `docs/JOURNAL_FILE_FORMAT.md`; a read-only scanner (`scratchpad/tools/jscan.py`) walking both real files counted exactly the objects `journalctl --header` reports (147,230 and 174,765 data objects) | confirmed |
| 8 | No kernel-transport entry references a compressed data object; the 8 kill lines are 159 to 171 bytes; 340 zstd objects belong to other entries | the scanner of row 7 on both files: 3,563 kernel entries, 0 with a compressed item | confirmed |
| 9 | Kernel-transport entries with a facility other than 0 exist (userspace writes to `/dev/kmsg`) | `journalctl --file=... _TRANSPORT=kernel -o export`: 3,342 with `SYSLOG_FACILITY=0`, 220 with 3, 1 with 5 | confirmed |
| 10 | The kernel refuses facility 0 for a write to `/dev/kmsg` (a userspace line can never pass the filter) | to verify: Security reads `devkmsg_write` in `kernel/printk/printk.c` at v5.15 in step 3 | unverified |
| 11 | The 5.15 kill line is `<reason>: Killed process <pid> (<comm>) total-vm:<n>kB, anon-rss:<n>kB, file-rss:<n>kB, shmem-rss:<n>kB, UID:<n> pgtables:<n>kB oom_score_adj:<n>` | the 8 real kill lines (`... UID:114 pgtables:572kB oom_score_adj:0`) | confirmed |
| 12 | The reasons `Memory cgroup out of memory` and `Out of memory (oom_kill_allocating_task)` use the same format | to verify: Tester reads `__oom_kill_process` and its callers in `mm/oom_kill.c` and `mm/memcontrol.c` at v5.15 before writing the E3 cases (step 5) | unverified |
| 13 | The keyed hash of a data object is SipHash-2-4 of its payload with the file ID as key | to verify: Dev, step 6, `TestFile_RealServerJournals` finds `_TRANSPORT=kernel` through the hash table of both real files | unverified |
| 14 | journald writes "Missed N kernel messages" as its own entry with `MESSAGE_ID=e9bf28e6e834481bb6f48f548ad13606` on the driver transport | to verify: Dev reads `src/journal/journald-kmsg.c` and `sd-messages.h` at systemd v249 in step 6; the real files hold no such entry | unverified |
| 15 | journald compresses a field only from 512 bytes on (default threshold of systemd 249) | to verify: Dev reads `COMPRESSION_SIZE_THRESHOLD_DEFAULT` in systemd v249 (`src/libsystemd/sd-journal/journal-file.h`) in step 6 | unverified |
| 16 | On 2 Oct 2026 there was no reboot; `pzstd` (04:51:36 UTC) and `mariadbd` (04:59:32 UTC) were killed; the next reboot was 21 Oct 16:28:27 UTC | `journalctl --file=... --list-boots` and the kill entries; the issue's "rebooted at 06:51" is refuted (spec *Problem*) | refuted |
| 17 | `journalctl` 255 here reads and verifies the non-compact layout and is a check of a generated fixture | the real files read with it (rows 6, 9); `journalctl --verify` passes the prototype `scratchpad/fixture/oom.journal` | confirmed |
| 18 | The journal files on the server are readable by the group `systemd-journal` | to verify: #42 (installation) checks `ls -ln /var/log/journal/<machine-id>` on the server; the Agent area names the group already | unverified |
| 19 | `/proc/uptime` counts like the kernel's `_SOURCE_MONOTONIC_TIMESTAMP` on a server that never suspends | to verify: Tester compares `/proc/uptime` with the stamp of a line written to `/dev/kmsg` in a scratch measurement (step 5); a suspend is a stated residual | unverified |
| 20 | The agent is already a member of `systemd-journal` and holds no capability that reading the journal needs | Agent area *Rights*; `deploy/agent/README.md:4` | confirmed |

## Acceptance criteria

The criteria are AC1 to AC20 of [spec.md](spec.md).

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `TestKernel_LiveKillBecomesEventWithinFiveSeconds`, `TestKernel_KillCaptureTimeFromItsOwnBoot`, `TestKernel_KillIdentityStableAcrossReadings`; `TestParseKill_RealLines` | `internal/kernevents/kernevents_test.go`; `internal/kernevents/kill_test.go` |
| AC2 | `TestScheduler_TriggerRunsExtraWithinOneSecond`, `TestScheduler_TriggerWaitsForCurrentRun`, `TestScheduler_TriggerKeepsGridWithoutPressure`, `TestScheduler_TriggerReplacesNextRunUnderPressure`; `TestProcesses_TriggeredRunIsFullAndNamesKills`; `TestConnections_TriggeredRunIsFullAndNamesKills`; `TestKernel_LiveKillTriggersBothCollectors` | `internal/scheduler/trigger_test.go`; `internal/procstats/procstats_test.go`; `internal/connstats/connstats_test.go`; `internal/kernevents/kernevents_test.go` |
| AC3 | `TestKernel_StormCoalescesWithinFifteenSeconds`; `TestScheduler_TriggerMergesKillsUpToSixtyFour` | `internal/kernevents/kernevents_test.go`; `internal/scheduler/trigger_test.go` |
| AC4 | `TestParseKill_IgnoresOtherLines`; `TestKernel_OnlyKernelTransportWithFacilityZero` | `internal/kernevents/kill_test.go`; `internal/kernevents/kernevents_test.go` |
| AC5 | `TestParseKill_HostileNames`, `TestParseKill_MissingFields`, `TestParseKill_AllKillForms` | `internal/kernevents/kill_test.go` |
| AC6 | `TestKernel_ResumesAtSavedPositionAcrossBoots`, `TestKernel_LateOrOtherBootKillsGetNoSnapshot` | `internal/kernevents/kernevents_test.go` |
| AC7 | `TestKernel_LostPositionRecordsGap`, `TestKernel_UndecodableEntryRecordsGap`, `TestKernel_HangingJournalLosesOnlyItsSample`; `TestFile_RefusesUnsupportedLayout`, `TestFile_BoundsEveryOffset` | `internal/kernevents/kernevents_test.go`; `internal/journal/journal_test.go` |
| AC8 | `TestKernel_UnreadableJournalLogsOnceAndResumes` | `internal/kernevents/kernevents_test.go` |
| AC9 | `TestBoot_NewBootRecordsPreviousBootAndUptime` | `internal/kernevents/boot_test.go` |
| AC10 | `TestBoot_SameBootRecordsNothing`, `TestBoot_FirstStartWithoutPrevious`, `TestBoot_WithoutStateEveryStart`; `TestOpen_StatusAndWarnings` | `internal/kernevents/boot_test.go`; `internal/agentstate/agentstate_test.go` |
| AC11 | `TestBoot_BadBootIDRecordsNothing` | `internal/kernevents/boot_test.go` |
| AC12 | `TestBackups_StartWithinFifteenSeconds`, `TestBackups_EndAfterSixtySeconds`, `TestBackups_ShortPauseKeepsOneRun`, `TestBackups_SingleSightingGivesStartAndEnd`, `TestBackups_CountsWithoutKeepingProcesses` | `internal/backups/backups_test.go` |
| AC13 | `TestBackups_RestartContinuesRun`, `TestBackups_RunEndedAcrossStopOrReboot` | `internal/backups/backups_test.go` |
| AC14 | `TestLoadAgent_BackupProcesses`; `TestBackups_RulesAreLiteral`; `TestJobs_BackupsOnlyWithRules` | `internal/config/agent_test.go`; `internal/backups/backups_test.go`; `internal/agent/agent_test.go` |
| AC15 | `TestBackupRun_Validate`; `TestSnapshotTrigger_Validate`; `TestKernelEvent_Validate_KillIdentity`; `TestEncodeBatchGolden`; `TestDecoder_FieldErrors` (new cases); C#: `BackupRunValidateRules`, `SnapshotTriggerValidateRules`, `KillRefValidateRules`, `PayloadValidationOomKillIdentity`, `WireContractDecodesGoldenBatch` (new records), `QueryRecordsAcceptsBackupRun` | `internal/model/backup_test.go`; `internal/model/trigger_test.go`; `internal/model/kernel_test.go`; `internal/wire/golden_test.go`; `internal/wire/decode_test.go`; `tests/Vandox.Core.Tests/BackupRunTests.cs`, `SnapshotTriggerTests.cs`, `KillRefTests.cs`, `PayloadValidationTests.cs`, `WireContractTests.cs`; `tests/Vandox.Storage.Tests/SqliteStoreQueryTests.cs` |
| AC16 | `TestFile_MemoryDoesNotGrowWithJournalSize`; `TestKernel_BacklogMemoryIsBounded` | `internal/journal/journal_test.go`; `internal/kernevents/kernevents_test.go` |
| AC17 | `TestSave_AtomicAndOwnerOnly`, `TestSaver_WriteRates`, `TestOpen_CorruptOrForeignFileIsMissing` | `internal/agentstate/agentstate_test.go` |
| AC18 | `TestKernel_LogsHoldNoMessageText`; `TestBackups_LogsHoldNoProcessText` | `internal/kernevents/kernevents_test.go`; `internal/backups/backups_test.go` |
| AC19 | `TestScheduler_AbandonedExtraRunRecordsGapAndSkips`, `TestScheduler_FailedExtraRunRecordsGap`, `TestScheduler_TriggerWhileStuckRecordsGap` | `internal/scheduler/trigger_test.go` |
| AC20 | `TestKernel_MissedKernelMessagesRecordGap` | `internal/kernevents/kernevents_test.go` |

Not tied to a criterion but required: `TestWrite_JournalctlAcceptsFixture` (skips when `journalctl` is not on the `PATH`; GitHub's Ubuntu runners have it), `TestFile_RealServerJournals` and `TestFixture_HoldsNoRealPrivateValue` (both skip unless `VANDOX_REAL_JOURNAL_DIR` names the directory of the real files).

## Approach

**Collectors and schedules** (`internal/agent` constants, job order after `clock`): `kernel` (interval 2 s, timeout 2 s, always
scheduled), `backups` (interval 10 s, timeout 5 s, scheduled only with at least one rule), `state` (interval 10 s, timeout 5 s,
always scheduled; returns no records). 2 s polls plus processing meet the 5 s of AC1.

**Journal reader** (`internal/journal`, package `journal`, security area 10). Reads through `io.ReaderAt` with fixed small buffers,
never `mmap`, so file pages do not count to the agent's RSS. The directory is `/var/log/journal/<machine-id>` when it exists, else
`/run/log/journal/<machine-id>`; the machine ID is the 32 lower-case hex digits of `/etc/machine-id` (at most 64 bytes read). Files
read: `system.journal` and `system@*.journal` (at most 1,024 listed, in chunks; `.journal~` files are not read, a stated residual);
per file only the header is kept (seqnum ID, head and tail seqnum and realtime, state). Accepted layout: signature `LPKSHHRH`, header
size 256 to 4,096, `KEYED-HASH` set, no flag outside `XZ`, `LZ4`, `KEYED-HASH`, `ZSTD`; else `journal: unsupported layout`. Every
offset is 8-aligned, inside `[header_size, min(header_size + arena_size, file size))` and below the header's tail object for an online
file; every object size is at least its fixed part and ends inside that range; entry arrays are followed only forward (strictly
increasing offsets), so a cycle cannot loop; at most 512 items per entry; a data payload is read up to 4,096 bytes (a longer one
counts as "not a kill line", never as an error, since kernel messages are at most 1,024 bytes); limits apply to raw bytes before any
decoding. The kernel entries are the entry list of the data object `_TRANSPORT=kernel`, found through the data hash table
(SipHash-2-4, key the file ID); journald's missed-messages entries are the list of `MESSAGE_ID=e9bf28e6e834481bb6f48f548ad13606`
filtered to `_TRANSPORT=driver`. An inconsistent online file (a torn write) is retried at the next poll and becomes a gap only when the
same entry fails three polls in a row. A compressed item is never decompressed: if `MESSAGE`, `SYSLOG_FACILITY` or
`_SOURCE_MONOTONIC_TIMESTAMP` of a kernel entry is compressed, the entry is unreadable (gap of one microsecond span widened to the
neighbours, below).

**Kernel collector** (`internal/kernevents`, name `kernel`). Per run: (first run only) the boot check; then from the saved position
`(seqnum ID, seqnum)` over the files with that seqnum ID in head-seqnum order, at most 2,000 kernel entries per run (the rest in the
next runs, so a backlog never runs into the timeout). An entry counts when `SYSLOG_FACILITY` is exactly `0`; then its `MESSAGE` goes to
`parseKill`. Time of a kill: `bootStart + _SOURCE_MONOTONIC_TIMESTAMP` with `bootStart = __REALTIME_TIMESTAMP − __MONOTONIC_TIMESTAMP`
of that entry; outside `[bootStart, receipt]` it is the receipt time. Identity: entry boot ID, kernel time in ns (the source stamp,
else the entry's monotonic stamp), victim PID. Live: the entry's boot ID equals `/proc/sys/kernel/random/boot_id` and
`/proc/uptime − kernel time ≤ 60 s`. Live kills go to the trigger state: if no triggered snapshot started in the last 15 s, call
`Trigger` at once for `processes` and `connections` with this kill; else keep it (at most 64, then count) and call `Trigger` with the
kept kills at the first run 15 s after the last trigger. Gaps (source `kernel`, collector `kernel`, cause `unknown`): a saved position
whose seqnum ID has no file, or older than the oldest file's head (from the position's realtime to the oldest entry's); an unreadable
entry or an unsupported file (from the last readable kernel entry's realtime to the next one's, or to the run's start); a missed-messages
entry (from the last kernel entry before it to the next one after it). The position is advanced in the state after the run's records
are returned (a crash between gives at most a second reading with the same identity, spec AC1). Without a saved position the reader
starts at the newest file's tail. Logs: warning `kernel messages not readable` (`err`: `journal: <file kind>: <fixed reason>`, never a
path or content) and info `kernel messages readable again`; warning `boot ID not readable`.

**Kill parser** (`parseKill`). Prefix exactly one of `Out of memory: Killed process `, `Memory cgroup out of memory: Killed process `,
`Out of memory (oom_kill_allocating_task): Killed process `; PID 1 to 10 digits within 32 bits and above 0; ` (`; the comm up to the
**last** `) total-vm:` of the message (a comm is at most 15 bytes, so a forged `) total-vm:` inside it is followed by the real one);
then the tail split at `, ` and ` ` into `key:value` tokens, of which only `anon-rss:<1-20 digits>kB` (times 1,024 with overflow check)
and `oom_score_adj:<-?1-4 digits>` (−1000 to 1000) are used, each at most once; anything else in the tail is ignored. The command is
`procstats.CleanText(comm, 1024)`, `(unnamed)` when empty; `message` is the whole message cleaned the same way at 16,384 bytes.

**Boots** (in `internal/kernevents`, first run of `kernel`). `boot_id` read up to 64 bytes and checked as a lower-case UUID with one
trailing line feed; `booted_at` from `procstats.ReadBootTime`. State boot ID equal: nothing. Different: `kernel_event` `boot` with
`previous_boot_id` and `previous_uptime_ns` from the state. No state boot: event without them. Then the state gets the current boot
ID and uptime.

**State** (`internal/agentstate`). File `agent-state.json` in `spool.directory`: JSON with `version` 1, read up to 16 KiB with
`O_NOFOLLOW|O_NONBLOCK`, accepted only as a regular file of the agent's effective UID with no permission bits for group or others and
strict decoding (unknown keys, wrong types, invalid UUIDs or times refused); anything else is "not available". Absent file in an empty
or missing-but-creatable directory is a first start (no warning); every other failure logs `agent state not available` once. Saving:
temporary file `agent-state.json.tmp` created with `O_CREAT|O_EXCL|O_NOFOLLOW`, mode 0600, written, `fsync`, renamed, directory
`fsync`; a left-over temporary file is removed first. The `state` job saves when the state changed and 10 s passed since the last save,
or 50 s passed (the uptime heartbeat); a failed save logs `agent state not saved` (`err`, an OS error text without the path) once until
a save succeeds. The spool (#38) must count and delete only its own files: the Agent area says so.

**Scheduler** (`internal/scheduler/trigger.go`). `Trigger` merges the kills into the runner's pending trigger (at most 64, the rest
counted) and wakes it; it returns false only for an unknown collector. A waiting runner handles it at once; a running one after its
run. The extra run: if an abandoned run is still out, no run and a `collector_timeout` gap from now over one interval of the mode;
else `Run{Start, Pressure, PreviousLost, Trigger}` with the same outcome handling as a cycle (gap over `[start, start+interval)`,
`unknown` for an error or panic, `collector_timeout` when abandoned, then the usual skips). Without pressure the grid stays; with
pressure (and a pressure interval) the next due time becomes `start + pressure interval`. The liveness deadline covers the extra run.

**Snapshots.** `processes`: `run.Trigger != nil` lists every process, as under pressure; `connections`: it makes the snapshot full;
both set `Trigger` on the record from `run.Trigger`.

**Backup check** (`internal/backups`, name `backups`). Per run: `procstats.ListPIDs`, then per PID `CommandReader.Process`; a rule
matches when `command` equals the process's command, or the cleaned command line starts with `cmdline_prefix`. Run start: the earliest
`started_at` among the matches of the first run that sees one; the start record carries that process's PID and command and the count.
Each run that sees a match sets `last_seen` and `max_processes`; the first run without a match sets `gone_by`; when 60 s pass after
`last_seen` without a match, the end record is written. The open run lives in the state; at the first run after a start: a stored run
of another boot, or one with no match now, is ended (`last_seen` from the state, `gone_by` now); a stored run of this boot with a match
continues. Logs: none beyond the scheduler's.

**Records (wire 1.1 additions).** `kernel_event.oom_kill`: optional `boot_id` (UUID) and `kernel_time_ns` (≥ 0); the agent sets both.
`process_snapshot` and `connection_snapshot`: optional `trigger` `{"kills":[{"boot_id","kernel_time_ns","victim_pid"}],
"kills_omitted":n}` (1 to 64 distinct kills). New kind `backup_run`, source `backups`: `phase` `start` (`boot_id`, `started_at`, `pid`
> 0, `command` short text, `processes` ≥ 1; `captured_at` = `started_at`) or `end` (`boot_id`, `started_at`, `last_seen_at` ≥
`started_at`, `gone_by` ≥ `last_seen_at`, `max_processes` ≥ 1; `captured_at` = `last_seen_at`), no field of the other phase.
`kernel_event` source `kernel`. Field errors use the existing reasons (`required`, `must be greater than 0`, `invalid UUID`,
`not allowed for phase start`, `must not be before started_at`).

**Configuration.** `backups.processes`: list of mappings `command` | `cmdline_prefix`, default `[{command: pzstd}]`, errors as listed in
the spec (AC14) with the key path `backups.processes[<i>]`; `AgentKeys` appends `backups.processes`.

**Fixtures** (Q3 decisions). `testdata/journal/kernel.export`: the source of truth in journal export text, made by the Tester from the
real files (entries of 30 Sep and 2 Oct: the `invoked oom-killer`, kill and `oom_reaper` lines of `pzstd` and `mariadbd`, one kill of
`/usr/sbin/amavi`; a facility-3 kernel entry; the `Linux version` entry of boot `f96458fd…`; one non-kernel entry of at least 512 bytes,
written compressed) plus hand-made entries, each with the field `VANDOX_FIXTURE=handmade` (a `logger -t kernel` kill line on the syslog
transport, a missed-messages entry per row 14, a kernel entry whose `MESSAGE` is stored compressed). Only `__REALTIME_TIMESTAMP`,
`__MONOTONIC_TIMESTAMP`, `__SEQNUM`, `_BOOT_ID`, `_TRANSPORT`, `SYSLOG_FACILITY`, `SYSLOG_IDENTIFIER`, `PRIORITY`,
`_SOURCE_MONOTONIC_TIMESTAMP`, `MESSAGE`, `MESSAGE_ID` and `_HOSTNAME`/`_MACHINE_ID` are kept; `_HOSTNAME` becomes `web-1`, the machine
ID and boot IDs become fixed made-up values, every other field is dropped. `internal/journal/journaltest` (a non-test package used only by
tests) writes journal files in the server's layout (rows 6 and 7: 256-byte header, `KEYED-HASH|COMPRESSED-ZSTD`, non-compact, data and
field hash tables, entry arrays; a compressed item as a zstd frame of raw blocks, which needs no compressor). `VANDOX_UPDATE_GOLDEN=1 go
test ./internal/journal/journaltest` writes `testdata/journal/system.journal` (online) and
`testdata/journal/system@00000000000000000000000000000001-0000000000000001-0005f8d1b1a2c3d4.journal` (archived) from the export;
`TestWrite_JournalctlAcceptsFixture` runs `journalctl --verify` and `-o export` on them and compares the entries with the export. Before
the fixtures are committed, the Tester runs `TestFixture_HoldsNoRealPrivateValue` with the real files: it decodes every field of the
export and both files and fails when any contains a private value of the real files (host name, machine ID, boot IDs, the address in the
host name and its parts, the provider domain) or a substring of 6 bytes or more of one.

**Real files** (never committed, never copied into the working tree). A developer keeps them in a directory outside the repository with
mode 0700 and runs `VANDOX_REAL_JOURNAL_DIR=<dir> go test ./internal/journal/... -run Real` (in this session the directory is
`/tmp/claude-0/-home-user-Vandox/58d2c1d7-bdb4-5d0c-8a35-e08f00638a4a/scratchpad/journalzip`). `TestFile_RealServerJournals` asserts the
counts only (kernel entries per file 1,456 and 2,107, 8 kills, 0 kernel entries with a compressed item) and prints no field value. The
Dev runs it in step 6 and the Tester in its coverage step, and both note the result in their reports.

Layout of new user-visible output: the record fields and log texts above are fixed; nothing else is left to the Tester.

## Options considered

The source of the kernel messages, compression, the hash lookup and the fixture are weighed in
[0119](../../docs/decisions/0119-kernel-messages-from-the-binary-journal-files-read-in-pure-go-without-decompression.md) (binary files in Go
chosen; `journalctl` child, `/dev/kmsg`, cgo, `kern.log`, a zstd library, a full scan and `systemd-journal-remote` output rejected).
The triggered runs, the link, the backup record, the state and the backup default are weighed in
[0120](../../docs/decisions/0120-oom-kill-events-triggered-snapshots-boots-from-saved-state-and-backup-runs.md). One further choice: the
kernel collector polls through the scheduler (chosen: the deadline, gaps and "never started twice" come for free; a run that the
scheduler abandons records a `collector_timeout` gap although the entries are read later, which over-reports a gap but never hides one)
rather than a free-running follower goroutine (no scheduler gap, but its own deadline and stall detection to build).

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Go | `internal/model/kernel.go` | `OOMKill.BootID`, `OOMKill.KernelTime` and their validation |
| Go | `internal/model/trigger.go` (new) | `KillRef`, `SnapshotTrigger`, `MaxTriggerKills` |
| Go | `internal/model/process.go`, `connection.go` | `Trigger` field and validation |
| Go | `internal/model/backup.go` (new), `model.go` | `BackupRun`, `BackupPhase`, `KindBackupRun`; the kind in the decoder's kind table (`internal/wire/decode.go`) |
| Go | `internal/scheduler/trigger.go` (new), `scheduler.go`, `runner.go` | `Trigger`, `Run.Trigger`, `Scheduler.Trigger`, the extra run |
| Go | `internal/procstats/procstats.go`, `command.go`, `boottime.go` | triggered full snapshot; `ProcessInfo`, `CommandReader.Process`, `ReadBootTime` |
| Go | `internal/connstats/connstats.go` | triggered full snapshot |
| Go | `internal/journal/` (new) | reader; `journaltest/` writer |
| Go | `internal/kernevents/` (new) | collector `kernel`: kills, boots, gaps, triggers |
| Go | `internal/agentstate/` (new) | state file and the `state` job |
| Go | `internal/backups/` (new) | collector `backups` |
| Go | `internal/config/agent.go` | `Backups`, `BackupRule`, `checkBackupRules`, default, `AgentKeys` |
| Go | `internal/agent/agent.go` | jobs, constants, the store, `Options` sources, `SetTrigger` wiring |
| C# | `src/Vandox.Core/Model/OomKill.cs`, `ProcessSnapshot.cs`, `ConnectionSnapshot.cs`, `KillRef.cs` (new), `SnapshotTrigger.cs` (new), `BackupRun.cs` (new), `RecordKind.cs`, `PayloadRegistry.cs` | the same fields, kind and rules |
| C# | `src/Vandox.Storage/RecordQueries.cs` | `backup_run` in `_kinds` |
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
// OOMKill gains: BootID string `json:"boot_id,omitempty"`; KernelTime *time.Duration `json:"kernel_time_ns,omitempty"`
// ProcessSnapshot and ConnectionSnapshot gain: Trigger *SnapshotTrigger `json:"trigger,omitempty"`
const KindBackupRun Kind = "backup_run"
type BackupPhase string
const (BackupPhaseStart BackupPhase = "start"; BackupPhaseEnd BackupPhase = "end")
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
// Run gains: Trigger *Trigger // set only for an extra run that Scheduler.Trigger requested
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
var (ErrUnsupported = errors.New("journal: unsupported layout"); ErrMalformed = errors.New("journal: malformed file"))
type Header struct {
	FileID, MachineID, SeqnumID [16]byte
	Online                       bool
	HeadSeqnum, TailSeqnum       uint64
	HeadRealtime, TailRealtime   time.Time
}
type File struct{ /* unexported */ }
func Open(r io.ReaderAt, size int64) (*File, error)
func (f *File) Header() Header
func (f *File) Refresh(size int64) error
func (f *File) Lookup(payload []byte) (Data, bool, error)
type Data struct{ /* offset, number of entries */ }
func (d Data) Len() uint64
func (f *File) Entries(d Data, after uint64, limit int, fn func(Entry) error) error
type Entry struct {
	Seqnum    uint64
	Realtime  time.Time
	Monotonic time.Duration
	BootID    [16]byte
	Fields    []Field
}
type Field struct {
	Name       string
	Value      []byte
	Compressed bool
}
func (e *Entry) Field(name string) (Field, bool)
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
const (StatusLoaded Status = iota; StatusFirstStart; StatusUnavailable)
type Store struct{ /* unexported */ }
func Open(dir string, log *slog.Logger) (*Store, Status)
func (s *Store) State() State
func (s *Store) Update(fn func(*State))
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
// Agent gains: Backups Backups `yaml:"backups"`
func checkBackupRules(file string, lines map[string]int, rules []BackupRule) error

// internal/agent: constants KernelInterval = 2 * time.Second, KernelTimeout = 2 * time.Second, BackupsInterval = 10 * time.Second,
// BackupsTimeout = 5 * time.Second, StateInterval = 10 * time.Second, StateTimeout = 5 * time.Second; Options gains
// Kernel kernevents.Sources and StateDir string ("" takes spool.directory).
```

C# (`Vandox.Core.Model`): `KillRef` (`BootId` string, `KernelTimeNs` long, `VictimPid` int, `Validate(string path)`),
`SnapshotTrigger` (`Kills` `List<KillRef>?`, `KillsOmitted` uint, `Validate(string path)`), `BackupRun : IPayload` (properties as the Go
fields, `Kind`, `Validate()`), `OomKill.BootId` (string, empty when absent), `OomKill.KernelTimeNs` (long?),
`ProcessSnapshot.Trigger` and `ConnectionSnapshot.Trigger` (`SnapshotTrigger?`), `RecordKind.BackupRun = "backup_run"`.

Existing files the skeleton rewrites: none whole; the files above gain members.

## Test files

`internal/journal/journal_test.go`, `internal/journal/siphash_test.go`, `internal/journal/journaltest/writer_test.go`,
`internal/kernevents/kernevents_test.go`, `internal/kernevents/kill_test.go`, `internal/kernevents/boot_test.go`,
`internal/agentstate/agentstate_test.go`, `internal/backups/backups_test.go`, `internal/scheduler/trigger_test.go`,
`internal/procstats/procstats_test.go`, `internal/connstats/connstats_test.go`, `internal/model/backup_test.go`,
`internal/model/trigger_test.go`, `internal/model/kernel_test.go`, `internal/wire/golden_test.go` (records added in `encode_test.go`
`allKindRecords`), `internal/wire/decode_test.go`, `internal/config/agent_test.go`, `internal/agent/agent_test.go`;
`tests/Vandox.Core.Tests/BackupRunTests.cs`, `SnapshotTriggerTests.cs`, `KillRefTests.cs`, `PayloadValidationTests.cs`,
`WireContractTests.cs`; `tests/Vandox.Storage.Tests/SqliteStoreQueryTests.cs`.

Existing test code affected (Tester adapts in step 5, since old and new coexist): `internal/config/agent_test.go:96` (`AgentKeys` list)
and `TestLoadAgent_Example` (the example file gains the key); `internal/agent/agent_test.go` `TestJobs_Schedules` (job list);
`tests/Vandox.Core.Tests/WireContractTests.cs:89` (expected kinds of the golden batch). No signature is removed.

Files no unit test can cover: none (`journaltest` is tested by its own test; wiring in `internal/agent` by `TestJobs_*` and `TestRun_*`).

## Areas

- `docs/areas/agent.md`: new sections *Kernel events*, *Boots*, *Backup runs*, *Agent state*; additions to *Scheduling* (extra runs),
  *Processes*, *Connections*, *Memory pressure mode*, *Rights* (journal, state file), *Spool and sending* (the spool leaves the state file
  alone), *Status*, *Related decisions*, *Implementation*; the stated gap of the backup default.
- `docs/areas/wire-format.md`: `kernel_event` (`boot_id`, `kernel_time_ns`, producer), `trigger` of both snapshots, the kind
  `backup_run`, the producer size contract list (`backup_run` always fits), *Versioning* (what 1.1 adds), *Related decisions*.
- `docs/areas/configuration-and-secrets.md`: `backups.processes` row and rules; `spool.directory` also holds the agent's state.

## Documentation updates

| File | Change | Owner |
| ---- | ------ | ----- |
| `docs/areas/agent.md`, `docs/areas/wire-format.md`, `docs/areas/configuration-and-secrets.md` | as *Areas* | Lead |
| `docs/ARCHITECTURE.md` | *Components* (the agent "collects … kernel events": now how, the new packages, the state file) and the record list | Lead |
| `README.md` | *Agent options* row `backups.processes`; `spool.directory` row mentions the state file | Dev |
| `deploy/agent/agent.yaml` | `backups.processes` at its default | Dev |
| `deploy/agent/README.md` | `systemd-journal` now used by the `kernel` collector; the state file written in `spool.directory`; the unit must keep `/var/log/journal` and `/run/log/journal` readable and start after `systemd-journal-flush.service` | Dev |
| `.squad/project.md` | security areas 6, 9, 10, 12 entries for the new packages; *Test doubles* rows `journald` (implemented: `journaltest` files, a growing in-memory file, a blocking reader) and the state file; *Integration surface* unchanged | Dev |
| `.squad/stack.md` | *Layout*: the new packages | Dev |

Sweep for enumerations the change extends (searched `docs/areas/` and `docs/ARCHITECTURE.md`): wire format *Records* (kinds), *Producer
size contract* list of kinds that always fit, *Versioning* 1.1 additions; Agent area *Status* list, *Memory pressure mode* effect list,
*Rights* list, *Implementation* list, *Related decisions*; configuration *Options* table; ARCHITECTURE *Components* and the
`internal/` package list. `docs/areas/storage.md` enumerates no kinds (no change).

## Architecture check

- **A hanging collector never blocks the agent:** `kernel`, `backups` and `state` are scheduled jobs with timeouts; an extra run uses
  the same runner, is never started beside an abandoned run, and its liveness deadline counts for the watchdog.
- **No gap unless recorded:** every lost span of kernel messages, every lost extra snapshot and every lost backup check is a gap
  (spec E5, E6, E7, E11); an abandoned `kernel` poll is a gap even if its entries are read later (over-reporting, never hiding).
- **Outbound only, no new right:** no socket, no program, no capability; the files read are the journal (group `systemd-journal`),
  `/proc`, `/etc/machine-id`; the one write is the state file in `spool.directory`.
- **Memory (0025):** fixed buffers, no `mmap`, extra snapshots at most one per collector per 15 s and replacing a regular run under
  pressure.

## Security considerations

- [ ] Byte and length limits are on raw bytes before decoding: journal payload 4,096 bytes, items 512, file list 1,024, header size up
  to 4,096; `boot_id` and `/proc/uptime` 64 bytes, `/etc/machine-id` 64 bytes, the state file 16 KiB; the kill command is cut to 1,024
  and the message to 16,384 bytes after U+FFFD replacement (`procstats.CleanText` measures after replacement).
- [ ] Error texts that reach a log: `journal: unsupported layout`, `journal: malformed file`, `journal: <file kind>: <fixed reason>`
  with the file kind `online` or `archived` (never a path, a field value or a message), OS error texts without the path for the state
  file (`agentstate: save: <errno text>`); no raw runtime exception message reaches a user.
- [ ] The kill filter (kernel transport and facility 0) is the guard of E1; row 10 is Security's to verify.
- [ ] The state file is trusted only as the agent's own regular file without group or other permissions (a link or another user's file
  is "not available"), and its values are validated like wire values before use.

## Decision records

- `docs/decisions/0119-kernel-messages-from-the-binary-journal-files-read-in-pure-go-without-decompression.md` (Proposed)
- `docs/decisions/0120-oom-kill-events-triggered-snapshots-boots-from-saved-state-and-backup-runs.md` (Proposed)

## Out of scope / follow-ups

As the spec's *Out of scope*. Follow-up issue for the orchestrator to open: "[Agent] Read journal files of the COMPACT layout" — the
reader supports only the layout of systemd 249; a server upgrade to systemd 252 or later turns kernel events into gaps (record 0119).
