# Plan: Continuous log shipping in the agent

Source: Issue #37 | [spec.md](spec.md)
Status: Revised (1)
Tier: security — the change creates a guard whose purpose is to stop an attacker (configured log files are opened only through a
link-free path walk, E5, so a service user cannot make the agent ship another file), adds a decoder of hostile input and a dependency,
and extends the trusted state file.
Security areas touched: 6 Agent privileges (new reads of the journal's system and user files and of `/var/log` files with the
existing groups and `CAP_DAC_READ_SEARCH`; `deploy/agent/README.md`), 9 File writes and paths derived from external input (the state
file `agent-state.json` gains log positions; `internal/agentstate`), 10 Parsing of external input (`internal/journal` entry list and
field reading, the new zstd decoder `internal/zstdfield`, the syslog line parser `internal/syslogline`, `/etc/localtime`, the
`log_shipping` options and their RE2 patterns in `internal/config`), 12 Logging and display of external data (shipped log lines are
external text; the collector's own log lines), and a new dependency `github.com/klauspost/compress` (`go.mod`, `go.sum`).

## Problem / root cause

The feature is specified in [spec.md](spec.md) (S1–S10, E1–E21, AC1–AC17); this plan builds it.

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | The agent has no log collector; the only journal reader is the kernel collector's, which looks entries up through the data hash of `_TRANSPORT=kernel` | `internal/kernevents/files.go`, `internal/journal/list.go` (`Lookup`, `Seek` over a data object's entry list); `internal/agent/agent.go` `jobs` | confirmed |
| 2 | `journal.ListFiles` lists only `system.journal`, `system@*.journal`, `system@*.journal~` | `internal/journal/dir.go:21`, `isJournalName` | confirmed |
| 3 | The reader reads at most 4,096 bytes of a payload and cuts a longer one silently; it keeps at most 16 wanted or compressed items per entry and never decompresses (a compressed item is kept as `Field{Compressed: true}` without a name) | `internal/journal/journal.go:73-78` (`maxPayload`, `maxKept`), `internal/journal/entry.go` `readItem` | confirmed |
| 4 | The header holds the number of entries at offset 152 and the offset of the main entry array at 176; both system files have them (72,909 and 73,265 entries) | read-only inspection of the three server files (scratch script `inspect.py`); systemd 249 `JOURNAL_FILE_FORMAT` | confirmed |
| 5 | The server journal holds the transports `syslog`, `stdout`, `journal`, `kernel`, `audit` and `driver`; audit entries carry no `PRIORITY`; some syslog entries carry no `SYSLOG_FACILITY`; kernel entries carry no `_PID` (209 of them `SYSLOG_PID`); an entry has at most 36 items, no name twice | inspection of the two system files (scratch `zinspect`), field names and counts only | confirmed |
| 6 | Compressed data objects are all zstd (340: `MESSAGE`, `SYSLOG_RAW`, `_CMDLINE`); every frame has the descriptor `0x60` (single segment, 2-byte content size, no checksum, no dictionary) and exactly one compressed block; content size = window 512 to 19,718 bytes; an entry holds at most 2 compressed items; the longest `MESSAGE` is 19,710 bytes | scratch `frames.py` and `zinspect` on the two system files; aggregate in `zstd-frames.txt` (below) | confirmed |
| 7 | The user file `user-10000.journal` has a seqnum ID different from both system files, its own file ID different from its seqnum ID, no entries (offline, empty), and a tail seqnum (30,748,404) that continues the system counter (30,747,972 in the online copy) | scratch `inspect.py` (header fields only) | confirmed |
| 8 | So files of the journal directory are not one sequence of one seqnum ID: positions are kept per seqnum ID | facts 7 and 4; within one ID the archived and online system files do not overlap in sequence numbers (inspection: no common seqnums, ranges disjoint) | confirmed |
| 9 | Entries with user-journal content (a non-empty user file) are not in the sample; they are read like system entries (same layout, same mapping) | to verify: Dev, step 6, runs `go test ./internal/journal -run Real` with `VANDOX_REAL_JOURNAL_DIR` on a copy with a non-empty user file if the Product Manager provides one; otherwise it stays a residual named in the Agent area (the sample's user file is empty) | unverified |
| 10 | The import's journal mapping: time from `__REALTIME_TIMESTAMP` (> 0, within the storable range), `host` `_HOSTNAME`, `program` `SYSLOG_IDENTIFIER` else `_COMM`, `pid` `_PID` else `SYSLOG_PID` (1 to 2,147,483,647, else 0), `priority` one digit 0–7, `message` `MESSAGE`; first of a repeated field; `MESSAGE` cut at 16,384 bytes and short fields at 1,024 with `truncated` | `docs/areas/log-import.md:203-233`, `src/Vandox.Core/LogParsing/JournalExportParser.cs` | confirmed |
| 11 | The import's syslog mapping (`[<PRI>]`, traditional or RFC 3339 time, HOST, `PROGRAM[PID]:`, priority PRI mod 8) | `docs/areas/log-import.md:217-225`, `SyslogParser.cs` | confirmed |
| 12 | `klauspost/compress` v1.20.3 refuses a single-segment frame whose content size (its window, at least 1 KiB) exceeds `WithDecoderMaxMemory`, and a frame whose window exceeds `WithDecoderMaxWindow`, before decoding | `zstd/framedec.go:235-246` (module cache, v1.20.3) | confirmed |
| 13 | With `WithDecoderConcurrency(1)`, `WithDecoderLowmem(true)` and 4 MiB bounds, `DecodeAll` starts no goroutine; a single-segment frame of at most 4 MiB into a reused buffer of 4 MiB + 16 bytes allocates 4,342,984 bytes once, then about 1.2 KB; a larger stated size or window is refused before allocating; a frame without a stated size is stopped only after its output passed the bound (`framedec.go:368`), growing the buffer to 5,251,072 bytes (21 MB into a fresh buffer) | Security's measurement in step 3 (scratch `zmeasure`, v1.20.3) | confirmed |
| 14 | The library's encoder with `WithSingleSegment(true)` and `WithEncoderCRC(false)` writes frames of the server's form (descriptor `0x60`, one compressed block) for 512 to 19,718 bytes of text, so the fixture writer can build real frames | to verify: Tester, step 5, `TestWrite_CompressedFramesMatchTheServerForm` compares every frame of the generated fixture files with `testdata/journal/zstd-frames.txt` (not measured) | unverified |
| 15 | Go's `time.Local` falls back to UTC without an error when `/etc/localtime` cannot be loaded | `$GOROOT/src/time/zoneinfo_unix.go:30-68` | confirmed |
| 16 | `syscall.Openat` exists on Linux in the standard library; `O_NOFOLLOW` on every element refuses a link there | `$GOROOT/src/syscall/syscall_linux.go:285`; open(2) | confirmed |
| 17 | The scheduler tells a collector only at its next run whether the previous result was kept (`Run.PreviousLost`); nothing is called when records are delivered; the agent saves no state when it stops | `internal/scheduler/runner.go:270-289` (`finish`); `internal/agent/agent.go` `Run` | confirmed |
| 18 | The state file is decoded strictly (version 1, unknown key refused) and read only up to 16 KiB today; this change raises the bound to 64 KiB | `internal/agentstate/agentstate.go:31`, `decode` | confirmed |
| 23 | `DecodeAll` takes about 3.6 ms for a 4 MiB text frame, so an entry of 512 such items (the reader's `maxItems`, `journal.go:77`) costs about 1.84 s, near the 2 s deadline; any local user can write such an entry through the native journal socket | Security's measurement in step 3 | confirmed |
| 24 | A valid RE2 pattern of 1,018 bytes (`.{0,1000}` repeated) compiles to 226,003 instructions and one match on a 16 KiB message takes 69 s | Security's measurement in step 3 (scratch `re`) | confirmed |
| 25 | With at most 1,000 compiled instructions per pattern and 4,000 for all drop patterns, filtering one 16 KiB line stays below 100 ms | to verify: Dev, step 6, a scratch benchmark of 64 rules at the bounds on 16 KiB lines on the development host, reporting the time; if it exceeds 100 ms the Dev reports it and the Lead lowers the bounds (not measured) | unverified |
| 26 | The decoder reads every zstd frame journald (libzstd) wrote on the server | to verify: Dev, step 6, runs `TestReal_NextWithDecodesEveryEntry` with `VANDOX_REAL_JOURNAL_DIR` set to the scratchpad `serverdata/` copy: no read error, `Undecoded` 0 on all entries of the two system files; reports counts only and copies nothing (the session has no `zstd` command, so no committed libzstd frame) | unverified |
| 27 | `govulncheck` reports nothing for `github.com/klauspost/compress` v1.20.3 | to verify: Code Officer, step 7, *Analyzer gate* (govulncheck), and CI before the diff approval; Security could not run it (proxy 403) | unverified |
| 28 | The `state` job saves an urgent change only when `run.Start` minus the last save is at least 10 s, both taken from `time.Now()` on a 10 s grid, so jitter can push a save to 20 s | `internal/agentstate/agentstate.go:473`, `internal/scheduler/runner.go` `cycle` | confirmed |
| 29 | A save writes one fixed temporary path (remove, `O_EXCL` create, rename); at a stop the `state` worker can still be in it while the agent would save again | `internal/agentstate/agentstate.go:348-380`; the scheduler does not wait for an abandoned run | confirmed |
| 30 | Ubuntu's rsyslog logrotate configuration renames all files and sends `HUP` once in `postrotate` (`sharedscripts`), so the writer appends to a renamed file for a while | to verify: on the server at #42 (`/etc/logrotate.d/rsyslog`); the design keeps reading a renamed file whatever the window (not checked here) | unverified |
| 19 | Records 0110, 0119 and 0120 are unreleased (not in `v0.1.0`), so 0119 is edited in place | `git ls-remote --tags origin` (only `v0.1.0`); `git tag --contains` of each record's adding commit returns nothing | confirmed |
| 20 | `procstats.CleanText` makes text valid UTF-8 and cuts it at a character boundary to a byte limit, so the cut text measures as the wire check measures | `internal/procstats/cmdline.go:31-60`; `docs/areas/wire-format.md` *Text* | confirmed |
| 21 | The server writes about 0.5 journal lines per second (72,909 entries in 38 hours), most at `info` | inspection (header times, counts) | confirmed |

## Acceptance criteria

The criteria are the spec's AC1–AC17 ([spec.md](spec.md)).

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `TestJournal_EntriesMapAsTheImportGolden` (the generated fixture files from `live.export`, compressed with real frames, against `testdata/journal/live-records.jsonl`), `TestJournal_ReadsUserFilesAndTheVolatileDirectory`, `TestJournal_CutsLongFieldsWithTruncated`, `TestJournal_XzOrLz4ItemIsAGap`; C#: `JournalExportParserParsesLiveFixtureAsTheGoldenRecords`; `TestEntries_SeekAllFindsTheFirstEntryAfter`, `TestEntries_NextWithDecodesCompressedItems`, `TestEntries_NextWithMarksCutFields`, `TestEntries_MoreThanFourCompressedItemsLeaveTheEntryUndecoded`, `TestEntries_CompressedObjectBeyondTheBoundIsNotRead`, `TestListAllFiles_TakesSystemAndUserFiles`, `TestReal_NextWithDecodesEveryEntry` (skips without `VANDOX_REAL_JOURNAL_DIR`) | `internal/logship/journal_test.go`, `tests/Vandox.Core.Tests/JournalExportParserTests.cs`, `internal/journal/entries_test.go`, `internal/journal/dir_test.go`, `internal/journal/real_test.go` |
| AC2 | `TestParse_ReadsTheImportsSyslogForms` (table from `testdata/logs/mail.log`, `kern.log-oom` and the forms of fact 11), `TestParse_LineWithoutHeaderIsRaw`, `TestClock_YearAndDaylightSavingRules`, `TestLoadZone_RefusesWhatIsNotAZoneFile`, `TestFiles_HoldsSyslogFilesWhileTheZoneIsUnknown` | `internal/syslogline/syslogline_test.go`, `internal/syslogline/clock_test.go`, `internal/syslogline/zone_test.go`, `internal/logship/files_test.go` |
| AC3 | `TestFilter_MinPriorityAndDropRules` (table: each condition alone and together, a line without priority) | `internal/logship/filter_test.go` |
| AC4 | `TestLogs_StopAndStartShipEveryLineOnce` (real files in `t.TempDir()`, a fixture journal, `Kept`, `Store.Flush`, a new collector on the saved state), `TestRun_SavesTheStateWhenItStops`, `TestRunner_TellsTheObserverWhetherARunWasKept`, `TestStore_FlushWaitsForARunningSave` | `internal/logship/logship_test.go`, `internal/agent/agent_test.go`, `internal/scheduler/runner_test.go`, `internal/agentstate/agentstate_test.go` |
| AC5 | `TestLogs_UncleanEndRepeatsOnlyLinesAfterTheSavedPosition`, `TestSaver_SavesALogPositionWithinTenSeconds` (run starts a few milliseconds early on the 10 s grid) | `internal/logship/logship_test.go`, `internal/agentstate/agentstate_test.go` |
| AC6 | `TestFiles_RotationByRenameWhileRunning` (a collector run between the rename and the writer's last append to the renamed file), `TestFiles_RenamedFileIsReadUntilTheWriterHasMoved`, `TestFiles_RenamedFileSurvivesARestart`, `TestFiles_RotationSearchChecksTheOpenedFile`, `TestFiles_RotationWhileStoppedFindsTheRenamedFile` (`.1` and `-YYYYMMDD`), `TestFiles_RotationWithoutTheOldFileIsAGap` | `internal/logship/files_test.go` |
| AC7 | `TestFiles_TruncationRestartsWithOneGap`, `TestFiles_TruncationRefilledPastThePositionIsDetected` | `internal/logship/files_test.go` |
| AC8 | `TestLogs_FirstStartShipsNothingOlder`, `TestFiles_AddedFileStartsAtItsEnd`, `TestFiles_FileThatAppearsLaterIsReadFromItsStart`, `TestJournal_NewSeqnumIDIsReadFromItsStart` | `internal/logship/logship_test.go`, `internal/logship/files_test.go`, `internal/logship/journal_test.go` |
| AC9 | `TestLogs_DroppedRunIsReadAgain`, `TestLogs_AbandonedRunNeverMovesAPosition` | `internal/logship/logship_test.go` |
| AC10 | `TestJournal_VacuumedPositionIsOneGap`, `TestJournal_UnreadableEntriesGiveOneGap`, `TestJournal_EntryWithoutMessageIsSkipped` | `internal/logship/journal_test.go` |
| AC11 | `TestOpenNoLinks_RefusesALinkInAnyElement`, `TestOpenNoLinks_RefusesNonRegularFiles`, `TestFiles_UnreadableFileKeepsItsPosition`, `TestJournal_UnreadableJournalKeepsThePosition` | `internal/logship/safeopen_test.go`, `internal/logship/files_test.go`, `internal/logship/journal_test.go` |
| AC12 | `TestFiles_PartialLineWaitsForItsLineFeed`, `TestFiles_PartialLineIsShippedWhenTheWriterHasMoved` | `internal/logship/files_test.go` |
| AC13 | `TestLogs_RunBudgets` (1,000 records, 4 MiB crossing record, one second with a scripted clock, filtered lines count only against time), `TestLogs_HeapStaysBounded` (records at every field's maximum, a journal of 100,000 entries, lines over 16 KiB), `TestDecoder_HeapStaysBoundedForTheLargestFrame` (the largest accepted frame, a frame without a stated size whose output passes the bound, a frame whose stated size is too small), `TestDecoder_RefusesFramesNotOfTheServerForm` (no stated size, not single-segment, a dictionary, a stated size over 4 MiB), `TestDecoder_NeverKeepsAGrownBuffer` | `internal/logship/logship_test.go`, `internal/zstdfield/zstdfield_test.go` |
| AC14 | `TestLoadAgent_LogShippingErrors` (every invalid option of E16, the 1,018-byte pattern of fact 24, patterns over 4,000 instructions together, a path with U+0000; the error holds file, line, key and index and not the value), `TestLoadAgent_LogShippingDefaults`, `TestLoadAgent_Example` (extended) | `internal/config/logshipping_test.go`, `internal/config/agent_test.go` |
| AC15 | `TestLogs_LogsHoldNoPathFieldOrMessage` (recording handler: the fixed texts only) | `internal/logship/logship_test.go` |
| AC16 | `TestRecords_RefusedRecordBecomesAGap` (the check step fed a record the wire check refuses, a negative `pid`, since the cuts make every mapped record pass), `TestLogs_FourthDroppedRunGivesUpTheSpan` (cases: four sink refusals, four timeouts, mixed) | `internal/logship/records_test.go`, `internal/logship/logship_test.go` |
| AC17 | `TestDecode_StateWithoutLogPositionsStaysUsable`, `TestEncode_FullLogPositionsFitTheBound` (16 file positions with renamed files, 256 journal positions), `TestDecode_RefusesInvalidLogPositions` | `internal/agentstate/agentstate_test.go` |
| AC18 | `TestJournal_IDWithoutPositionIsReadOnlyWhenNewer`, `TestJournal_SetAsideFileOfAForgottenIDIsNotReadAgain` (after a restart), `TestJournal_ManyUserIDsAreNeverReadTwice` (300 IDs with an online file, a restart), `TestJournal_TurnedOnLaterStartsAtTheTails`, `TestJournal_ReadableAfterAFirstStartStartsAtTheTails` | `internal/logship/journal_test.go` |

## Approach

**Fixtures (real samples, anonymized by the Lead).** The server files stay outside the repository. From field-name shapes only the Lead
generated `live.export` (21 entries covering facts 5 and 6 and a 20,000-byte message; every value generated; 454 fields compared with
the real values: no equality, no contained host, ID, IPv4 or mail address, no shared 8-byte prefix) and `zstd-frames.txt` (frame
headers only). The Tester copies both from the scratchpad `fixtures/` to `testdata/journal/` (SHA-256 of `live.export`
`1f50cea662d1d65a7d21f3b0e9a2d1171da7a3b998b0ae9eed4524de9c38fdfe`) and writes `testdata/journal/live-records.jsonl` (the expected
records by the import's mapping, read by the Go and the C# test). Binary journal files are generated at test time by `journaltest.Write`
with `Options.Compress` (fact 14); a user file is written with a seqnum ID of its own as `user-1000.journal`. Syslog lines come from
`testdata/logs/mail.log`, `kern.log-oom` and hand-written forms; rotation, truncation and links use real files in `t.TempDir()`.

**Journal (`internal/journal`).** `ListAllFiles` adds `user-<digits>.journal`, `user-<digits>@*.journal` and `user-<digits>@*.journal~`
(digits 1 to 10, no other character). `SeekAll` positions a cursor on the file's main entry array (header offsets 152 and 176) at the
first entry after a sequence number, by the same bisection as `Seek`; `NextWith` reads an entry with `ReadOptions`: wanted payloads up
to `MaxPayload` bytes (excess not read, `Field.Cut` set), and compressed items with the zstd flag passed to `Decompress` (output up to
`MaxPayload` plus the name), at most `MaxCompressed` (4) per entry; the compressed object is read whole into the entry's input buffer
(reused, grown once up to `MaxCompressedBytes`, 4 MiB); a larger object, a fifth compressed item (the entry is not decoded further), XZ or
LZ4 items and failed decodings count in `Entry.Undecoded`. `Next` keeps its behavior (kernel
collector unchanged).

**Decoder (`internal/zstdfield`).** One `zstd.Decoder` per collector with `WithDecoderConcurrency(1)`, `WithDecoderLowmem(true)`,
`WithDecoderMaxMemory(4 MiB)`, `WithDecoderMaxWindow(4 MiB)`. Before `DecodeAll`, `Decompress` reads the frame header itself and refuses
(`ErrFrameRefused`) every frame that is not single-segment, has no stated content size, a stated size over `MaxFrameBytes` or a
dictionary (every real frame is `0x60`, fact 6). It decodes into one output buffer allocated once at `MaxFrameBytes` + 128 KiB + 16
bytes (one block of overshoot for a frame whose stated size is too small, plus the library's over-allocation), never grown: a result
whose backing array is not that buffer is an error and is dropped. It copies at most the caller's limit into `dst`. Any error makes the
item undecoded (E12). Memory: about 4.3 MiB output plus at most 4 MiB input, within the spec's 8.5 MiB.

**Syslog (`internal/syslogline`).** A hand-written, linear port of the import's syslog line rules (fact 11) on bytes, the year and
daylight-saving rules of E10 through `Clock` (one per file, keeps the previous line's instant), and `LoadZone` reading at most 64 KiB of
`localtime` from an `fs.FS` (production `os.DirFS("/etc")`) into `time.LoadLocationFromTZData`; an error never falls back to UTC.

**Collector (`internal/logship`, name `logs`, interval 2 s, timeout 2 s, after `kernel` in the job list).** A run works on a fork of the
committed reading state and stops at 1,000 records, at the record that crosses 4 MiB of text, after one second of reading or at its
deadline.
- *Journal:* locate the directory as the kernel collector does (persistent, else volatile), list with `ListAllFiles`, group the files by
  seqnum ID; per ID with a position, merge its files by sequence number from the position (`SeekAll`). An ID without a position is
  read from its start only when the header head time of its earliest file is after the newest committed journal position's time;
  otherwise, and whenever no journal position is held (a first start, the journal off or unreadable until now), it starts at its tail
  (E22). Vacuum and lost-ID gaps follow the kernel collector's rules (a gap from the position's time unless the first remaining entry's
  sequence number directly follows). An ID none of whose files is online and whose position is at its last file's tail is forgotten;
  its remaining files are older than the newest position, so the time rule never reads them again. Positions of at most 256 IDs are
  saved (IDs with an online file first, then those still being read, newest first); a further ID keeps its position in memory only, and
  after a restart starts at its tail with one gap from the newest saved position's time to the run's start.
- *Mapping:* fact 10 (`log`/`source` `journal`); host and program cut to 1,024 bytes and the message to 16,384 with `CleanText`,
  `truncated` when any is cut or a field was `Cut`; entries without `MESSAGE` or with a time outside the storable range are skipped;
  `Undecoded` or a read error of one entry is a gap over it.
- *Files:* each configured file is opened through `OpenNoLinks` (from `/`, every directory with `openat` and
  `O_PATH|O_DIRECTORY|O_NOFOLLOW|O_CLOEXEC`, so a search-only directory works, the file with `O_RDONLY|O_NOFOLLOW|O_NONBLOCK|O_CLOEXEC`,
  then `fstat`: a regular file with one link only, so a hard link to another file is refused as well); the open descriptor of the
  committed file is kept across runs. A different device or inode at the path is a rotation: the kept descriptor becomes the
  **renamed** file and the new one the current file. Every run reads the renamed file from its offset to its end first and ships those
  lines before the current file's; the renamed file is finished (its partial last line shipped as it is, the descriptor closed) only when
  the current file has data and the renamed file's size did not change since the previous run. Its device, inode and offset are saved
  with the file's position. After a restart, a saved renamed or current file whose inode is no longer at the path is searched in the
  directory (opened `O_RDONLY|O_DIRECTORY|O_NOFOLLOW`) as `<base>.1` and `<base>-YYYYMMDD` with `fstatat(AT_SYMLINK_NOFOLLOW)`, opened
  with `openat(dirfd, name, O_RDONLY|O_NOFOLLOW|O_NONBLOCK)` and accepted only when the opened descriptor's `fstat` shows the saved
  device and inode again; not found is the E2 gap. A rotation while a renamed file is still read finishes that file first. A size below the offset or a changed SHA-256 of the first
  up to 256 bytes (the head kept with the position) is a truncation: the S6 gap and offset 0. Lines are cut at 16 KiB (rest discarded); a
  partial last line waits. Records: `log` the configured path, `source` `syslog`.
- *Filter* after mapping (AC3), compiled once from the configuration.
- *Check:* every record goes through `wire.CheckRecord` (as the scheduler stamps it) before it is returned; a refused one becomes a gap
  over its line (E19).
- *Commit:* `Kept(run, true)` commits the fork of that run and calls `store.Update(true, …)`. Dropped runs are counted from
  `Kept(run, false)` and from `Run.PreviousLost` at the next `Collect` (a timeout or skip calls no `Kept`), as `kernevents` does. A fork
  an abandoned worker may still write is never committed. At the fourth dropped run in a row from one committed state the run gives up:
  the span up to where the last fork that came back got (its positions committed, the E19 gap from the committed time to its last
  line's time, none of the span's lines returned); when no fork came back (all timed out), one gap from the committed time to the run's
  start, once per committed state, and the positions stay.
- *Logs:* warnings `journal not readable` (`err` a fixed `journal: …` reason), `log file not readable` (`file` the index, `err` one of
  `missing`, `not a regular file`, `link refused` (a symbolic or a hard link), `permission denied`, `read failed`), `time zone not readable`, and their `… readable
  again` infos; never a path, a field value or a message.

**Scheduler.** `Observer` is optional: `runner.finish` calls `Kept(run, kept)` after the delivery of a run that finished in time (an
error or panic: `false`); an abandoned or skipped run calls nothing.

**State.** `State.Logs` with up to 256 journal positions (`seqnum_id`, `seqnum`, `realtime`) and up to 16 file positions (`path_sha256`
64 lower-case hex digits, `device`, `inode`, `offset` ≥ 0, `head_sha256`, `head_len` 0 to 256, `last_time`, optional `renamed` with
`device`, `inode`, `offset`, `size`); decoded strictly; a file without `logs` stays valid; the read bound becomes 64 KiB (256 journal
positions of about 110 bytes and 16 file positions of about 400 bytes fit with room). One store-level write lock covers encode, write
and the dirty/sequence update in `Saver.Collect` and `Store.Flush`; `Flush` waits at most 5 s for it (else the warning `agent state not
saved`). `agent.Run` calls `Flush` after the scheduler stopped. The urgent save tolerates the grid's jitter: due when at least 10 s
minus half the `state` interval have passed since the last save (fact 28), so a committed position is saved within one state interval.

**Configuration.** `log_shipping` with `journal` (bool, default `true`), `min_priority` (default `info`), `files` (list of mappings
`path`, `format`), `drop` (list of mappings `log`, `program`, `priority`, `message`), checked as E16; `message` is parsed with
`regexp/syntax` (`Perl` flags), simplified and compiled, and refused with the fixed reason `must be an RE2 pattern of at most 1024 bytes
and 1000 compiled instructions` when it does not compile or its program exceeds 1,000 instructions, and the key `log_shipping.drop` with
`patterns must compile to at most 4000 instructions together` above 4,000 (facts 24, 25); a path with U+0000 or another control character
is already refused by the decoder's clean-string rule; reasons never quote a value.

New user-visible output: the log lines above (fixed texts and attributes as listed) and the example file's comments; layout free for
the Tester, approved in step 9.

## Options considered

The five choices with their alternatives are in [0121](../../docs/decisions/0121-log-shipping-journal-and-syslog-files-positions-per-seqnum-id-zstd-commit-when-kept.md)
(*Options considered*).

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Go agent | `internal/journal/dir.go` | `ListAllFiles`, user file names |
| Go agent | `internal/journal/entries.go` (new), `journal.go`, `entry.go`, `list.go` | `SeekAll`, `NextWith`, `ReadOptions`, `Decompressor`, `Field.Cut`, `Entry.Undecoded`, header entry count and array offset |
| Go agent | `internal/journal/journaltest/journaltest.go`, `builder.go` | `Options.Compress` (real frames) |
| Go agent | `internal/zstdfield/zstdfield.go` (new) | bounded zstd decoder |
| Go agent | `internal/syslogline/syslogline.go`, `clock.go`, `zone.go` (new) | syslog line parser, year and DST rules, zone loading |
| Go agent | `internal/logship/logship.go`, `journal.go`, `files.go`, `safeopen.go`, `filter.go`, `records.go`, `positions.go` (new) | the `logs` collector |
| Go agent | `internal/scheduler/scheduler.go`, `runner.go` | `Observer`, called in `finish` |
| Go agent | `internal/agentstate/agentstate.go` | `State.Logs`, `LogPositions`, `FilePosition`, `Flush` |
| Go agent | `internal/config/agent.go`, `logshipping.go` (new) | `log_shipping` section |
| Go agent | `internal/agent/agent.go` | job `logs`, `Options.Logs`, `Flush` at stop |
| Go agent | `go.mod`, `go.sum` | `github.com/klauspost/compress v1.20.3` |
| Deploy | `deploy/agent/agent.yaml`, `deploy/agent/README.md` | options; rights of the collector |
| Test data | `testdata/journal/live.export`, `zstd-frames.txt`, `live-records.jsonl` | fixtures (Tester) |
| .NET tests | `tests/Vandox.Core.Tests/JournalExportParserTests.cs` | golden parity test |

## Signatures (for the Dev's skeleton)

```go
// internal/journal
func ListAllFiles(dir fs.FS) ([]string, error)
type Decompressor interface {
	// Decompress appends the decoded frame src to dst[:0], at most limit bytes; an error makes the item undecoded.
	Decompress(dst, src []byte, limit int) ([]byte, error)
}
type ReadOptions struct {
	MaxPayload         int          // bytes of a wanted payload kept, after decompression
	Decompress         Decompressor // nil: compressed items stay undecoded
	MaxCompressed      int          // compressed items decoded per entry; one more leaves the entry undecoded
	MaxCompressedBytes int          // largest compressed object read; a larger one is undecoded without being read
}
func (f *File) SeekAll(after uint64) (Cursor, error)
func (f *File) NextWith(c *Cursor, want []string, e *Entry, opt *ReadOptions) (bool, error)
// Field gains: Cut bool; Entry gains: Undecoded int

// internal/journal/journaltest
// Options gains: Compress func(payload []byte) ([]byte, error) // nil keeps the raw-block frames

// internal/zstdfield
const MaxFrameBytes = 4 << 20
var ErrFrameRefused = errors.New("zstdfield: frame refused")
type Decoder struct{ /* unexported: the library decoder and the fixed output buffer */ }
func New() (*Decoder, error)
func (d *Decoder) Decompress(dst, src []byte, limit int) ([]byte, error)

// internal/syslogline
type Line struct {
	Time     time.Time // UTC; zero when the line has no header time
	Host     string
	Program  string
	PID      int32
	Priority *uint8
	Message  string
	Header   bool // the line has a syslog header
}
type Clock struct{ /* unexported: location, previous instant */ }
func NewClock(loc *time.Location) *Clock
func Parse(line []byte, clock *Clock, now time.Time) Line
func LoadZone(etc fs.FS) (*time.Location, error)

// internal/logship
const Name = "logs"
type Sources struct {
	Etc     fs.FS                            // machine-id and localtime; nil: os.DirFS("/etc")
	Journal []fs.FS                          // persistent, then volatile journal roots; nil: /var/log/journal, /run/log/journal
	Open    func(path string) (*os.File, error) // nil: OpenNoLinks
	Now     func() time.Time                 // nil: time.Now
}
type FileSpec struct{ Path, Format string }
type Rule struct {
	Log      string         // "" any, "journal" or a configured path
	Program  string         // "" any
	Priority *uint8         // nil any
	Message  *regexp.Regexp // nil any
}
type Config struct {
	Journal     bool
	MinPriority uint8
	Files       []FileSpec
	Drop        []Rule
}
func New(src Sources, cfg Config, store *agentstate.Store, log *slog.Logger) (*Logs, error)
func (l *Logs) Name() string
func (l *Logs) Collect(ctx context.Context, run scheduler.Run) ([]model.Record, error)
func (l *Logs) Kept(run scheduler.Run, kept bool)
func OpenNoLinks(path string) (*os.File, error)

// internal/scheduler
type Observer interface {
	// Kept reports whether the records of run, which finished in time, were kept.
	Kept(run Run, kept bool)
}

// internal/agentstate
type LogPositions struct {
	Journal []JournalPosition
	Files   []FilePosition
}
type FilePosition struct {
	PathSHA256 string // 64 lower-case hex digits of the SHA-256 of the configured path
	Device     uint64
	Inode      uint64
	Offset     int64
	HeadSHA256 string // of the first HeadLen bytes
	HeadLen    int    // 0 to 256
	LastTime   time.Time
	Renamed    *RenamedFile // the rotated file still being read, or nil
}
type RenamedFile struct {
	Device uint64
	Inode  uint64
	Offset int64
	Size   int64 // size seen at the last run
}
// State gains: Logs *LogPositions; maxFileSize becomes 64 KiB
const MaxLogJournals = 256
const MaxLogFiles = 16
func (s *Store) Flush(now time.Time) error

// internal/config
type LogShipping struct {
	Journal     bool       `yaml:"journal"`
	MinPriority string     `yaml:"min_priority"`
	Files       []LogFile  `yaml:"files"`
	Drop        []DropRule `yaml:"drop"`
}
type LogFile struct {
	Path   string `yaml:"path"`
	Format string `yaml:"format"`
}
type DropRule struct {
	Log      string `yaml:"log"`
	Program  string `yaml:"program"`
	Priority string `yaml:"priority"`
	Message  string `yaml:"message"`
}
// Agent gains: LogShipping LogShipping `yaml:"log_shipping"`
func PriorityLevel(name string) (uint8, bool)
const (
	MaxLogFiles     = 16
	MaxDropRules    = 64
	MaxPatternBytes      = 1024
	MaxPatternInsts      = 1000
	MaxPatternInstsTotal = 4000
)

// internal/agent
const (
	LogsInterval = 2 * time.Second
	LogsTimeout  = 2 * time.Second
)
// Options gains: Logs logship.Sources
func jobs(opts Options, cfg *config.Agent, log *slog.Logger) ([]scheduler.Job, *sysstats.System, *agentstate.Store)
func newScheduler(opts Options, cfg *config.Agent, log *slog.Logger) (*scheduler.Scheduler, *agentstate.Store, error)
```

The skeleton rewrites `internal/agent/agent.go` (`jobs`, `newScheduler`, `Run`), `internal/scheduler/runner.go` (`finish`),
`internal/agentstate/agentstate.go` (`State`, `fileState`, `decode`, `encode`, `copyState`), `internal/config/agent.go` (`Agent`,
`DefaultAgent`, `AgentKeys`, `validate`) and `internal/journal/entry.go` (`Field`, `Entry`, `readItem`); every other file listed is new.

## Test files

The files of *Tests per criterion*, colocated as `foo_test.go` (`.squad/stack.md`, *Layout*); `TestWrite_CompressedFramesMatchTheServerForm`
lives in `internal/journal/journaltest/writer_test.go`.

Existing test code that calls a changed signature: `internal/agent/agent_test.go` calls `jobs` (lines 734, 784) and `newScheduler`
(lines 952, 1061, 1125); the Tester adapts them in step 5 (new return values). `AgentKeys` and the example file are pinned by
`TestLoadAgent_Example` and the keys test in `internal/config/agent_test.go` (Tester).

Files of the change no unit test can cover: none (`OpenNoLinks` runs against real files in `t.TempDir()`).

## Areas

- `docs/areas/agent.md`: new section *Log shipping* (as the spec's *Area documents* outline); *Status*, *Collecting*, *Agent state*
  (format with `logs`, the path hash, `Flush` at stop), *Scheduling* (the observer), *Rights* (the collector's reads), *Gaps*, *Related
  decisions* (0121), *Implementation* (new packages, fixtures).
- `docs/areas/configuration-and-secrets.md`: the `log_shipping` options; the `spool.directory` note (log positions in the state file).
- `docs/areas/wire-format.md`: `log_line` — the agent's `log` and sources `journal` and `syslog`.
- `docs/areas/log-import.md`: *Duplicates* — shipped and imported lines of one period are stored twice.

## Documentation updates

Sweep: `grep` for `agentstate`, `position in the journal`, `the open backup run` and the collector lists; one owner each.

- `docs/areas/agent.md`, `docs/areas/configuration-and-secrets.md`, `docs/areas/wire-format.md`, `docs/areas/log-import.md` — Lead
  (docs task group).
- `docs/ARCHITECTURE.md` — Lead: the collector list (line 14), the *Components* bullet of `cmd/vandox-agent` (lines 53-61: the `logs`
  collector, the state file's log positions, the commit when kept), the `internal/` package list (line 110: `internal/zstdfield`,
  `internal/syslogline`, `internal/logship`), the records list (0121).
- `README.md` — Dev: *Agent options* rows `log_shipping.journal`, `log_shipping.min_priority`, `log_shipping.files`,
  `log_shipping.drop`; the `spool.directory` row (log positions).
- `deploy/agent/agent.yaml` — Dev: the section with every option at its default and commented examples (a `syslog` file, two drop
  rules, the duplicate warning of E15).
- `deploy/agent/README.md` — Dev: the `logs` collector's reads under the rights list (journal system and user files with
  `systemd-journal`, configured files with `adm` or `CAP_DAC_READ_SEARCH`, `/etc/localtime`), the state file's log positions (line 113).
- `.squad/project.md` — Dev: security areas 6, 9, 10 and 12 (the new readers, bounds and logs; record 0121), *Test doubles* `journald`
  row (`live.export`, `Options.Compress`, `zstd-frames.txt`).
- `.squad/stack.md` — Dev: *Layout* Go package list (`zstdfield`, `syslogline`, `logship`).

## Architecture check

- *No data gaps unless explicitly recorded*: every unreadable entry, vacuumed position, lost rotation, truncation, refused record and
  given-up span is a gap of collector `logs`; nothing else is dropped except by the configured filter, which is the operator's choice.
- *A hanging collector never blocks the agent*: the collector runs under the scheduler's deadline; reads honor the run's deadline
  between entries and lines; an abandoned run never commits (its `Kept` is never called).
- *Backfilled data never alerts*: unchanged (the agent sends records as before; the backend classifies).

## Security considerations

- [ ] Limits on parsed input: journal payloads 16,384 bytes (`MESSAGE`) and 1,024 (short fields) **before** decoding to text, and the
  record cuts (16,384 / 1,024 bytes) **after** making the text valid UTF-8 (`CleanText`, U+FFFD counts 3 bytes, as the wire check
  measures); zstd frames refused **before** decoding unless single-segment with a stated size of at most 4 MiB and no dictionary, compressed
objects over 4 MiB not read, at most 4 decoded per entry, output into one fixed buffer and cut to the payload bound; drop patterns at
most 1,024 bytes and 1,000 compiled instructions each, 4,000 together; file lines cut
  at 16 KiB of raw bytes **before** decoding, then cleaned and cut again; `localtime` at most 64 KiB raw; state file 64 KiB raw;
  patterns 1,024 bytes of the configuration value; run budgets count bytes of the cleaned record text.
- [ ] Exceptions reaching a user-visible text: configuration errors (`KeyError` with fixed reasons, never the value or the pattern's
  compile error text); the collector's log lines carry only the fixed `err` texts listed in *Approach*, never an `os` error with a path
  (an `*fs.PathError` is reduced to its errno class before logging).

## Decision records

- `docs/decisions/0121-log-shipping-journal-and-syslog-files-positions-per-seqnum-id-zstd-commit-when-kept.md` (Proposed, indexed).
- `docs/decisions/0119-…` edited in place (unreleased): its last consequence names 0121.
- Record 0121 updated in this revision: the ID time rule and the bound of 256, the renamed file, the frame rule, the item cap, the
  pattern bound and the 64 KiB state file.

## Challenge

- **M1: accepted.** The ID time rule, the forget rule and the bound of 256 saved IDs are in *Approach* (*Journal*), spec E22 and AC18;
  the state bound becomes 64 KiB; tests in the AC18 row.
- **M2: accepted.** The renamed file is read until the writer has moved and is saved with the position (*Approach*, *Files*;
  `RenamedFile`); spec S5, E7, AC6, AC12; the rotation test runs a cycle between the rename and the last append.
- **m1: accepted.** Urgent save due at 10 s minus half the `state` interval (fact 28, *State*); the saver test starts runs early.
- **m2: accepted.** Dropped runs counted from `Run.PreviousLost` too; give-up rule for timeouts in *Approach* (*Commit*); a case of four
  timeouts.
- **m3: accepted.** One store-level write lock; `Flush` waits at most 5 s (*State*); `TestStore_FlushWaitsForARunningSave`.
- **m4: accepted in part.** No `zstd` command in the session, so no libzstd fixture frame (rejected for that reason); the Dev decodes the
  340 journald frames of the server files instead (fact 26, task 14).

## Security review

- **B1: accepted, option (a)** — the frame rule and the fixed output buffer (*Approach*, *Decoder*); fact 13 holds the measured figures;
  the heap test adds a frame without a stated size and one with a too small stated size.
- **B2: accepted** — compressed objects over 4 MiB are not read, the input buffer is grown once and reused (*Journal*); the spec's figure
  counts it (8.5 MiB zstd path, about 18.5 MiB total).
- **B3: accepted** — at most 4 compressed items decoded per entry; a fifth leaves the entry undecoded (E12).
- **B4: accepted** — 1,000 compiled instructions per pattern, 4,000 together (*Configuration*); fact 24's pattern is a test case; fact
  25 measured by the Dev.
- **N1–N3: taken** — one link required, the rotation search re-checks the opened descriptor, directories opened `O_PATH` (*Files*).
- **N4:** U+0000 in a path is already refused by the decoder's clean-string rule; a test case pins it.
- **N5: taken** — task 13 (`.squad/project.md` area 6) and the Agent area's *Rights* name the `logs` collector as the first user of
  `CAP_DAC_READ_SEARCH`, kept to the configured files by the link-free walk.
- **N6: taken** as fact 27 (Code Officer's gate and CI); no `noasm` or `nounsafe` tag (the default build is what `govulncheck` sees).

## Out of scope / follow-ups

- #289 (error log formats), #290 (classifying live lines), #38 (spool; exact delivery across a crash), #44 (the volume on the server).
- A non-empty user journal (fact 9), if one becomes available.
