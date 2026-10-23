# Plan: Continuous log shipping in the agent

Source: Issue #37 | [spec.md](spec.md)
Status: Draft
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
| 13 | With `WithDecoderConcurrency(1)` and `WithDecoderLowmem(true)`, `DecodeAll` of a 4 MiB frame into a reused buffer peaks below 8 MiB of heap and starts no goroutine | to verify: Security reads `zstd/decoder.go` `DecodeAll` at v1.20.3 in step 3; the Tester's `TestDecoder_HeapStaysBoundedForTheLargestFrame` (step 5) measures it (not measured) | unverified |
| 14 | The library's encoder with `WithSingleSegment(true)` and `WithEncoderCRC(false)` writes frames of the server's form (descriptor `0x60`, one compressed block) for 512 to 19,718 bytes of text, so the fixture writer can build real frames | to verify: Tester, step 5, `TestWrite_CompressedFramesMatchTheServerForm` compares every frame of the generated fixture files with `testdata/journal/zstd-frames.txt` (not measured) | unverified |
| 15 | Go's `time.Local` falls back to UTC without an error when `/etc/localtime` cannot be loaded | `$GOROOT/src/time/zoneinfo_unix.go:30-68` | confirmed |
| 16 | `syscall.Openat` exists on Linux in the standard library; `O_NOFOLLOW` on every element refuses a link there | `$GOROOT/src/syscall/syscall_linux.go:285`; open(2) | confirmed |
| 17 | The scheduler tells a collector only at its next run whether the previous result was kept (`Run.PreviousLost`); nothing is called when records are delivered; the agent saves no state when it stops | `internal/scheduler/runner.go:270-289` (`finish`); `internal/agent/agent.go` `Run` | confirmed |
| 18 | The state file is decoded strictly (version 1, unknown key refused) and read only up to 16 KiB | `internal/agentstate/agentstate.go:31`, `decode` | confirmed |
| 19 | Records 0110, 0119 and 0120 are unreleased (not in `v0.1.0`), so 0119 is edited in place | `git ls-remote --tags origin` (only `v0.1.0`); `git tag --contains` of each record's adding commit returns nothing | confirmed |
| 20 | `procstats.CleanText` makes text valid UTF-8 and cuts it at a character boundary to a byte limit, so the cut text measures as the wire check measures | `internal/procstats/cmdline.go:31-60`; `docs/areas/wire-format.md` *Text* | confirmed |
| 21 | The server writes about 0.5 journal lines per second (72,909 entries in 38 hours), most at `info` | inspection (header times, counts) | confirmed |

## Acceptance criteria

