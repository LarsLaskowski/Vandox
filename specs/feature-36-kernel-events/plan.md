# Plan: Kernel events, reboots and backup runs

Source: Issue #36 | [spec.md](spec.md)
Status: Approved by Security
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
| 12 | The cgroup and allocating-task reasons use the same format | to verify: Tester reads `__oom_kill_process` and callers in `mm/oom_kill.c`, `mm/memcontrol.c` v5.15 (step 5). Not reported in step 5. Step 9 (Lead): a scratch copy of `mm/oom_kill.c` of unestablished version (no `process_mrelease`, so likely before 5.15) has one `pr_err("%s: Killed process %d (%s) total-vm:…")` for all three prefixes, `out_of_memory` passing `Memory cgroup out of memory` for a memcg OOM and `Out of memory (oom_kill_allocating_task)`; the format equals the 8 real 5.15 lines (fact 11). The exact v5.15 text stays unread; residual in the Agent area (a differing line is no event and no gap) | unverified |
| 13 | The keyed hash is SipHash-2-4 of the payload keyed with the file ID | Dev, step 6 (G4): `TestFile_RealServerJournals` finds `_TRANSPORT=kernel` through `File.Lookup` in both real files (1,456 and 2,107 kernel entries, 8 kills) | confirmed |
| 14 | journald's "Missed N kernel messages" entry has `MESSAGE_ID=e9bf28e6e834481bb6f48f548ad13606` on the driver transport | Dev, step 6 (G4), systemd v249 `journald-kmsg.c` and `sd-messages.h`: `SD_MESSAGE_JOURNAL_MISSED` emitted through `server_driver_message` with `_TRANSPORT=driver`, `SYSLOG_FACILITY=3`, `SYSLOG_IDENTIFIER=systemd-journald`, `PRIORITY=6`; not in the real files | confirmed |
| 15 | journald compresses only fields whose whole `NAME=value` payload is at least 512 bytes, and not every such payload is compressed | Dev, step 6 (G4), systemd v249 `DEFAULT_COMPRESS_THRESHOLD` 512 applied to the whole payload | confirmed |
| 16 | No reboot on 2 Oct 2026; `pzstd` and `mariadbd` were killed; next reboot 21 Oct 16:28:27 UTC | `journalctl --file=... --list-boots` and the kill entries; the issue's account is refuted (spec *Problem*) | refuted |
| 17 | `journalctl` 255 here verifies and exports the non-compact layout | rows 6 and 9 read with it; `--verify` passes the prototype `scratchpad/fixture/oom.journal` | confirmed |
| 18 | The server's journal files are readable by `systemd-journal` | to verify: #42 checks `ls -ln /var/log/journal/<machine-id>` on the server | unverified |
| 19 | The kernel's printk clock drifts against `CLOCK_MONOTONIC` and real time | Devil's Advocate M1 against `audit(...)` stamps; spot check here: the 2 Oct `mariadbd` kill has receipt monotonic minus source stamp = 4.30 s at uptime 128,205 s (about 33.5 µs/s) | confirmed |
| 20 | `/proc/uptime` equals the journal's `__MONOTONIC_TIMESTAMP` clock on a server that never suspends | to verify: #42 on the server compares the first field of `/proc/uptime` with the `__MONOTONIC_TIMESTAMP` of a fresh `logger` entry (`journalctl -o export -n 1`), expected difference below 1 s; not measurable in the session (no journald in the sandbox, step 5). By the kernel's definitions `/proc/uptime` counts `CLOCK_BOOTTIME`, which includes suspend, and `__MONOTONIC_TIMESTAMP` is `CLOCK_MONOTONIC`, which does not; without a suspend they agree. Residual until then, in record 0120 and the Agent area: after a suspend every kill reads as late and gets no snapshots (fails closed: an event without snapshots, never a snapshot too many) | unverified |
| 21 | Entry arrays double in size (a chain has at most 64 arrays); new data objects are linked at their hash chain's end (offsets strictly increase) | systemd `journal-file.c` (`scratchpad/src/`) lines 1837-1843 and 1254-1265; the file is append-only | confirmed |
| 22 | An unclean close renames the file to `*.journal~`, and the next file gets a new seqnum ID | systemd v249 `journal-file.c` 589-593 and 3836-3861, `journald-server.c` 281-283, `journal-file.c` 443-446 (Devil's Advocate M2) | confirmed |
| 23 | Seqnums are not dense | archived file: 73,265 entries over seqnums 1 to 30,218,745; online file: 72,909 over 30,674,914 to 30,747,972 | confirmed |
| 24 | `wire.CheckRecord` refuses a time from 1 Jan 10000 UTC on (`253402300800000000` µs, also `1<<62`): `Time.MarshalJSON: year outside of range [0,9999]`; `checkTime` (`internal/model/model.go:260-268`) checks only non-zero and UTC | Security's scratch program `scratchpad/sec36/rt/main.go` (B7) | confirmed |

## Acceptance criteria

AC1 to AC20 of [spec.md](spec.md).

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `TestKernel_LiveKillBecomesEventWithinFiveSeconds`, `TestKernel_CaptureTimeCorrectsKernelClockDrift` (also: the fixture read from the boot's first entry gives the same 0.490464 s and 0.410875 s delays as from seqnum 21; a line received more than 600 s before the kill does not set the drift; a re-read after a dropped run gives the same capture time; a source stamp above `__MONOTONIC_TIMESTAMP` counts as lag 0), `TestKernel_KillIdentityStableAcrossReadings`; `TestParseKill_RealLines` | `internal/kernevents/kernevents_test.go`; `internal/kernevents/kill_test.go` |
| AC2 | `TestScheduler_TriggerRunsExtraWithinOneSecond`, `TestScheduler_TriggerWaitsForCurrentRun`, `TestScheduler_TriggerKeepsGridWithoutPressure`, `TestScheduler_TriggerReplacesNextRunUnderPressure`; `TestProcesses_TriggeredRunIsFullAndNamesKills`; `TestConnections_TriggeredRunIsFullAndNamesKills`; `TestKernel_LiveKillTriggersBothCollectors`; G8: `TestKernel_DroppedRunSendsNoLateTrigger` | `internal/scheduler/trigger_test.go`; `internal/procstats/procstats_test.go`; `internal/connstats/connstats_test.go`; `internal/kernevents/kernevents_test.go` |
| AC3 | `TestKernel_StormCoalescesWithinFifteenSeconds`, `TestKernel_StormNamesAtMostSixtyFourKills`, `TestKernel_KillsOfOneRunShareOneTrigger` (three live kills in one run without a trigger in the last 15 s: one pair naming all three, sent before the run returns; 70 in one run: 64 named, 6 counted); `TestScheduler_TriggerMergesKillsUpToSixtyFour` | `internal/kernevents/kernevents_test.go`; `internal/scheduler/trigger_test.go` |
| AC4 | `TestParseKill_IgnoresOtherLines`; `TestKernel_OnlyKernelTransportWithFacilityZero` | `internal/kernevents/kill_test.go`; `internal/kernevents/kernevents_test.go` |
| AC5 | `TestParseKill_HostileNames`, `TestParseKill_MissingFields` (also: an `anon-rss` or `oom_score_adj` token that appears twice leaves that field absent, the event stays), `TestParseKill_AllKillForms`, `TestParseKill_PIDs` (`2147483647` accepted; `0`, `2147483648`, `4294967295`, 11 digits refused) | `internal/kernevents/kill_test.go` |
| AC6 | `TestKernel_ResumesAtSavedPositionAcrossBoots`, `TestKernel_ReadsSetAsideFilesAfterUncleanShutdown`, `TestKernel_JournaldRestartStartsNewSeqnumID`, `TestKernel_LostRunRereadsEntries`, `TestKernel_LateOrOtherBootKillsGetNoSnapshot`, `TestKernel_ReversedTimeGapStillMovesPosition` (a span whose missed-messages entry lies between two kernel entries of the same µs and one whose clock steps back: each gap ends 1 µs after its start, every returned record passes `wire.CheckRecord`, the next run's `PreviousLost` is false and the position moves past the span, the span is not returned again), `TestKernel_RefusedRecordBecomesGap` (a record forced to fail the check by swapping the package variable `checkRecord`, not in a parallel test: left out, a gap in its place, the position moves on), `TestKernel_PositionNeverMovesBackInTime` (after a file without a head time or an older set-aside file of another seqnum ID is read last, the next runs return no record of the files already read and the saved position is the last entry of the newest file with a head time; an online file of another seqnum ID whose head time lies before the position's, a clock set back, is still read; a file of another seqnum ID without a head time cut by the 256-kill limit is continued, not read again from its head; after a dropped run these files are read again exactly as before), `TestKernel_RereadCappedAtThree` (four dropped runs in a row: three re-reads, then one gap over the span and reading after it; a dropped run that read nothing does not count; a dropped give-up run gives up the same span again without its kills) | `internal/kernevents/kernevents_test.go` |
| AC7 | `TestKernel_LostPositionRecordsGapAndContinues`, `TestKernel_UndecodableEntryRecordsGap`, `TestKernel_MonotonicStampOutOfRangeIsUnreadable` (`_SOURCE_MONOTONIC_TIMESTAMP` empty, 21 digits, `18446744073709551616`, `9223372036854776` µs, with a sign, a space or a line feed; `__MONOTONIC_TIMESTAMP` above `math.MaxInt64 / 1000`: each a gap, no event, never a negative `kernel_time_ns`; the boundary `9223372036854775` µs in both fields is accepted and gives an event), `TestKernel_RealtimeOutOfRangeIsUnreadable` (`__REALTIME_TIMESTAMP` 0, `253402300800000000` and `1<<62` on a kill entry and on the entries a missed-messages gap and a give-up span would take their bounds from: each such entry unreadable, a gap from readable times only, every returned record passes `wire.CheckRecord`, the next run's `PreviousLost` is false and the position moves on; `253402300799999999` accepted; and, through the unexported hook, a gap still refused becomes `[run.Start − 1 µs, run.Start)`), `TestKernel_HangingJournalLosesOnlyItsSample`, `TestKernel_PollWorkIndependentOfEarlierEntries`, `TestKernel_TimeBudgetEachRunGetsFurther`, `TestKernel_TornOrMalformedArchivedFileRecordsGap` (a set-aside file cut inside an entry and an archived file with a malformed entry in the middle: one gap each from the last readable kernel entry before the break to the header's tail time, the next file is read, the position moves past the file, the next run records no further gap), `TestKernel_UnsupportedFileRecordsGap` (an archived file with signature `NOTAJRNL`: one gap `[committed position's time, run.Start)` per process, none at the next runs; an unsupported online file: the same first gap, then one gap `[end of the last one, run.Start)` at the first run at least 600 s later), `TestKernel_ReadErrorRetriedThenGivenUp` (a read error in an archived file of the position's seqnum ID: the files after it are not read and no gap at the first two runs, at the third run in a row failing at the same position the file is given up with the gap of a malformed file and reading goes on; a file that opens again after one failing run loses nothing); `TestFile_RefusesUnsupportedLayout`, `TestFile_BoundsEveryOffset`, `TestFile_TornTailIsEndOfFile` (also: `Cursor.Torn` is true after the cut, false at the normal end of a list), `TestFile_FieldsBeyondCapAreCounted` (an entry with 17 `SYSLOG_FACILITY` items: 16 in `Fields`, `Omitted` 1; one with 17 compressed items likewise); in G5 `TestKernel_UndecodableEntryRecordsGap` also covers a kernel entry with a compressed item and one with `Omitted` above 0 (each a gap, no event), and `TestKernel_ReadsSetAsideFilesAfterUncleanShutdown` a file of another seqnum ID without a header head time (read after the others, its kill returned); G8: `TestKernel_VacuumedPositionRecordsGap` | `internal/kernevents/kernevents_test.go`; `internal/journal/journal_test.go` |
| AC8 | `TestKernel_UnreadableJournalLogsOnceAndResumes` (also: `Collect` returns a nil error and no record on every run while the journal is unreadable) | `internal/kernevents/kernevents_test.go` |
| AC9 | `TestBoot_NewBootRecordsPreviousBootAndUptime` (also: the state then holds `Boot.Uptime` equal to the first field of `/proc/uptime`, 5,000,000 s in the harness); review round 1: `TestSaver_EverySaveRefreshesUptime` (an urgent save after more than 10 s and less than 50 s writes the uptime `/proc/uptime` shows at that save; also without any heartbeat since the boot was stored) | `internal/kernevents/boot_test.go`; `internal/agentstate/agentstate_test.go` |
| AC10 | `TestBoot_SameBootRecordsNothing`, `TestBoot_FirstStartWithoutPrevious`, `TestBoot_WithoutStateEveryStart`; `TestOpen_StatusAndWarnings` | `internal/kernevents/boot_test.go`; `internal/agentstate/agentstate_test.go` |
| AC11 | `TestBoot_BadBootIDRecordsNothing`; G8: `TestReadBootID` | `internal/kernevents/boot_test.go`; `internal/procstats/procstats_test.go` |
| AC12 | `TestBackups_StartWithinFifteenSeconds`, `TestBackups_EndAfterSixtySeconds`, `TestBackups_ShortPauseKeepsOneRun`, `TestBackups_SingleSightingGivesStartAndEnd`, `TestBackups_CountsWithoutKeepingProcesses`, `TestBackups_LostRunIsDecidedAgain` (G6: a run with `PreviousLost` after a dropped start returns the start again while the process runs, and nothing with no open run once it ended; after a dropped end, the end again with the original `last_seen` and the `gone_by` of the last kept check; after a dropped first check after a restart that ended the stored run, the end again with `gone_by` of the repeating check); G8: `TestBackups_MalformedBootIDFailsTheRun` | `internal/backups/backups_test.go` |
| AC13 | `TestBackups_RestartContinuesRun`, `TestBackups_RunEndedAcrossStopOrReboot` | `internal/backups/backups_test.go` |
| AC14 | `TestLoadAgent_BackupProcesses`; `TestBackups_RulesAreLiteral`; `TestJobs_BackupsOnlyWithRules` | `internal/config/agent_test.go`; `internal/backups/backups_test.go`; `internal/agent/agent_test.go` |
| AC15 | `TestBackupRun_Validate`; `TestSnapshotTrigger_Validate`; `TestKernelEvent_Validate_KillIdentity`; `TestEncodeBatchGolden`; `TestDecoder_FieldErrors` (new cases); C#: `BackupRunValidateRules`, `SnapshotTriggerValidateRules`, `KillRefValidateRules`, `PayloadValidationOomKillIdentity`, `WireContractDecodesGoldenBatch`, `QueryRecordsAcceptsBackupRun`; G8: decoder case `S41 oom_kill boot_id too long` in `testdata/wire/decoder-cases.json` (both decoders), the 37-byte case in `TestKernelEvent_Validate_KillIdentity` and `PayloadValidationOomKillIdentity` | `internal/model/backup_test.go`, `trigger_test.go`, `kernel_test.go`; `internal/wire/golden_test.go`, `decode_test.go`; `tests/Vandox.Core.Tests/BackupRunTests.cs`, `SnapshotTriggerTests.cs`, `KillRefTests.cs`, `PayloadValidationTests.cs`, `WireContractTests.cs`; `tests/Vandox.Storage.Tests/SqliteStoreQueryTests.cs`; `testdata/wire/decoder-cases.json` |
| AC16 | `TestFile_MemoryDoesNotGrowWithJournalSize`; `TestKernel_BacklogMemoryIsBounded` | `internal/journal/journal_test.go`; `internal/kernevents/kernevents_test.go` |
| AC17 | `TestSave_AtomicAndOwnerOnly`, `TestSaver_WriteRates`, `TestOpen_CorruptOrForeignFileIsMissing`; review round 1: `TestOpen_InvalidOpenRunIsMissing` | `internal/agentstate/agentstate_test.go` |
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

*Reader contract (G4, Lead decision after the Dev's report).* `Entry` holds the stamps as stored (`RealtimeUS`, `MonotonicUS`) next to
the converted ones: `Realtime` is the zero time when the stamp is 0 or at least 253402300800000000, `Monotonic` is zero above
`math.MaxInt64 / 1000` µs; the collector decides from the raw fields (*Timestamps*). A failure of the underlying reader other than
EOF is `ErrRead` (`errors.Is` also finds the reader's error). At most 16 fields are kept per entry (wanted names and compressed items);
every further wanted or compressed item is counted in `Entry.Omitted`, never dropped silently. A compressed item is
`Field{Compressed: true}` with an empty name and value. `Seek` and `Next` re-read the list's entry count from its data object, so a
cursor follows a growing online file. A torn tail: in an archived or set-aside file `Next` ends the list (`false, nil`), and `Seek` and
`Lookup` do not fail on it; in an online file `Next` returns `ErrMalformed` and leaves the cursor where it was, so the read is repeated
after `Refresh`. A header head or tail time outside the range of *Timestamps* is the zero time in `Header`. `Cursor.Torn` (G5, Lead
decision) reports that the list ended at such a cut, so the collector can tell a torn tail from the normal end of a list.

**Kernel collector** (`internal/kernevents`, name `kernel`). First run: the boot check (below). Each run: commit the position kept in
memory by the previous run when `run.PreviousLost` is false, else drop it and read again from the committed position (same identities;
kills already triggered are remembered by identity, the last 64, and not triggered again). Because the collector returns only records
that pass the scheduler's check (*Record check* below), `run.PreviousLost` means a dropped result (timeout, skip, error, sink error),
never a refused record. *Re-read cap:* a counter of re-reads of the same committed position in a row (reset when a run is kept or the
committed position moves); a dropped run that read no entry (its in-memory position equals the committed one) does not count. At the
fourth dropped run in a row, the next run reads nothing of the span from the committed position to the dropped run's in-memory
position: it returns a gap over it (cause `unknown`, from the committed position's time to the time of the last readable entry read, else the
run's start, bounds as in *Gaps*) and reads on after the span; the committed position moves past it with the usual commit at the following run, so while this
run is dropped too, the next one gives up the same span again (its gap again, never its kills); the give-up run starts from the
dropped run's in-memory reading state.

*Reading state* (G5, Lead decision). What a run reaches — the position, the drift window (*Time*), the set of archived and
set-aside files of other seqnum IDs read to their end in this process (by file ID, at most 1,024) and, per file without a head time, the last seqnum read
there — is kept in memory and committed as one unit at the start of the next run; a re-read starts from the committed copy. Only the
position goes into the state file. Per run the files are read in this order: (1) the files of the position's seqnum ID in
head-seqnum order, from the position (a file whose tail seqnum is not after the position's is skipped); (2) the archived and
set-aside files of other seqnum IDs whose header head time is not before the position's time, in head-time order (ties by file
name), from their heads; (3) the archived and set-aside files of other seqnum IDs without a header head time, in file-name order
(Security N9), from their heads or from the seqnum the reading state holds for them; (4) the online file when its seqnum ID is
another, from its head, whatever its head time (the newest file; a clock set back, E10, must not hide it). A file of steps 2 and 3
in the set of files read to their end is not read again. The position is `(seqnum ID, seqnum, realtime)` of the last entry read in
a file of step 1 or 4, or of a file of step 2 whose head time is not before the in-memory position's time at the moment step 2
reaches that file (G5 follow-up, Lead decision: a step-2 file older than where reading has got, such as the fixture's set-aside
file of 20 Sep read after the files of the position's ID, is read but leaves the position where it was, here `(ID1, 157,
1792657800000000)`; otherwise the position would leave the seqnum ID whose online file still grows, and step 2 would then skip
that ID's files for their earlier head time). A file without a head time never becomes the position, and the position's realtime
never moves back (the later of the old and the entry's time). A saved seqnum ID without any file gives a gap from the position's time to the first entry after it,
and reading goes on there; no saved position starts at the newest file's tail. *Vacuumed position* (G8 decision, finding 1): when
files of the position's seqnum ID remain but none holds the position (no file with `HeadSeqnum ≤ pos.seq ≤ TailSeqnum`) and one
starts after it, the entries between are gone: one gap from the position's time to the header head time of the first such file (by
head seqnum; without a readable head time, to the next readable kernel entry, else the run's start), recorded once per position (a
marker in the reading state, so a re-read gives the same gap once and a later run without a new entry none), and reading goes on in
that file. Seqnums are not dense (fact 23), so the rule compares no seqnum differences except one: when the first such file's head
seqnum is exactly `pos.seq + 1`, nothing lies between and no gap is recorded (review round 1, N1; this replaces the Dev's guard
`pos.seq == 0`, so a position `(ID, 0)` saved from an empty online file gives the gap when the remaining files start above 1). It may
record a gap where the vacuumed file held no kernel entry after the position. A run that has already recorded, for a file it met
for the first time, the refused-file gap of *Failures* (b) or (a) (from the committed position's time to `run.Start`) records no
vacuumed-position gap and sets the marker as if it had: that gap covers the span, and a file whose header cannot be read may be the
one that holds the position (review round 1 follow-up, Lead decision; `TestKernel_OpenFailuresAtScanTime` keeps one gap). A refused
file met in an earlier run does not suppress the gap. Residual (Security delta note, step 9): after the clock was set back, vacuumed
entries stamped after `run.Start` lie outside that refused-file gap, which `newGap` may shrink to 1 µs; the loss stays recorded at
the position (Agent area, *Residuals*).

A run stops after 1 s of reading (half the timeout,
checked between entries) or 256 kills, whichever first, and returns what it read. An entry counts only with `_TRANSPORT=kernel` and
exactly one `SYSLOG_FACILITY` item whose value is exactly `0`; its `MESSAGE` goes to `parseKill`. A kernel entry with a compressed
item (its name is unknown, it could be `MESSAGE` or a second `SYSLOG_FACILITY`) or with `Entry.Omitted` above 0 is unreadable (a gap
as for an undecodable entry), never a kill. `ErrRead` is logged as the fixed reason `read failed`,
`ErrUnsupported` as `unsupported layout`, `ErrMalformed` as `malformed file`. Abandoned runs never touch the store:
only the start of the next run commits.

*Failures* (G5, Lead decision). `Collect` returns a nil error in every case: an unreadable journal is the warning of *Gaps* and,
where something is lost, a gap of the collector, never a scheduler gap. (a) No machine ID, no journal directory, or a listing that
fails: the run stops there with what it read before, no gap, retried at every run (E4: nothing is lost while the journal holds it).
A file that no longer exists is skipped (vacuumed: the lost-ID rule and the vacuumed-position rule of *Reading state* cover it). An
`open` (or `stat`) of a listed file that fails is handled as `ErrRead` in (d) (G8 decision, as built and pinned by
`TestKernel_OpenFailuresAtScanTime`): the run stops at that file without a gap; at the third run in a row failing at the same file
and position the file is given up, with the gap of (b) when its header could not be read (once per process; no repeated 600-s gap,
which (b) keeps for a file the reader refuses), else as in (c). (b) A file that `Open` refuses (`ErrUnsupported`, or `ErrMalformed` on its header): it cannot be ordered or
read; one gap `[committed position's time, else run.Start, run.Start)` the first time this process meets it (by file name), and the
other files are read on; while the online file is refused, a further gap `[end of the last one, run.Start)` at the first run at
least 600 s after it, so a journal the reader cannot read stays covered by gaps (record 0119). (c) A torn tail (`Cursor.Torn`) or
`ErrMalformed` from `Lookup`, `Seek` or `Next` in an archived or set-aside file: that file's list ends there, with a gap from the last readable
kernel entry's time read in it (else the committed position's) to the header's tail time when that is readable and later, else to
the next readable kernel entry read after it in this run, else the run's start (a torn tail whose header tail time is not later
gives no gap); the position moves to `(its seqnum ID, the later of the last seqnum read and the header's tail seqnum, the later of
the position's time and the header's tail time)`, so the file is not read again. (d) `ErrRead` in any file, or `ErrMalformed` in the
online file: the run stops at that file as in (a); the third run in a row that fails at the same position (the reader's "three polls on the same entry") gives the file up as in
(c) (for the online file the position moves to the header's tail seqnum after `Refresh`), so no damaged file holds the position
for ever (B6).

*Record check.* Before returning, `checkRecords` copies each record as the scheduler's `stamp` does (`Origin` agent, `Seq`
`math.MaxInt64`, `CapturedAt` set) and calls `wire.CheckRecord` through the package variable `checkRecord` (the hook tests swap); a record it refuses is left out and replaced by a gap over the time of
its entry (from the previous kernel entry's time, or the committed position's, to that entry's time, bounds as in *Gaps*). No log line (the gap records the loss;
spec *Logs* adds none). `checkRecords` runs the same check on every gap it builds or gets (also the give-up gap); a gap still refused is
replaced by the fallback gap `[run.Start − 1 µs, run.Start)`, so the returned records pass the check in every case (B7).

*Timestamps.* `__MONOTONIC_TIMESTAMP` is read from the entry object as an unsigned 64-bit number of µs, `_SOURCE_MONOTONIC_TIMESTAMP`
from its item as 1 to 20 ASCII digits `0`-`9`, nothing else (no sign, space or line feed), parsed into a `uint64` with an overflow check;
each value is turned into ns only when it is at most `math.MaxInt64 / 1000` µs. `__REALTIME_TIMESTAMP` (unsigned 64-bit µs) is used only
when it is above 0 and below 253402300800000000 µs (1 Jan 10000 UTC, the first value `time.Time.MarshalJSON` refuses); the same range
applies to the header's head and tail times, and one outside it counts as absent. Any other value makes the entry unreadable (a gap as
for an undecodable entry), never a negative or wrapped duration or a time the wire format refuses.

*Time.* `lag = max(0, __MONOTONIC_TIMESTAMP − _SOURCE_MONOTONIC_TIMESTAMP)` of the entry (a source stamp above the receipt stamp
counts as 0). The *drift window* holds the `(__MONOTONIC_TIMESTAMP, lag)` of the last 64 counted kernel entries (kernel transport,
exactly one facility `0`, readable stamps, a source stamp present) of one boot in reading order; it is part of the reading state,
so it is kept across runs, restored on a re-read, emptied when an entry of another boot is read and empty after a restart.
`drift` = the smallest `lag` among the entry itself and the window's entries whose `__MONOTONIC_TIMESTAMP` lies in
`[its own − 600 s, its own]` (`driftSpan`; G5, Lead decision: the drift only grows, so a minimum over older lines would take the
oldest line and overstate the delay, by 5.77 s for the 2 Oct `pzstd` kill read from the boot's start; within 600 s the drift moves
by at most 20 ms at 33.5 µs/s); `delay = lag − drift`, so `0 ≤ delay ≤ lag ≤ __MONOTONIC_TIMESTAMP` and without an earlier line
the delay is 0. Capture time = `__REALTIME_TIMESTAMP − delay`: never after the receipt, never before that boot's start. An entry
without a source stamp has delay 0. Live: entry boot ID equals `/proc/sys/kernel/random/boot_id` and
`uptime − __MONOTONIC_TIMESTAMP + delay ≤ 60 s` (a negative age counts as 0; `uptime` from `/proc/uptime` read by
`agentstate.ReadUptime`; unreadable: not live; fact 20, unverified, fails closed after a suspend). Identity: entry boot ID, the raw
`_SOURCE_MONOTONIC_TIMESTAMP` in ns (else `__MONOTONIC_TIMESTAMP`), victim PID.

*Triggers.* Times are `run.Start`. The live kills a run reads that were not triggered before (the last 64 identities) are
collected; a kill counts as triggered (its identity remembered) only when it is queued below, never while it is read (G8 decision).
A run whose context has ended when it finishes (its deadline passed: the scheduler abandons or drops its result) queues and sends
nothing, so an abandoned run that returns late never sends a trigger; its kills are judged live again when the next run reads them
again. Otherwise, before the run returns, when no trigger was sent at a `run.Start` less than 15 s ago, one `Trigger` for `processes` and
one for `connections` names the kept kills and this run's (oldest first, at most 64, the rest counted in `KillsOmitted`); otherwise
they are kept (at most 64, then counted) and sent so at the first run whose `run.Start` is at least 15 s after the last trigger's
(G5, Lead decision: kills of one run share one pair; spec S2 and S3).

*Gaps* (source and collector `kernel`, cause `unknown`): a lost saved seqnum ID (above); a vacuumed position (*Reading state*); an unreadable entry (from the last readable entry's time of the `_TRANSPORT=kernel` list, any facility, else the committed
position's time, to the next such entry's time, or to the run's start); a file the reader refuses, a torn tail, a malformed or
given-up file (*Failures*); a missed-messages entry with
`_TRANSPORT=driver` (from the last kernel entry before it to the next one after it); a refused record (*Record check*); a given-up span
(*Re-read cap*). Every gap is valid by construction, `newGap`: when `To` is not after `From` (two entries in the same µs, or a clock set
back between them, spec E10), `To = From + 1 µs`; `From` and `To` are UTC. Gap bounds and give-up spans take only the times of readable entries (*Timestamps*), the
committed position's time (strictly decoded from the state file), a header's head time or the run's start, never the time of an unreadable entry. Logs: warning `kernel messages not readable` (`err`:
`journal: <online|archived>: <fixed reason>`, or, before any file is read, one of the fixed texts `journal: machine ID not
readable`, `journal: no journal directory`, `journal: directory not readable`; G8 decision, as built), info `kernel messages
readable again`, warning `boot ID not readable`.

**Kill parser** (`parseKill`). Prefix exactly one of `Out of memory: Killed process `, `Memory cgroup out of memory: Killed process `,
`Out of memory (oom_kill_allocating_task): Killed process `; a PID of 1 to 10 digits from 1 to 2147483647 (`int32`, the type of `victim_pid`; anything else is no kill); ` (`; the comm up to the
**last** `) total-vm:`; the tail split at `, ` and ` ` into tokens, of which only `anon-rss:<1-20 digits>kB` (times 1,024, overflow
checked) and `oom_score_adj:<-?1-4 digits>` (−1000 to 1000), are used; a token of either name that appears twice leaves that field absent (the event stays). A line
without `) total-vm:` after the comm is no kill. Command: `procstats.CleanText(comm, 1024)`,
`(unnamed)` when empty; `message`: the message cleaned the same way at 16,384 bytes.

**Boots** (first `kernel` run). `boot_id` up to 64 bytes, a lower-case UUID and one line feed, read by `procstats.ReadBootID`, which
the backup check uses too (G8 decision, finding 5); `booted_at` from `procstats.ReadBootTime`.
State boot equal: nothing; different: `boot` event with `previous_boot_id` and `previous_uptime_ns`; none: event without them. Then (`Store.Update`, urgent) the
state holds `Boot{ID: the current boot ID, Uptime: agentstate.ReadUptime(proc) (0 when unreadable), BootedAt: booted_at}`; when the
boot is equal, the boot check leaves the state alone (the `state` job's heartbeat keeps the uptime); with an unreadable boot ID
(E9) the state's `Boot` is not touched.

**State** (`internal/agentstate`). `agent-state.json` in `spool.directory`, which the agent never creates (#42 does). Opened with
`O_RDONLY|O_NOFOLLOW|O_NONBLOCK`; the checks run on `fstat` of that descriptor: a regular file of the agent's effective UID without group
or other permission bits, at most 16 KiB; strict JSON (`version` 1, unknown keys, wrong types, invalid UUIDs or times refused). Status:
absent file in an existing directory with no other entry = first start (no warning); anything else not usable = `agent state not
available` (once). Save: remove a left-over `agent-state.json.tmp`, create it with `O_CREAT|O_EXCL|O_NOFOLLOW` mode 0600, write, `fsync`,
rename, `fsync` the directory; a failure (also a missing directory) logs `agent state not saved` (`err`: the OS error text, no path) once
until a save succeeds. The `state` job saves when a kill, boot or backup change waits and 10 s passed since the last save, else after
50 s (the uptime heartbeat; a position-only change waits for it). Every save first sets `Boot.Uptime` (when a boot is stored) from
`agentstate.ReadUptime` before the state is copied, so the saved uptime is never more than a minute old (review round 1, B1). The
decode also checks the open run's own rules (review round 1, N2): `started_at` and `last_seen` non-zero, `last_seen` not before
`started_at`, `max_processes` at least 1, and every time of the file before 1 Jan 10000 UTC; a file that breaks one is not usable,
as a corrupt one. The spool (#38) must count and delete only its own files.

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
The boot ID comes from `procstats.ReadBootID`; one that cannot be read or is not a lower-case UUID fails the run (`backups: procstats:
boot_id: ...`, a scheduler gap), so no record and no open run with a malformed boot ID exists (G8 decision, finding 5). A process `CommandReader.Process` cannot read, ended or not, is skipped like an ended one, with no log line and no gap (G6, Lead
decision: the `processes` collector already warns `processes not read` for the same processes; a backup process unreadable throughout
is not recognized, one that becomes unreadable during a run can end it early). `gone_by` lives in the collector's memory only, not in
`agentstate.OpenRun`: after a restart the first check without a match is the gone-by time (S12), also when a check before the stop had
already seen none, so `gone_by` stays at or after the real end. *Lost runs* (G6, Lead decision): each run first takes a copy of the
open run in the state, `gone_by` and whether the first check was done; when `run.PreviousLost` is true, the run first restores these
from the copy the previous run took (the store with `Update(true, …)`, only when the stored run differs), so a start, an end or a
`last_seen` whose records were dropped (timeout, error, sink error) is decided again by this check: a start is written again while a
match runs, an end again with the `gone_by` of the last kept check, and the first check after a start is repeated. The scheduler's gap
covers the lost check. Residual: a state the `state` job saved between the lost run and the next one, followed by a restart, keeps the
lost run's change (record 0120, *Consequences*).

**Records (wire 1.1).** `kernel_event.oom_kill`: optional `boot_id` (UUID) and `kernel_time_ns` (≥ 0), set by the agent.
`process_snapshot`, `connection_snapshot`: optional `trigger` `{"kills":[{"boot_id","kernel_time_ns","victim_pid"}],"kills_omitted":n}`
(1 to 64 distinct kills). Kind `backup_run`, source `backups`: `phase` `start` (`boot_id`, `started_at`, `pid` > 0, `command` short text,
`processes` ≥ 1; `captured_at` = `started_at`) or `end` (`boot_id`, `started_at`, `last_seen_at` ≥ `started_at`, `gone_by` ≥
`last_seen_at`, `max_processes` ≥ 1; `captured_at` = `last_seen_at`), no field of the other phase. `oom_kill.boot_id` is checked
with the common optional-UUID rule in both languages (`required`/`too long`/`invalid characters`; a value over 36 bytes is `too long`,
as in C# and for every other UUID field; G8 decision, finding 4). Field errors use the existing reasons
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
func ReadBootID(proc fs.FS) (string, error) // G8: at most 64 bytes, a lower-case UUID and at most one line feed; errors "procstats: boot_id: <os error>" or "procstats: boot_id: not a lower-case UUID"

// internal/journal
var (
	ErrUnsupported = errors.New("journal: unsupported layout")
	ErrMalformed   = errors.New("journal: malformed file")
	ErrRead        = errors.New("journal: read failed") // a non-EOF reader failure; Unwrap gives the reader's error
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
func (c *Cursor) Torn() bool // G5: the list ended at a cut-off tail of an archived or set-aside file
type Entry struct { // not comparable with ==
	Seqnum      uint64
	Realtime    time.Time     // zero when RealtimeUS is 0 or ≥ 253402300800000000
	Monotonic   time.Duration // zero when MonotonicUS > math.MaxInt64 / 1000
	BootID      [16]byte
	Items       int
	Fields      []Field // only the names in want and compressed items, at most 16, values up to 4,096 bytes in reused buffers
	Omitted     int     // wanted or compressed items beyond the 16 kept
	RealtimeUS  uint64  // the stamp as stored
	MonotonicUS uint64  // the stamp as stored
}
type Field struct {
	Name       string // empty for a compressed item
	Value      []byte // empty for a compressed item
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
func ReadUptime(proc fs.FS) (time.Duration, bool) // G5: the existing readUptime exported, unchanged (64 bytes, first field)
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
func checkRecords(records []model.Record, at time.Time, gapFrom func(i int) time.Time) []model.Record // at = run.Start; refused → gap, refused gap → [at − 1 µs, at)
func realtimeOK(us uint64) bool                                                  // 0 < us < 253402300800000000
func parseMicros(b []byte) (time.Duration, bool)                                 // 1-20 ASCII digits, overflow checked, ≤ MaxInt64/1000 µs
const maxRereads = 3
const driftSpan = 600 * time.Second          // G5: the drift window's span before an entry
const refusedGapEvery = 600 * time.Second    // G5: the spacing of the further gaps while the online file is refused
var checkRecord = wire.CheckRecord           // G5: the record check checkRecords calls; tests swap it (never in a parallel test)

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

- `docs/areas/agent.md`: new *Kernel events* (with the commit rule, the re-read cap of three and, next to it, its trade-off: a sink error lasting more than
  four runs, later a full spool (#38), gives up the span's kills as a gap even when the sink recovers soon after; the record check, the
  time ranges and the gap bounds), *Boots*, *Backup runs*, *Agent state*; additions to *Scheduling* (extra runs),
  *Processes*, *Connections*, *Memory pressure mode*, *Rights*, *Spool and sending* (the spool leaves the state file alone), *Status*,
  *Related decisions*, *Implementation*; residuals: the backup default (`pzstd` only), the filter proves that the kernel wrote the line,
  not that the OOM killer did (N4), `COMPACT` journals are not read (covered by gaps while refused); `/proc/uptime` and the
  journal's monotonic clock are assumed equal (fact 20: after a suspend every kill reads as late and gets no snapshots); after a
  restart, files of another seqnum ID without a header head time are read again from their heads (their kills return with the same
  identities), and a refused file gives its gap again once per agent start.
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
| `.squad/project.md` | G8: area 10, remove the repeated "and the systemd variables it reads from the environment slice it is given" so the sentence reads once | Dev |
| `deploy/agent/README.md` | G8: a journal that cannot be read records no gap; the collector logs `kernel messages not readable` once and resumes at the saved position | Dev |
| `docs/areas/agent.md` | G8: *Position* gets the vacuumed-position gap (the residual goes), *Extra snapshots* the late-trigger rule, *Backup runs* the boot ID rule | Lead |
| `docs/areas/wire-format.md` | G8: none (the UUID rule already stands) | Lead |
| `.squad/project.md` | Step 9: area 12, the logging sentences of the kernel event and backup collectors and the state file, and records 0119, 0120 (text in the Lead's step-9 result) | Dev |
| `docs/areas/agent.md` | Step 9: *Kernel events* residuals for the clock set back under the refused-file/vacuum rule and for the kill-line forms built from the source | Lead |

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
  `newGap` makes every gap valid, monotonic stamps are 1 to 20 digits with an overflow check, a real-time stamp outside
  `(0, 253402300800000000)` µs makes its entry unreadable, gap bounds use readable times only, a gap still refused falls back to
  `[run.Start − 1 µs, run.Start)`, and re-reads stop after three.

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

- Row 10: confirmed, now fact 10. The optional measurement (facility of a `<0>` write) was planned with fact 20's scratch measurement; with no journald in the session it is dropped (fact 10 settles the rule from the kernel source) and fact 20 moves to the server (#42).
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
- B7 (an out-of-range `__REALTIME_TIMESTAMP` still froze the position), round 3: accepted as the Lead's decision under the exception
  for a defect Security measured and supplied the complete fix for (no scope or criterion change: AC6 and AC7 already demand it).
  *Timestamps* bounds the real-time stamp and the header times, *Gaps* takes bounds from readable times only, *Record check* checks
  its gaps too with the fallback `[run.Start − 1 µs, run.Start)`; test `TestKernel_RealtimeOutOfRangeIsUnreadable`. One Security delta
  confirmation follows.
- N7: accepted (boundary `9223372036854775` µs accepted in `TestKernel_MonotonicStampOutOfRangeIsUnreadable`). N8: accepted (the
  trade-off of the re-read cap in the Agent area, *Areas*; record 0120, *Consequences*).
- N5: accepted (*Memory bound*: about 13 KiB per kill record, about 3.3 MiB for 256). N6: accepted (exact-match exceptions `sshd`,
  `apport`, `kernel`, `systemd-journald` in `TestFixture_HoldsNoRealPrivateValue`).
- N9 (a file whose header head time is absent could be skipped silently), Security delta round: accepted, option "read it after the
  others, from its head" (Lead decision in G4): the reader reports a zero `HeadRealtime`, the collector reads such a file of another
  seqnum ID after the files with a head time, in file-name order (*Kernel collector*); pinned in
  `TestKernel_ReadsSetAsideFilesAfterUncleanShutdown`. A gap instead would lose kills the file holds.
- G4 deviations (Lead decision): the reader contract (*Reader contract*) adds raw stamps, `ErrRead`, a cap of 16 kept fields and
  compressed items without a name. The cap as first built dropped further items silently, so a second `SYSLOG_FACILITY` item beyond it
  could have let an entry pass the exactly-one-facility guard; amended to fail closed: `Entry.Omitted` counts them and the collector
  treats such an entry, and any kernel entry with a compressed item, as unreadable. Security confirms this in its diff review.
- G5 questions of the Tester before the implementation (Lead decision): (1) the drift baseline takes only lines received within
  600 s before the entry (*Time*), since a minimum over the last 64 lines of a quiet boot took the oldest one; (2) the position is
  taken only from files with a head time and the online file, the reading state (position, drift window, files read to their end,
  cursors of files without a head time) is committed as one unit, and the online file is always read (*Reading state*), so a file of
  another seqnum ID can neither move the position back in time nor hide the online file; (3) the record-check hook is the package
  variable `checkRecord`; (4) the Tester's assumptions on the drift window across runs, gap bounds from the neighbouring entries of
  the `_TRANSPORT=kernel` list, the give-up gap, `run.Start` as the clock of the 15 s, the count of four dropped runs and the comm up
  to `) total-vm:` are confirmed; several live kills of one run share one trigger (amended); (5) `Collect` never returns an error,
  PIDs are `int32`, a duplicated token leaves its field absent, the gaps of a refused, torn, malformed or failing file and the
  stored uptime are fixed (*Failures*, *Kill parser*, *Boots*), and a read error can no longer hold the position for ever; the
  collector tells a torn tail through the new `Cursor.Torn`; (6) fact 20 is not measurable here and stays unverified, checked on the
  server in #42, with the residual in record 0120. Security confirms (2), (5) and `Cursor.Torn` in its diff review.
- G6 Dev and Tester questions (Lead decision): (1) `TestBackups_LogsHoldNoProcessText` expected no record at the first check after a
  restart without a match, against S12 and *Backup check*; the test is wrong and keeps process 100 running at that check (Tester), the
  collector stays as built. (2a) an unreadable process is skipped silently, as an ended one (*Backup check*; no code change).
  (2b) `gone_by` stays in memory; S12 already names the first check after the start as the gone-by time (no change). (2c) the
  collector restores its copy of the open run on `run.PreviousLost` (*Lost runs*; Dev, test `TestBackups_LostRunIsDecidedAgain` by
  the Tester; record 0120, option 10). Options rejected: commit the open run only at the next kept run, as the kernel collector does
  (a restart then loses up to one more `last_seen` and gives a second start within a wider window, against AC13); accept the loss
  (a dropped end leaves a run without an end for ever).
- G8 findings of the Lead's documentation step (Lead decision, all fixed in this feature, no criterion changed): (1) a vacuumed
  position with later files of the same seqnum ID skipped entries without a gap, against E7 and AC7: the *Vacuumed position* rule
  (*Reading state*), Dev, test `TestKernel_VacuumedPositionRecordsGap`; (2) a failing `open` is handled as `ErrRead`, as built and
  pinned (*Failures* (a) rewritten); (3) the three fixed warning texts before any file is read are taken into *Gaps*, as built;
  (4) Go refused an `oom_kill.boot_id` over 36 bytes as `invalid characters`, C# as `too long`: Go follows the common UUID rule, pinned
  by a shared decoder case; (5) the backup check took the boot ID unchecked, so a malformed one would have reached the record and the
  state file, whose strict decode then refuses the whole state: one `procstats.ReadBootID` for both collectors; (6) two sentences of
  the Dev's documents are corrected (*Documentation updates*); (7) an abandoned kernel run that returned late still sent its trigger,
  and kills were remembered as triggered while being read, so a re-read could not trigger them: kills are remembered only when queued,
  and a run whose context ended queues nothing (*Triggers*), test `TestKernel_DroppedRunSendsNoLateTrigger`. Options rejected: (1) a
  documented residual (a silent loss against the guarantee *No gap unless recorded*); (7) a residual in record 0120 (the fix is a few
  lines in two functions). Security confirms (1), (5) and (7) in its diff review.
- Review round 1 (Lead decision): B1 (Reviewer, blocking: an urgent save wrote the uptime of the last heartbeat, so
  `previous_uptime_ns` could be more than a minute short, against S4, Q6 and AC9): accepted; every save refreshes the stored uptime
  (*State*), Dev in `Saver.prepare`, test `TestSaver_EverySaveRefreshesUptime` (AC9 row). Security N1 (the vacuumed-position rule skipped
  every position with seqnum 0): accepted, narrowed to "no gap only when the next file's head seqnum is `pos.seq + 1`"
  (*Reading state*), test case in `TestKernel_VacuumedPositionRecordsGap`; option rejected: state the exception as a residual (a silent
  loss of up to the entries before the first remaining file). N2 (the open run's own rules not checked on decode): accepted, fixed
  now (*State*), test `TestOpen_InvalidOpenRunIsMissing`; option rejected: a follow-up issue (the fix is a few lines in `decode`).
  N3 (the journal files are opened through `os.DirFS`, which follows a symbolic link in the journal directory): accepted as a residual,
  since the directory belongs to root and only root (or journald) can place a link there, and root can change the journal anyway;
  Agent area *Kernel events*. N4: no change (Security rated it harmless).
- Security delta note (clock set back under the refused-file/vacuum rule) and step-9 decisions (Lead, `approve-pr`): the note is
  taken as a residual line in the Agent area (*Kernel events*, *Residuals*) and in *Reading state*; the rule stays (a second gap
  over the same position would only re-state a loss the refused-file gap already records). Security notes N1 to N4 of the diff
  review are all closed (N1 narrowed, N2 fixed, N3 residual, N4 no change). Deviations confirmed against the plan and the area
  document: the header of every listed file read at every run, no header cache (B4 bounds the work per entry; per file it is one
  `open` and 256 bytes, at most 1,024 files, Agent area *Where it reads*); the online file's `malformed file` retried without a log
  line until it is given up (*Failures* (d), the reader's "three polls"); the boot event returned again until a run's result is kept
  (*No gap unless recorded*; Agent area *Boots*); the per-file cursor kept between runs (B4); `Entry.Omitted` (G4 decision); the
  fixed warning texts before any file is read (G8 decision 3); the test harness numbering seqnums from 1 as journald does (needed by
  the `pos.seq + 1` rule); the Dev's `.squad/project.md` and `.squad/stack.md` edits (*Documentation updates*) except that area 12
  lacks the sentences for the new collectors, which the plan assigned: Dev adds them verbatim (step-9 condition). Facts 12, 18 and 20
  stay unverified and are named in the PR with their residuals; 18 and 20 are checked on the server in #42. The 60-s end of a backup
  run is pinned exactly: the Tester changes `TestBackups_EndAfterSixtySeconds` to require the end at the run 60 s after the last
  sighting and none at the run after it (step-9 condition). The Sonar gate runs on the pull request; its findings are worked there.

## Out of scope / follow-ups

As the spec's *Out of scope*. Follow-up issues: "[Agent] Read journal files of the COMPACT layout" (systemd 252 and later; record
0119); "[Agent] Default backup rules from Plesk's captured backup processes" (after #45; record 0120); comments on #42 for the
server checks of facts 18 and 20.
