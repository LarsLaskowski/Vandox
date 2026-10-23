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

The feature is specified in [spec.md](spec.md) (S1–S10, E1–E23, AC1–AC18); this plan builds it.

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | The agent has no log collector; the only journal reader is the kernel collector's, which looks entries up through the data hash of `_TRANSPORT=kernel` | `internal/kernevents/files.go`, `internal/journal/list.go` (`Lookup`, `Seek` over a data object's entry list); `internal/agent/agent.go` `jobs` | confirmed |
| 2 | `journal.ListFiles` lists only `system.journal`, `system@*.journal`, `system@*.journal~` | `internal/journal/dir.go:21`, `isJournalName` | confirmed |
| 3 | The reader reads at most 4,096 bytes of a payload and cuts a longer one silently; it keeps at most 16 wanted or compressed items per entry and never decompresses (a compressed item is kept as `Field{Compressed: true}` without a name) | `internal/journal/journal.go:73-78` (`maxPayload`, `maxKept`), `internal/journal/entry.go` `readItem` | confirmed |
| 4 | The header holds the number of entries at offset 152 and the offset of the main entry array at 176; both system files have them (72,909 and 73,265 entries) | read-only inspection of the three server files (scratch script `inspect.py`); systemd 249 `JOURNAL_FILE_FORMAT` | confirmed |
| 5 | The server journal holds the transports of 0121's *Context*; audit entries lack `PRIORITY`, some syslog entries `SYSLOG_FACILITY`, kernel entries `_PID` (209 have `SYSLOG_PID`); at most 36 items per entry, no name twice | scratch `zinspect` on the two system files (names and counts only) | confirmed |
| 6 | All 340 compressed objects are zstd frames of descriptor `0x60` (single segment, 2-byte content size, no checksum or dictionary) with one compressed block, 512 to 19,718 bytes; at most 2 per entry; longest `MESSAGE` 19,710 bytes | scratch `frames.py`, `zinspect`; aggregate in `zstd-frames.txt` | confirmed |
| 7 | `user-10000.journal` has its own seqnum ID (and a file ID different from it), no entries, and a tail seqnum (30,748,404) continuing the system counter (30,747,972) | scratch `inspect.py` (header fields only) | confirmed |
| 8 | So positions are kept per seqnum ID; within one ID the system files' seqnum ranges are disjoint | facts 4, 7; inspection | confirmed |
| 9 | Entries with user-journal content (a non-empty user file) are not in the sample; they are read like system entries (same layout, same mapping) | to verify: Dev, step 6, runs `go test ./internal/journal -run Real` with `VANDOX_REAL_JOURNAL_DIR` on a copy with a non-empty user file if the Product Manager provides one; otherwise it stays a residual named in the Agent area (the sample's user file is empty) | unverified |
| 10 | The import's journal mapping (time, `host`, `program`, `pid`, `priority`, `message`, first of a repeated field, cuts at 16,384 / 1,024 bytes with `truncated`) | `docs/areas/log-import.md:203-233`, `JournalExportParser.cs` | confirmed |
| 11 | The import's syslog mapping (`[<PRI>]`, traditional or RFC 3339 time, HOST, `PROGRAM[PID]:`, priority PRI mod 8) | `docs/areas/log-import.md:217-225`, `SyslogParser.cs` | confirmed |
| 12 | `klauspost/compress` v1.20.3 refuses a single-segment frame whose content size (its window, at least 1 KiB) exceeds `WithDecoderMaxMemory`, and a frame whose window exceeds `WithDecoderMaxWindow`, before decoding | `zstd/framedec.go:235-246` (module cache, v1.20.3) | confirmed |
| 13 | With `WithDecoderConcurrency(1)`, `WithDecoderLowmem(true)` and 4 MiB bounds, `DecodeAll` starts no goroutine; a frame of at most 4 MiB into a reused 4 MiB + 16-byte buffer allocates 4,342,984 bytes once, then about 1.2 KB; a larger stated size or window is refused before allocating; a frame without a stated size is stopped only after its output passed the bound (`framedec.go:368`), growing the buffer to 5,251,072 bytes | Security, step 3 (scratch `zmeasure`) | confirmed |
| 14 | The library's encoder with `WithSingleSegment(true)` and `WithEncoderCRC(false)` writes frames of the server's form (descriptor `0x60`, one compressed block) for 512 to 19,718 bytes of text, so the fixture writer can build real frames | to verify: Tester, step 5, `TestWrite_CompressedFramesMatchTheServerForm` compares every frame of the generated fixture files with `testdata/journal/zstd-frames.txt` (not measured) | unverified |
| 15 | Go's `time.Local` falls back to UTC without an error when `/etc/localtime` cannot be loaded | `$GOROOT/src/time/zoneinfo_unix.go:30-68` | confirmed |
| 16 | `syscall.Openat` exists on Linux in the standard library; `O_NOFOLLOW` on every element refuses a link there | `$GOROOT/src/syscall/syscall_linux.go:285`; open(2) | confirmed |
| 17 | The scheduler tells a collector only at its next run whether the previous result was kept (`Run.PreviousLost`); nothing is called when records are delivered; the agent saves no state when it stops | `internal/scheduler/runner.go:270-289` (`finish`); `internal/agent/agent.go` `Run` | confirmed |
| 18 | The state file is decoded strictly (version 1, unknown key refused) and read only up to 16 KiB today; this change raises the bound to 64 KiB | `internal/agentstate/agentstate.go:31`, `decode` | confirmed |
| 23 | `DecodeAll` takes about 3.6 ms for a 4 MiB text frame, so an entry of 512 such items (the reader's `maxItems`, `journal.go:77`) costs about 1.84 s, near the 2 s deadline; any local user can write such an entry through the native journal socket | Security's measurement in step 3 | confirmed |
| 24 | A valid RE2 pattern of 1,018 bytes (`.{0,1000}` repeated) compiles to 226,003 instructions and one match on a 16 KiB message takes 69 s | Security's measurement in step 3 (scratch `re`) | confirmed |
| 25 | With at most 400 compiled instructions (`len(prog.Inst)` of `syntax.Compile(syntax.Parse(p, syntax.Perl).Simplify())`, as `regexp.Compile` builds it) per pattern and for all drop patterns together, matching one 16 KiB line against every pattern takes at most 90 ms; at 1,000 / 4,000 it took up to 300 ms / 1.2 s | Lead, scratch `re4` (Go 1.24.7, Xeon 2.1 GHz, best of 5, 16,384 × `a` and a mixed-letter line): `(?i)\pL{0,199}y` (401) 90 ms, `\pL{0,199}y` (401) 85 ms, 4 × `\pL{0,49}…` (404) 82 ms, 8 × `\pL{0,24}…` (408) 84 ms, `[a-z]{399}y` (402) 65 ms; about 0.2 ms per instruction per line (NFA; the backtracker never takes inputs this long); Security, round 2, at the old bounds; Security to confirm in round 3 | confirmed |
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
| AC1 | `TestJournal_EntriesMapAsTheImportGolden` (the generated fixture files from `live.export`, compressed with real frames, against `testdata/journal/live-records.jsonl`), `TestJournal_ReadsUserFilesAndTheVolatileDirectory`, `TestJournal_CutsLongFieldsWithTruncated`, `TestJournal_XzOrLz4ItemIsAGap`; C#: `JournalExportParserParsesLiveFixtureAsTheGoldenRecords`; `TestEntries_SeekAllFindsTheFirstEntryAfter`, `TestEntries_NextWithDecodesCompressedItems`, `TestEntries_NextWithMarksCutFields`, `TestEntries_MoreThanFourCompressedItemsLeaveTheEntryUndecoded`, `TestEntries_CompressedObjectBeyondTheBoundIsNotRead`, `TestListAllFiles_TakesSystemAndUserFiles`, `TestReal_NextWithDecodesEveryEntry` (skips without `VANDOX_REAL_JOURNAL_DIR`), `TestJournal_PidFallsBackToSyslogPid` (decision 8), `TestEntries_NextWithCountsTheValueOnly` (decision 6) | `internal/logship/journal_test.go`, `tests/Vandox.Core.Tests/JournalExportParserTests.cs`, `internal/journal/entries_test.go`, `internal/journal/dir_test.go`, `internal/journal/real_test.go` |
| AC2 | `TestParse_ReadsTheImportsSyslogForms` (table from `testdata/logs/mail.log`, `kern.log-oom` and the forms of fact 11), `TestParse_LineWithoutHeaderIsRaw` (with the impossible and unstorable header times of decision 5), `TestClock_YearAndDaylightSavingRules`, `TestClock_SetZoneKeepsThePreviousInstant` (step 6), `TestLoadZone_RefusesWhatIsNotAZoneFile`, `TestFiles_HoldsSyslogFilesWhileTheZoneIsUnknown` | `internal/syslogline/syslogline_test.go`, `internal/syslogline/clock_test.go`, `internal/syslogline/zone_test.go`, `internal/logship/files_test.go` |
| AC3 | `TestFilter_MinPriorityAndDropRules` (table: each condition alone and together, a line without priority) | `internal/logship/filter_test.go` |
| AC4 | `TestLogs_StopAndStartShipEveryLineOnce` (real files in `t.TempDir()`, a fixture journal, `Kept`, `Store.Flush`, a new collector on the saved state), `TestRun_SavesTheStateWhenItStops`, `TestRunner_TellsTheObserverWhetherARunWasKept`, `TestStore_FlushWaitsForARunningSave`, `TestStore_FlushGivesUpWhenASaveDoesNotEnd` (decision 2, coverage mode) | `internal/logship/logship_test.go`, `internal/agent/agent_test.go`, `internal/scheduler/runner_test.go`, `internal/agentstate/agentstate_test.go` |
| AC5 | `TestLogs_UncleanEndRepeatsOnlyLinesAfterTheSavedPosition`, `TestSaver_SavesALogPositionWithinTenSeconds` (run starts a few milliseconds early on the 10 s grid) | `internal/logship/logship_test.go`, `internal/agentstate/agentstate_test.go` |
| AC6 | `TestFiles_RotationByRenameWhileRunning` (a collector run between the rename and the writer's last append to the renamed file), `TestFiles_RenamedFileIsReadUntilTheWriterHasMoved`, `TestFiles_RenamedFileSurvivesARestart`, `TestFiles_RotationSearchChecksTheOpenedFile`, `TestFiles_RotationSearchRefusesALinkInTheDirectoryPath` (after a restart), `TestFiles_RotationWhileStoppedFindsTheRenamedFile` (`.1` and `-YYYYMMDD`), `TestFiles_RotationWithoutTheOldFileIsAGap` | `internal/logship/files_test.go` |
| AC7 | `TestFiles_TruncationRestartsWithOneGap`, `TestFiles_TruncationRefilledPastThePositionIsDetected` | `internal/logship/files_test.go` |
| AC8 | `TestLogs_FirstStartShipsNothingOlder`, `TestFiles_AddedFileStartsAtItsEnd`, `TestFiles_FileThatAppearsLaterIsReadFromItsStart`, `TestJournal_NewSeqnumIDIsReadFromItsStart` | `internal/logship/logship_test.go`, `internal/logship/files_test.go`, `internal/logship/journal_test.go` |
| AC9 | `TestLogs_DroppedRunIsReadAgain`, `TestLogs_AbandonedRunNeverMovesAPosition`, `TestFiles_DroppedRunResolvesTheRepeatedHourAlike` (step 6) | `internal/logship/logship_test.go`, `internal/logship/files_test.go` |
| AC10 | `TestJournal_VacuumedPositionIsOneGap`, `TestJournal_UnreadableEntriesGiveOneGap`, `TestJournal_EntryWithoutMessageIsSkipped`, `TestJournal_DamagedFileIsGivenUpWithOneGap` (decision 3), `TestJournal_RefusedFileIsAGap` (E13, step 6), `TestEntries_NextWithDamagedItemObjectIsUndecoded`, `TestEntries_NextWithDamagedEntryKeepsTheCursor` | `internal/logship/journal_test.go`, `internal/journal/entries_test.go` |
| AC11 | `TestOpenNoLinks_RefusesALinkInAnyElement`, `TestOpenNoLinks_RefusesNonRegularFiles`, `TestFiles_UnreadableFileKeepsItsPosition`, `TestJournal_UnreadableJournalKeepsThePosition` | `internal/logship/safeopen_test.go`, `internal/logship/files_test.go`, `internal/logship/journal_test.go` |
| AC12 | `TestFiles_PartialLineWaitsForItsLineFeed`, `TestFiles_PartialLineIsShippedWhenTheWriterHasMoved` | `internal/logship/files_test.go` |
| AC13 | `TestLogs_RunBudgets` (1,000 records, 4 MiB crossing record, one second with a scripted clock, filtered lines count only against time, filtered entries that need decoding look at the clock at each entry (step 6)), `TestLogs_HeapStaysBounded` (records at every field's maximum, a journal of 100,000 entries, lines over 16 KiB), `TestDecoder_HeapStaysBoundedForTheLargestFrame` (the largest accepted frame, a frame without a stated size whose output passes the bound, a frame whose stated size is too small), `TestDecoder_RefusesFramesNotOfTheServerForm` (no stated size, not single-segment, a dictionary, a stated size over 4 MiB), `TestDecoder_NeverKeepsAGrownBuffer` | `internal/logship/logship_test.go`, `internal/zstdfield/zstdfield_test.go` |
| AC14 | `TestLoadAgent_LogShippingErrors` (every invalid option of E16; the fact-24 pattern `.{0,1000}` repeated 113 times, 1,017 bytes and 226,002 instructions; the worst-case patterns of fact 25 at the bound: `\pL{0,199}y` and `[a-z]{398}y` (401) refused, `\pL{0,198}y`, `(?i)\pL{0,198}y` (399) and `[a-z]{397}y` (400) accepted; `\pL{0,99}y` and `\pL{0,98}y` (201 + 199) accepted together, `\pL{0,99}y` twice (402) refused with the `log_shipping.drop` reason; a path with U+0000; the error holds file, line, key and index and not the value), `TestLoadAgent_LogShippingDefaults`, `TestLoadAgent_Example` (extended) | `internal/config/logshipping_test.go`, `internal/config/agent_test.go` |
| AC15 | `TestLogs_LogsHoldNoPathFieldOrMessage` (recording handler: the fixed texts only) | `internal/logship/logship_test.go` |
| AC16 | `TestRecords_RefusedRecordBecomesAGap` (coverage mode, through the seam of decision 7: the check step fed a record the wire check refuses, a negative `pid`, since the cuts make every mapped record pass), `TestLogs_FourthDroppedRunGivesUpTheSpan` (cases: four sink refusals, four timeouts, mixed) | `internal/logship/records_test.go`, `internal/logship/logship_test.go` |
| AC17 | `TestDecode_StateWithoutLogPositionsStaysUsable`, `TestEncode_FullLogPositionsFitTheBound` (16 file positions with renamed files, 256 journal positions), `TestDecode_RefusesInvalidLogPositions` | `internal/agentstate/agentstate_test.go` |
| AC18 | `TestJournal_IDWithoutPositionIsReadOnlyWhenNewer`, `TestJournal_SkippedNewerPartOfAnIDIsAGap` (a clock step back; a user journal whose first entry is older than the newest committed position and whose tail is newer), `TestJournal_SetAsideFileOfAForgottenIDIsNotReadAgain` (after a restart), `TestJournal_ManyUserIDsAreNeverReadTwice` (300 IDs with an online file, a restart), `TestJournal_TurnedOnLaterStartsAtTheTails`, `TestJournal_ReadableAfterAFirstStartStartsAtTheTails` | `internal/logship/journal_test.go` |

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
to `MaxPayload` bytes (excess not read, `Field.Cut` set), and compressed items with the zstd flag passed to `Decompress` (limit `MaxPayload + 66`, decision 6), at most `MaxCompressed` (4) per entry; the compressed object is read whole into the entry's input buffer
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
  otherwise it starts at its tail, with one gap from that time to the header tail time of the ID's latest file when that is later (the skipped part
  may hold lines newer than every position: a backwards clock step, a new user journal with older first entries); whenever no
  journal position is held (a first start, the journal off or unreadable until now) it starts at its tail without a gap (E22). The
  newest committed time is taken from the committed state at the run's start, so the rule does not depend on the order of the IDs. Vacuum and lost-ID gaps follow the kernel collector's rules (a gap from the position's time unless the first remaining entry's
  sequence number directly follows). An ID none of whose files is online and whose position is at its last file's tail is forgotten;
  its remaining files are older than the newest position, so the time rule never reads them again. Positions of at most 256 IDs are
  saved (IDs with an online file first, then those still being read, newest first); a further ID keeps its position in memory only, and
  after a restart starts at its tail by the rule above (one gap up to its tail time).
- *Mapping:* fact 10 (`log`/`source` `journal`); host and program cut to 1,024 bytes and the message to 16,384 with `CleanText`,
  `truncated` when any is cut or a field was `Cut`; entries without `MESSAGE` or with a time outside the storable range are skipped;
  `Undecoded` makes an entry a gap over it (E12); structural damage (`ErrMalformed` of an entry array or entry object) follows E23
  (decision 3).
- *Files:* each configured file is opened through `OpenNoLinks` (from `/`, every directory with `openat` and
  `O_PATH|O_DIRECTORY|O_NOFOLLOW|O_CLOEXEC`, so a search-only directory works, the file with `O_RDONLY|O_NOFOLLOW|O_NONBLOCK|O_CLOEXEC`,
  then `fstat`: a regular file with one link only, so a hard link to another file is refused as well); the open descriptor of the
  committed file is kept across runs. A different device or inode at the path is a rotation: the kept descriptor becomes the
  **renamed** file and the new one the current file. Every run reads the renamed file from its offset to its end first and ships those
  lines before the current file's; the renamed file is finished (its partial last line shipped as it is, the descriptor closed) only when
  the current file has data and the renamed file's size did not change since the previous run. Its device, inode and offset are saved
  with the file's position. After a restart, a saved renamed or current file whose inode is no longer at the path is searched in the
  directory, reached by the same link-free walk as `OpenNoLinks` (every element `O_PATH|O_DIRECTORY|O_NOFOLLOW`; a link there is
  `link refused` and the position stays, AC11), as `<base>.1` and `<base>-YYYYMMDD` with `fstatat(AT_SYMLINK_NOFOLLOW)`, opened
  with `openat(dirfd, name, O_RDONLY|O_NOFOLLOW|O_NONBLOCK|O_CLOEXEC)` and accepted only when the opened descriptor's `fstat` shows the saved
  device and inode again; not found is the E2 gap. A rotation while a renamed file is still read finishes that file first. A size below the offset or a changed SHA-256 of the first
  up to 256 bytes (the head kept with the position) is a truncation: the S6 gap and offset 0. Lines are cut at 16 KiB (rest discarded); a
  partial last line waits. Records: `log` the configured path, `source` `syslog`.
- *Filter* after mapping (AC3), compiled once from the configuration.
- *Check:* every record goes through `wire.CheckRecord` (as the scheduler stamps it) before it is returned; a refused one becomes a gap
  over its line (E19), through the seam `checkRecords` of decision 7.
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
minus half the `state` interval (5 s, `urgentInterval`) have passed since the last save (fact 28), so a committed position is saved within one state interval
(decision 1).

**Configuration.** `log_shipping` with `journal` (bool, default `true`), `min_priority` (default `info`), `files` (list of mappings
`path`, `format`), `drop` (list of mappings `log`, `program`, `priority`, `message`), checked as E16; `message` is parsed with
`regexp/syntax` (`Perl` flags), simplified and compiled, and refused with the fixed reason `must be an RE2 pattern of at most 1024 bytes
and 400 compiled instructions` when it does not compile or its program (`len(prog.Inst)`) exceeds 400 instructions, and the key
`log_shipping.drop` with `patterns must compile to at most 400 instructions together` above 400 for all rules (facts 24, 25: under
100 ms per 16 KiB line; an ordinary pattern takes about 30, so about a dozen fit); a path with U+0000 or another control character
is already refused by the decoder's clean-string rule; reasons never quote a value.

New user-visible output: the log lines above (fixed texts and attributes as listed) and the example file's comments; layout free for
the Tester, approved in step 9.

## Decisions after tests first (step 5)

1. **Urgent save threshold: 5 s, as planned.** `urgentInterval` becomes 10 s minus half the `state` interval. A threshold just
   under 10 s tolerates only that much jitter of `Run.Start` (`time.Now()` after the grid tick, fact 28), and the agent is busiest
   when it matters most (an OOM storm); with 5 s a late run still saves at the next tick. Since the `state` job runs on a 10 s grid,
   saves stay at about one per tick; a run delayed by more than 5 s can add one extra write, which is harmless. The existing tests
   `TestSaverWriteUrgentChangeWaitsTenSecondsSinceTheLastSave` and `TestSaverWriteUrgentWithinTenSecondsAfterHeartbeatSaveWaits` pin
   the old 10 s rule: the Tester renames them to `TestSaverWriteUrgentChangeWaitsFiveSecondsSinceTheLastSave` and
   `TestSaverWriteUrgentWithinFiveSecondsAfterHeartbeatSaveWaits` and asserts no save at 4 s (54 s) and a save at 5 s (55 s) after the
   last save. The Agent area's *Saving* paragraph (Lead, G4) and the comments on `urgentInterval` and `Saver.Collect` (Dev) say so.
2. **`Store.Flush` gets a seam for its wait.** The store's write lock is `saving chan struct{}` (capacity 1, made in `Open`; a save
   holds it by sending and releases it by receiving), and `Flush` waits with `select` on the send and on `flushAfter(5 * time.Second)`,
   where `var flushAfter = time.After` is unexported and swappable. On the timeout `Flush` saves nothing, logs the warning `agent
   state not saved` with `err` `agentstate: save still running` (fixed text, every time it gives up) and returns a non-nil error;
   `agent.Run` does not log that error again. Test (coverage mode, after G3): `TestStore_FlushGivesUpWhenASaveDoesNotEnd` fills
   `s.saving`, replaces `flushAfter` with a function that returns an already-fired channel (no real clock), and asserts the error,
   one warning with that `err` and the unchanged state file.
3. **Structural damage of a journal file: spec E23, the kernel collector's rule.** `NextWith` keeps `Next`'s contract: on
   `ErrMalformed` from an entry array or the entry object, the cursor stays where it was; at the cut-off end of an archived or
   set-aside file the list ends. A damaged *data object of an item* (a wrong type or size, an offset outside the file or not before
   the entry object) does not fail the entry: it counts in `Entry.Undecoded`, so the entry is E12's gap and the cursor moves on. In
   the collector an online file's `ErrMalformed` is first repeated once after re-reading the size (`Refresh`), as `kernevents`
   `fileReader.do` does; then the run stops reading that ID (records before it returned, position before the damage), and the third
   run in a row that stops at the same position (file, ID, sequence number) gives the rest up; an archived or set-aside file is given
   up at once. Giving up: one gap from the last entry read from the file (else the position's time, else the run's start) to the
   file's header tail time, the position moved to the file's tail sequence number, and reading goes on after it; the warning
   `journal not readable` with `err` `journal: <kind>: malformed file` as `kernevents` words it. Tests (now, G2):
   `TestJournal_DamagedFileIsGivenUpWithOneGap` with the cases *archived file with a damaged entry object in the middle* (lines
   before shipped, one gap to its tail time, the next file of the ID shipped), *online file*: two runs without a gap, the third gives
   the gap, and the entries of the ID's next online file are shipped, and *a damaged item object is one gap over its entry only*;
   `TestEntries_NextWithDamagedEntryKeepsTheCursor` (an entry object with a wrong type: `ErrMalformed`, and after restoring the
   byte the same cursor reads that entry), `TestEntries_NextWithDamagedItemObjectIsUndecoded`.
4. **The Tester's interpretations, confirmed against the spec, with three precisions.**
   - The renamed file is finished in the run that finds its size unchanged since the previous run (`RenamedFile.Size`, saved, so also
     after a restart) and the current file not empty: that run reads its rest, ships its partial last line as it is and closes it; an
     append after that is never read (the writer has moved, S5). Confirmed.
   - Truncation gap from the last shipped line's time to `Run.Start` of the run that notices it (S6); rotation-lost gap from the last
     shipped line's time to the capture time of the first line read from the current file, else `Run.Start` (E2). Confirmed.
     Precision: `FilePosition.LastTime` is the capture time of the last line shipped from the file, and when the collector takes a
     position without shipping a line (S7, S8, E17) it is set to that run's `Run.Start`, so it is never zero and is the start of
     both gaps when nothing was shipped (spec S6, E2 updated). The Tester adds the case *nothing shipped yet* to
     `TestFiles_TruncationRestartsWithOneGap` (now).
   - Vacuum and unreadable-entry gaps: from the realtime of the previous entry read in the ID (shipped or filtered), else the
     position's realtime, to the realtime of the next readable entry, else `Run.Start` (E3, E12). Confirmed.
   - Precision for every gap of the collector: a gap whose end is not after its start (a line dated after `Run.Start`, a clock step)
     ends 1 µs after its start, so the gap passes `model.Gap.Validate` and is still recorded.
   - Configuration error keys follow `processes.programs`: `log_shipping.min_priority`; `log_shipping.files` and `log_shipping.drop`
     for more than 16 files or 64 rules and for the patterns together, at the line of that key; `log_shipping.files[i].path` (also a
     path listed twice, at the second) and `log_shipping.files[i].format`; `log_shipping.drop[i]` for a rule without a condition;
     `log_shipping.drop[i].log`, `.priority` and `.message` for a bad value. The line is the named key's, else the element's.
     Confirmed; the tests accept these keys as written.
5. **A syslog line with an impossible or unstorable header time is shipped raw (spec E9).** The import skips it, but live a skipped
   line would be lost without a gap. `syslogline.Parse` returns `Header` false with the whole line as `Message` for 30 February, 31
   November, 29 February in a common year, hour 24, minute or second 60, and an RFC 3339 time outside the storable range; such a line
   does not change the clock's previous instant. Tests (now, G2): those cases in `TestParse_LineWithoutHeaderIsRaw`, traditional and
   RFC 3339 forms.
6. **`NextWith` semantics.** `MaxPayload` counts the bytes of the value after `NAME=` (the import's bounds are value bounds), so
   `MaxPayload` 16,384 keeps 16,384 bytes of `MESSAGE`; `Cut` is set when the value is longer, and the excess is not read. A
   compressed item is decoded with the limit `MaxPayload + 66` (journald's longest field name of 64 bytes, the `=` and one byte that
   shows a longer value) and its value cut to `MaxPayload` the same way. `NextWith` never puts a `Compressed` field into
   `Entry.Fields`: a decoded item is an ordinary field (`Compressed` false) or left out when its name is not wanted, and an item it
   cannot decode (also with `Decompress` nil) counts only in `Undecoded`. `Next` is unchanged. Test change (now, G2): in
   `TestEntries_NextWithMarksCutFields` the cut value is exactly 100 bytes, and `TestEntries_NextWithCountsTheValueOnly` reads a
   value of exactly `MaxPayload` bytes uncut and one of `MaxPayload` + 1 bytes cut, plain and compressed.
7. **Seam for AC16: confirmed.** `internal/logship/records.go`: `var checkRecord = wire.CheckRecord` (unexported, swappable) and
   `checkRecords(records []model.Record, at time.Time, gapFrom func(i int) time.Time) []model.Record`, which checks each record as
   the scheduler stamps it (origin agent, `Seq` `math.MaxInt64`, a zero `CapturedAt` set to `at`) and replaces each refused record i
   by one gap (collector `logs`, cause `unknown`, captured at `at`) from `gapFrom(i)` to the record's capture time, by the 1 µs rule
   of decision 4. The Dev adds it in G3 (task 11); the Tester writes `records_test.go` in coverage mode (task 14a).
8. **The `_PID` fallback gets a test now (G2), not in coverage mode:** the behavior is fixed by S1 and the import's `ParsePid`
   (`JournalExportParser.cs`): a `_PID` that is not 1 to 10 ASCII digits of at most 2,147,483,647 (0, 2,147,483,648, empty, a
   non-digit, 11 digits, absent) falls back to `SYSLOG_PID` by the same rule, else 0. `TestJournal_PidFallsBackToSyslogPid`
   (table: each of those `_PID` forms with `SYSLOG_PID` 42 gives 42 and with an invalid `SYSLOG_PID` gives 0; `_PID` 2,147,483,647
   is kept).

## Decisions after implementation (step 6)

Disputed tests (Tester, task 14b; both are test defects, the implementation is right):

1. `TestEntries_NextWithCountsTheValueOnly` (`internal/journal/entries_test.go:235`): the loop variable shadows the constant `limit`,
   so `limit != limit+66` always holds. Rename the loop variable (`got`), compare it with the constant (`got != limit+66`), and
   assert the number of calls: exactly one `Decompress` call in the two compressed cases, none in the two plain ones (a table field
   `decodes int`), so the check can no longer pass vacuously.
2. `givenUpOnlineFile` (`internal/logship/journal_test.go:705`): `w.rename` renames in `w.logDir`, the journal is in
   `w.jroot/<machine ID>`. Add the world helper `renameJournal(from, to string)` (`world_test.go`, renames within
   `filepath.Join(w.jroot, machineID)`, `Fatalf` with got and want on an error) and call it at line 705; nothing else changes.

The Dev's deviations:

1. **Clock checks: accepted with a correction.** `Sources.Now` is read at the run's start, after every shipped record and every
   1,000 entries or lines examined, and after every full 64 KiB chunk of a file. Correction (Dev): the clock is also looked at after
   every entry whose reading called the decoder (a counting wrapper around the decoder in `internal/logship`; no change to
   `internal/journal`). Without it 1,000 filtered entries of 4 compressed items of 4 MiB each (fact 23: 3.6 ms per frame) read
   about 14 s past the second, so any local user could force abandoned runs and E19 give-ups. Test: the subcase *filtered entries
   that need decoding look at the clock at each entry* of `TestLogs_RunBudgets` (20 entries of priority 7 with 700-byte messages, then
   three wanted ones; clock step 250 ms): the first kept run returns nothing and the saved position of the ID is above 0 and below
   20; at most 10 further `w.step()` calls (not `drain`, which stops at an empty run) return the three wanted lines and no gap.
2. **The source read first rotates from run to run: accepted.** Order within a source is kept (S4, AC4); a backlog in one source
   cannot keep the others waiting (E11). The Agent area states it (Lead, G4).
3. **`syslogline.(*Clock).SetZone`: accepted and added to *Signatures*,** with a correction (Dev): the per-file clock belongs to
   the fork (`fileState`, copied by `clone`), not to `Logs`, so a dropped or abandoned run never moves the previous instant the
   re-read starts from (today a dropped run in the repeated hour gives the re-read lines another offset, against AC9). Tests:
   `TestClock_SetZoneKeepsThePreviousInstant` (`clock_test.go`: a line in the repeated hour, `SetZone` to the same rules from a new
   `LoadLocationFromTZData`, the next line still decided against the previous instant), `TestFiles_DroppedRunResolvesTheRepeatedHourAlike`
   (`files_test.go`: a zone with daylight saving time in `localtime`, lines `02:30` and then `02:05` of the repeated hour, the run
   refused, the next run returns both lines with the times of the refused run).
4. **`FilePosition.LastTime` follows every line read, shipped or filtered: accepted.** A filtered line is not lost, so the S6 and
   E2 gaps start after it, as E12's journal gaps do; the precision of decision 4 (step 5) is replaced, and spec S6 and E2 say so.
5. **Held partial line of at most 16,896 bytes (16,384 + 512 for the header), shipped as it is at a truncation, saved offset at the
   line's start: accepted** (E6, E7; the message is cut at 16,384 after the header as the import cuts it). Held lines live in the
   committed state and up to two forks, so the spec's memory figure for the file reader becomes under 2 MiB (spec fixed).
6. **`SeekAll` returns the start when `after` is below the header head sequence number: accepted;** equal to the bisection for a
   file whose head sequence number is its first entry's, which journald sets once at the first entry, and a damaged entry before
   the position is not met.
7. **Vacuum gap only when no file of the ID holds the position and the first later file does not start at position + 1:
   accepted;** the seqnums of one ID's files have holes (fact 7), so a check between files would give false gaps. The rule may
   over-report (a gap where only user-journal numbers lay between); the Agent area says so.
8. **The newest journal position is never forgotten: accepted;** forgetting it would lower the newest time and let E22's time
   rule read the forgotten ID's files again.
9. **`oPath = 0x200000`: accepted.** It is the value of every Linux port of Go (`golang.org/x/sys` v0.48.0 `zerrors_linux_*.go`:
   only sparc64, a gccgo-only target, differs), and only `linux/amd64` is built (`ci.yml:213`, `release.yml:117`). The Dev extends
   the constant's comment with that (comment only).
10. **Rotated-file search: accepted as equivalent to N2.** The identity is checked on the opened descriptor (no window between a
    stat and the open), a non-regular candidate is opened non-blocking and closed after `fstat`, as `OpenNoLinks` does for the
    configured path; the dated names kept are at most 65,536 names of `<base>-` plus 8 digits (about 2 MiB for a short base, once
    per restart or lost rotation). It departs from N2's wording on a security control, so Security confirms it in the diff review.
11. **`Saver.Collect` and `Store.Flush` log a failed save themselves: accepted** (once per failing streak; the `Flush` timeout warning
    every time, decision 2); `agent.Run` does not log it again.
12. **A refused journal file's gap survives a dropped run (defect found here, Dev).** `Logs.refused` is updated in `Collect`, so the
    E13 gap of a dropped run is never recorded again. It moves into the fork `state` (`maps.Clone` in `clone`, committed with the
    fork), as `kernevents` keeps it (`internal/kernevents/state.go:111-133`). Test: `TestJournal_RefusedFileIsAGap` (a file shorter
    than the header named as an archived file: one gap from the newest position's time to the run's start and none in the next
    run; the online file refused: one more gap 600 s after the last; the run with the first gap refused: the next run records it).
    Spec AC10 gains E13, which AC11 named without asserting its gap.

Coverage mode (task 14a), after the two tests of decisions 2 and 7, in this order (the paths that decide whether a line is lost or
shipped twice first): (a) a file backlog across runs, `pump` stopping mid-file and `rewind` (1,500 lines: 1,000, then 500, none twice,
also with a partial last line at the stop); (b) a second rotation while the renamed file is still read (`finishRenamed`: its rest and
partial line shipped before the newer files, nothing twice); (c) a lost rotation while the new file has no line yet (`endLostGap`:
the gap ends at the run's start) and a restart whose renamed file is gone or behind a link (`findRenamed`); (d) a torn archived file
(the cut-off end gives one gap to its tail time, `Torn`); (e) `logshipConfig` in `internal/agent` (every option mapped: `journal`
false, a `min_priority`, files, a drop rule with every condition whose pattern matches); (f) `classify` and `directoryError`
(a regular file as a directory element is `missing`, a directory without search permission `permission denied`, skipped as root);
(g) the error branches of `readItemWith` and `readCompressedItem` (a compressed object beyond the file's end or over
`MaxCompressedBytes`, the input buffer growing once); (h) `decodeJournal` refusals in `internal/agentstate`; then the rest
(`New` errors, `Name`, `statOf`, `openJournal`, `tailTime`).

## Options considered

The five choices with their alternatives are in [0121](../../docs/decisions/0121-log-shipping-journal-and-syslog-files-positions-per-seqnum-id-zstd-commit-when-kept.md)
(*Options considered*).

## Affected projects and types

The packages of *Signatures* (new: `internal/zstdfield`, `internal/syslogline`, `internal/logship`; changed: `internal/journal`
with `journaltest`, `internal/scheduler`, `internal/agentstate`, `internal/config`, `internal/agent`), `go.mod`/`go.sum`
(`github.com/klauspost/compress v1.20.3`), the documentation of *Documentation updates*, the fixtures of *Approach* and the C# golden
parity test `tests/Vandox.Core.Tests/JournalExportParserTests.cs`.

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
func (c *Clock) SetZone(loc *time.Location) // keeps the previous instant (step 6, deviation 3)
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
// records.go (unexported, decision 7)
var checkRecord = wire.CheckRecord
func checkRecords(records []model.Record, at time.Time, gapFrom func(i int) time.Time) []model.Record

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
// unexported (decision 2): Store gains saving chan struct{} (capacity 1, the write lock); var flushAfter = time.After

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
	MaxPatternInsts      = 400
	MaxPatternInstsTotal = 400
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
`DefaultAgent`, `AgentKeys`, `validate`) and `internal/journal/entry.go` (`Field`, `Entry`, `readItem`), and extends
`internal/journal/dir.go`, `journal.go`, `list.go`, `journaltest/journaltest.go`, `journaltest/builder.go` and
`internal/scheduler/scheduler.go`. New files: `internal/journal/entries.go`, `internal/zstdfield/zstdfield.go`,
`internal/syslogline/syslogline.go`, `clock.go`, `zone.go`, `internal/logship/logship.go`, `journal.go`, `files.go`, `safeopen.go`,
`filter.go`, `records.go`, `positions.go`, `internal/config/logshipping.go`.

## Test files

The files of *Tests per criterion*, colocated as `foo_test.go` (`.squad/stack.md`, *Layout*); `TestWrite_CompressedFramesMatchTheServerForm`
lives in `internal/journal/journaltest/writer_test.go`.

Existing test code that calls a changed signature: `internal/agent/agent_test.go` calls `jobs` (lines 734, 784) and `newScheduler`
(lines 952, 1061, 1125); the Tester adapts them in step 5 (new return values). `internal/agentstate/agentstate_test.go`:
the Tester changes `TestSaverWriteUrgentChangeWaitsTenSecondsSinceTheLastSave` and
`TestSaverWriteUrgentWithinTenSecondsAfterHeartbeatSaveWaits` to the 5 s threshold (decision 1). `AgentKeys` and the example file are pinned by
`TestLoadAgent_Example` and the keys test in `internal/config/agent_test.go` (Tester).

Files of the change no unit test can cover: none (`OpenNoLinks` runs against real files in `t.TempDir()`).

## Areas

- `docs/areas/agent.md`: new section *Log shipping* (as the spec's *Area documents* outline; it states that the cap of 4 decoded
  items counts every compressed item, wanted or not, since the name is inside the frame, so an entry with more, such as a coredump's,
  is one gap); *Status*, *Collecting*, *Agent state*
  (format with `logs`, the path hash, `Flush` at stop), *Scheduling* (the observer), *Rights* (the collector's reads), *Gaps*, *Related
  decisions* (0121), *Implementation* (new packages, fixtures).
- `docs/areas/configuration-and-secrets.md`: the `log_shipping` options; the `spool.directory` note (log positions in the state file).
- `docs/areas/wire-format.md`: `log_line` — the agent's `log` and sources `journal` and `syslog`.
- `docs/areas/log-import.md`: *Duplicates* — shipped and imported lines of one period are stored twice.

## Documentation updates

Sweep: `grep` for `agentstate`, `position in the journal`, `the open backup run` and the collector lists; one owner each.

- `docs/areas/agent.md`, `docs/areas/configuration-and-secrets.md`, `docs/areas/wire-format.md`, `docs/areas/log-import.md` — Lead
  (docs task group); in `agent.md` also *Saving* (the urgent save after 5 s, `Flush` and its timeout warning, decisions 1 and 2).
- `docs/ARCHITECTURE.md` — Lead: the collector list (line 14), the *Components* bullet of `cmd/vandox-agent` (lines 53-61: the `logs`
  collector, the state file's log positions, the commit when kept), the `internal/` package list (line 110: `internal/zstdfield`,
  `internal/syslogline`, `internal/logship`), the records list (0121).
- `README.md` — Dev: *Agent options* rows `log_shipping.journal`, `log_shipping.min_priority`, `log_shipping.files`,
  `log_shipping.drop` (with the pattern bound); the `spool.directory` row (log positions).
- `deploy/agent/agent.yaml` — Dev: the section with every option at its default and commented examples (a `syslog` file, two drop
  rules, the pattern bound, the duplicate warning of E15).
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

- [ ] Limits on parsed input (as *Approach* sets them): journal payloads 16,384 / 1,024 bytes **before** decoding to text and the
  record cuts **after** `CleanText` (as the wire check measures); zstd frames refused **before** decoding unless single-segment with a
  stated size of at most 4 MiB and no dictionary, objects over 4 MiB not read, at most 4 decoded per entry, one fixed output buffer;
  drop patterns at most 1,024 bytes and 400 compiled instructions each and together (fact 25); file lines cut at 16 KiB raw before
  decoding; `localtime` and the state file at most 64 KiB raw; run budgets count bytes of the cleaned record text.
- [ ] Exceptions reaching a user-visible text: configuration errors (`KeyError` with fixed reasons, never the value or the pattern's
  compile error text); the collector's log lines carry only the fixed `err` texts listed in *Approach*, never an `os` error with a path
  (an `*fs.PathError` is reduced to its errno class before logging).

## Decision records

- `docs/decisions/0121-log-shipping-journal-and-syslog-files-positions-per-seqnum-id-zstd-commit-when-kept.md` (Proposed, indexed).
- `docs/decisions/0119-…` edited in place (unreleased): its last consequence names 0121.
- Record 0121 updated in this revision: the ID time rule with its gap, the pattern bound of 400 instructions, and option 7's stale
  claim that the state file's bound stays unchanged.

## Challenge

- **M1: accepted** — ID time rule, forget rule, 256 saved IDs (*Journal*; spec E22, AC18); state bound 64 KiB.
- **M2: accepted** — the renamed file is read until the writer has moved and saved with the position (*Files*; spec S5, E7, AC6, AC12).
- **m1: accepted** — urgent save due at 10 s minus half the `state` interval (fact 28, *State*).
- **m2: accepted** — dropped runs counted from `Run.PreviousLost` too; give-up rule for timeouts (*Commit*).
- **m3: accepted** — one store-level write lock; `Flush` waits at most 5 s (*State*).
- **m4: accepted in part** — no `zstd` command in the session, so no libzstd fixture frame; the Dev decodes the server's 340 frames
  (fact 26, task 14).

## Security review

- **B1: accepted, option (a)** — frame rule and fixed output buffer (*Decoder*, fact 13); heap test cases in AC13.
- **B2: accepted** — objects over 4 MiB not read, input buffer grown once (*Journal*); spec memory figure 8.5 MiB zstd path.
- **B3: accepted** — at most 4 compressed items decoded per entry (E12).
- **B4: accepted**, figures replaced by B5 — fact 24's pattern is a test case.
- **B5 (round 2): accepted** (Lead's decision after the second rejection) — 400 compiled instructions per pattern and together;
  fact 25 holds the Lead's measurement at that bound (worst 90 ms per 16 KiB line); the AC14 row tests the fact-24 pattern and the
  worst-case patterns on both sides of the bound; spec E16, *Security*, `log_shipping.drop` and 0121 carry the figure. The cost:
  about a dozen ordinary patterns fit (rules without `message` are free), named in the example file and the README row.
  **Security to confirm in round 3.**
- **N1–N3: taken** — one link required, the rotation search re-checks the opened descriptor, directories opened `O_PATH` (*Files*).
- **N4:** U+0000 in a path is refused by the decoder's clean-string rule; a test case pins it.
- **N5: taken** — task 13 and the Agent area's *Rights* name `logs` as the first user of `CAP_DAC_READ_SEARCH`.
- **N6: taken** as fact 27; no `noasm` or `nounsafe` tag.
- **N7 (round 2): taken** — an ID started at its tail whose latest file's tail time is newer than the newest committed position gives
  one gap up to that time (*Journal*; spec E22, AC18); the unsaved-ID gap after a restart is the same rule.
- **N8 (round 2): taken** — the Agent area states that the cap of 4 counts every compressed item (*Areas*); no code change.
- **N9 (round 2): taken** — the rotation search reaches its directory through the same link-free walk (*Files*, AC6 test).

## Out of scope / follow-ups

- #289 (error log formats), #290 (classifying live lines), #38 (spool; exact delivery across a crash), #44 (the volume on the server).
- A non-empty user journal (fact 9), if one becomes available.