The criteria are the spec's AC1–AC17 ([spec.md](spec.md)).

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `TestJournal_EntriesMapAsTheImportGolden` (the generated fixture files from `live.export`, compressed with real frames, against `testdata/journal/live-records.jsonl`), `TestJournal_ReadsUserFilesAndTheVolatileDirectory`, `TestJournal_CutsLongFieldsWithTruncated`, `TestJournal_XzOrLz4ItemIsAGap`; C#: `JournalExportParserParsesLiveFixtureAsTheGoldenRecords`; `TestEntries_SeekAllFindsTheFirstEntryAfter`, `TestEntries_NextWithDecodesCompressedItems`, `TestEntries_NextWithMarksCutFields`, `TestListAllFiles_TakesSystemAndUserFiles` | `internal/logship/journal_test.go`, `tests/Vandox.Core.Tests/JournalExportParserTests.cs`, `internal/journal/entries_test.go`, `internal/journal/dir_test.go` |
| AC2 | `TestParse_ReadsTheImportsSyslogForms` (table from `testdata/logs/mail.log`, `kern.log-oom` and the forms of fact 11), `TestParse_LineWithoutHeaderIsRaw`, `TestClock_YearAndDaylightSavingRules`, `TestLoadZone_RefusesWhatIsNotAZoneFile`, `TestFiles_HoldsSyslogFilesWhileTheZoneIsUnknown` | `internal/syslogline/syslogline_test.go`, `internal/syslogline/clock_test.go`, `internal/syslogline/zone_test.go`, `internal/logship/files_test.go` |
| AC3 | `TestFilter_MinPriorityAndDropRules` (table: each condition alone and together, a line without priority) | `internal/logship/filter_test.go` |
| AC4 | `TestLogs_StopAndStartShipEveryLineOnce` (real files in `t.TempDir()`, a fixture journal, `Kept`, `Store.Flush`, a new collector on the saved state), `TestRun_SavesTheStateWhenItStops`, `TestRunner_TellsTheObserverWhetherARunWasKept` | `internal/logship/logship_test.go`, `internal/agent/agent_test.go`, `internal/scheduler/runner_test.go` |
| AC5 | `TestLogs_UncleanEndRepeatsOnlyLinesAfterTheSavedPosition`, `TestSaver_SavesALogPositionWithinTenSeconds` | `internal/logship/logship_test.go`, `internal/agentstate/agentstate_test.go` |
| AC6 | `TestFiles_RotationByRenameWhileRunning`, `TestFiles_RotationWhileStoppedFindsTheRenamedFile` (`.1` and `-YYYYMMDD`), `TestFiles_RotationWithoutTheOldFileIsAGap` | `internal/logship/files_test.go` |
| AC7 | `TestFiles_TruncationRestartsWithOneGap`, `TestFiles_TruncationRefilledPastThePositionIsDetected` | `internal/logship/files_test.go` |
| AC8 | `TestLogs_FirstStartShipsNothingOlder`, `TestFiles_AddedFileStartsAtItsEnd`, `TestFiles_FileThatAppearsLaterIsReadFromItsStart`, `TestJournal_NewSeqnumIDIsReadFromItsStart` | `internal/logship/logship_test.go`, `internal/logship/files_test.go`, `internal/logship/journal_test.go` |
| AC9 | `TestLogs_DroppedRunIsReadAgain`, `TestLogs_AbandonedRunNeverMovesAPosition` | `internal/logship/logship_test.go` |
| AC10 | `TestJournal_VacuumedPositionIsOneGap`, `TestJournal_UnreadableEntriesGiveOneGap`, `TestJournal_EntryWithoutMessageIsSkipped` | `internal/logship/journal_test.go` |
| AC11 | `TestOpenNoLinks_RefusesALinkInAnyElement`, `TestOpenNoLinks_RefusesNonRegularFiles`, `TestFiles_UnreadableFileKeepsItsPosition`, `TestJournal_UnreadableJournalKeepsThePosition` | `internal/logship/safeopen_test.go`, `internal/logship/files_test.go`, `internal/logship/journal_test.go` |
| AC12 | `TestFiles_PartialLineWaitsForItsLineFeed`, `TestFiles_PartialLineIsShippedAtRotation` | `internal/logship/files_test.go` |
| AC13 | `TestLogs_RunBudgets` (1,000 records, 4 MiB crossing record, one second with a scripted clock, filtered lines count only against time), `TestLogs_HeapStaysBounded` (records at every field's maximum, a journal of 100,000 entries, lines over 16 KiB), `TestDecoder_HeapStaysBoundedForTheLargestFrame`, `TestDecoder_RefusesAFrameBeyondTheBound` | `internal/logship/logship_test.go`, `internal/zstdfield/zstdfield_test.go` |
| AC14 | `TestLoadAgent_LogShippingErrors` (every invalid option of E16, the error holds file, line, key and index and not the value), `TestLoadAgent_LogShippingDefaults`, `TestLoadAgent_Example` (extended) | `internal/config/logshipping_test.go`, `internal/config/agent_test.go` |
| AC15 | `TestLogs_LogsHoldNoPathFieldOrMessage` (recording handler: the fixed texts only) | `internal/logship/logship_test.go` |
| AC16 | `TestRecords_RefusedRecordBecomesAGap` (the check step fed a record the wire check refuses, a negative `pid`, since the cuts make every mapped record pass), `TestLogs_FourthDroppedRunGivesUpTheSpan` | `internal/logship/records_test.go`, `internal/logship/logship_test.go` |
| AC17 | `TestDecode_StateWithoutLogPositionsStaysUsable`, `TestEncode_SixteenFilePositionsFitTheBound`, `TestDecode_RefusesInvalidLogPositions` | `internal/agentstate/agentstate_test.go` |

## Approach

**Fixtures (real samples, anonymized by the Lead).** The three server files stay outside the repository. The Lead generated, from
field-name shapes only, the export `live.export` (21 entries covering the variants of facts 5 and 6 plus a
20,000-byte message; every value generated) and the aggregate `zstd-frames.txt` (frame form and size range per file, no content). Every field of `live.export` was
compared with the real values (equality, contained host names, IDs, IPv4 and mail addresses, 8-byte prefixes): 454 fields, 0 hits. The Tester copies both byte for byte (SHA-256 of `live.export`
`1f50cea662d1d65a7d21f3b0e9a2d1171da7a3b998b0ae9eed4524de9c38fdfe`) from the session scratchpad
`fixtures/` to `testdata/journal/live.export` and `testdata/journal/zstd-frames.txt`, and creates `testdata/journal/live-records.jsonl`
(the expected `log_line` records, one JSON object per line with `log`, `source`, `captured_at`, `host`, `program`, `pid`,
`priority`, `message`, `truncated`, written once from the import's mapping by hand and checked by both languages). The binary journal
files of the tests are generated at test time by `journaltest.Write` with the new `Options.Compress` (the library's encoder, fact 14)
(fact 14); a user file is written by the same writer with a seqnum ID of its own and named `user-1000.journal`. Syslog lines come from
`testdata/logs/mail.log` and `kern.log-oom` and hand-written forms; file rotation, truncation and links use real files in `t.TempDir()`.

**Journal (`internal/journal`).** `ListAllFiles` adds `user-<digits>.journal`, `user-<digits>@*.journal` and `user-<digits>@*.journal~`
(digits 1 to 10, no other character). `SeekAll` positions a cursor on the file's main entry array (header offsets 152 and 176) at the
first entry after a sequence number, by the same bisection as `Seek`; `NextWith` reads an entry with `ReadOptions`: wanted payloads up
to `MaxPayload` bytes (excess not read, `Field.Cut` set), and compressed items with the zstd flag passed to `Decompress` (output up to
`MaxPayload` plus the name), XZ or LZ4 items and failed decodings counted in `Entry.Undecoded`. `Next` keeps its behavior (kernel
collector unchanged).

**Decoder (`internal/zstdfield`).** One `zstd.Decoder` per collector with `WithDecoderConcurrency(1)`, `WithDecoderLowmem(true)`,
`WithDecoderMaxMemory(4 MiB)`, `WithDecoderMaxWindow(4 MiB)`; `Decompress` appends into a reused buffer and returns at most the
caller's limit; a frame beyond the bound or malformed is an error (the entry becomes unreadable, E12).

**Syslog (`internal/syslogline`).** A hand-written, linear port of the import's syslog line rules (fact 11) on bytes, the year and
daylight-saving rules of E10 through `Clock` (one per file, keeps the previous line's instant), and `LoadZone` reading at most 64 KiB of
`localtime` from an `fs.FS` (production `os.DirFS("/etc")`) into `time.LoadLocationFromTZData`; an error never falls back to UTC.

**Collector (`internal/logship`, name `logs`, interval 2 s, timeout 2 s, after `kernel` in the job list).** A run works on a fork of the
committed reading state and stops at 1,000 records, at the record that crosses 4 MiB of text, after one second of reading or at its
deadline.
- *Journal:* locate the directory as the kernel collector does (persistent, else volatile), list with `ListAllFiles`, group the files by
  seqnum ID; per ID with a position, merge its files by sequence number from the position (`SeekAll`); per ID without one, read from
  its start, except at a first start (no log state), where every ID starts at its tail. Vacuum and lost-ID gaps follow the kernel
  collector's rules (a gap from the position's time unless the first remaining entry's sequence number directly follows). An ID whose
  files are all archived and read to their end is forgotten; at most 32 IDs are kept (the oldest by position time dropped first).
- *Mapping:* fact 10 (`log`/`source` `journal`); host and program cut to 1,024 bytes and the message to 16,384 with `CleanText`,
  `truncated` when any is cut or a field was `Cut`; entries without `MESSAGE` or with a time outside the storable range are skipped;
  `Undecoded` or a read error of one entry is a gap over it.
- *Files:* each configured file is opened through `OpenNoLinks` (from `/`, `openat` with `O_NOFOLLOW|O_NONBLOCK|O_CLOEXEC` per element,
  directories with `O_DIRECTORY`, then `fstat`: a regular file only); the open descriptor of the committed file is kept across runs. A
  different device or inode at the path is a rotation: the kept descriptor is read to its end first (a partial last line shipped as it
  is), else, after a restart, the directory (opened the same way) is searched for `<base>.1` and `<base>-YYYYMMDD` with the saved device
  and inode (`fstatat` with `AT_SYMLINK_NOFOLLOW`); not found is the E2 gap. A size below the offset or a changed SHA-256 of the first
  up to 256 bytes (the head kept with the position) is a truncation: the S6 gap and offset 0. Lines are cut at 16 KiB (rest discarded); a
  partial last line waits. Records: `log` the configured path, `source` `syslog`.
- *Filter* after mapping (AC3), compiled once from the configuration.
- *Check:* every record goes through `wire.CheckRecord` (as the scheduler stamps it) before it is returned; a refused one becomes a gap
  over its line (E19).
- *Commit:* `Kept(run, true)` commits the fork of that run and calls `store.Update(true, …)`; a run without `Kept(true)` leaves the
  committed state, and the fourth dropped run in a row from one committed state commits its fork with the give-up gap of E19 and
  returns no lines of the span. A late result of an abandoned run never commits (its `Kept` is never called).
- *Logs:* warnings `journal not readable` (`err` a fixed `journal: …` reason), `log file not readable` (`file` the index, `err` one of
  `missing`, `not a regular file`, `link refused`, `permission denied`, `read failed`), `time zone not readable`, and their `… readable
  again` infos; never a path, a field value or a message.

**Scheduler.** `Observer` is optional: `runner.finish` calls `Kept(run, kept)` after the delivery of a run that finished in time (an
error or panic: `false`); an abandoned or skipped run calls nothing.

**State.** `State.Logs` with up to 32 journal positions (`seqnum_id`, `seqnum`, `realtime`) and up to 16 file positions (`path_sha256`
64 lower-case hex digits, `device`, `inode`, `offset` ≥ 0, `head_sha256`, `head_len` 0 to 256, `last_time`); decoded strictly; a file
without `logs` stays valid; 16 KiB stays the bound. `Store.Flush` saves at once when a change waits; `agent.Run` calls it after the
scheduler stopped.

**Configuration.** `log_shipping` with `journal` (bool, default `true`), `min_priority` (default `info`), `files` (list of mappings
`path`, `format`), `drop` (list of mappings `log`, `program`, `priority`, `message`), checked as E16; `message` compiled with `regexp`
(RE2) and refused with the fixed reason `must be an RE2 pattern of at most 1024 bytes`; reasons never quote a value.

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
	MaxPayload int          // bytes of a wanted payload kept, after decompression
	Decompress Decompressor // nil: compressed items stay undecoded
}
func (f *File) SeekAll(after uint64) (Cursor, error)
func (f *File) NextWith(c *Cursor, want []string, e *Entry, opt *ReadOptions) (bool, error)
// Field gains: Cut bool; Entry gains: Undecoded int

// internal/journal/journaltest
// Options gains: Compress func(payload []byte) ([]byte, error) // nil keeps the raw-block frames

// internal/zstdfield
const MaxFrameBytes = 4 << 20
type Decoder struct{ /* unexported */ }
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
}
// State gains: Logs *LogPositions
const MaxLogJournals = 32
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
	MaxPatternBytes = 1024
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
  measures); zstd frames refused above 4 MiB of content or window **before** decoding, output cut to the payload bound; file lines cut
  at 16 KiB of raw bytes **before** decoding, then cleaned and cut again; `localtime` at most 64 KiB raw; state file 16 KiB raw;
  patterns 1,024 bytes of the configuration value; run budgets count bytes of the cleaned record text.
- [ ] Exceptions reaching a user-visible text: configuration errors (`KeyError` with fixed reasons, never the value or the pattern's
  compile error text); the collector's log lines carry only the fixed `err` texts listed in *Approach*, never an `os` error with a path
  (an `*fs.PathError` is reduced to its errno class before logging).

## Decision records

- `docs/decisions/0121-log-shipping-journal-and-syslog-files-positions-per-seqnum-id-zstd-commit-when-kept.md` (Proposed, indexed).
- `docs/decisions/0119-…` edited in place (unreleased): its last consequence names 0121.

## Out of scope / follow-ups

- #289 (error log formats), #290 (classifying live lines), #38 (spool; exact delivery across a crash), #44 (the volume on the server).
- A non-empty user journal (fact 9), if one becomes available.
