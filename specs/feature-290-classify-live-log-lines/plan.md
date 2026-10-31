# Plan: Classify live log lines

Source: Issue #290 | [spec.md](spec.md)
Status: Revised (2)
Tier: standard — the agent gets new behavior (live lines classified and grouped, a flush of the open groups at the stop, saved positions held back before held lines), but no security control is created, changed or removed: the stages are a classification, not a trust boundary.
Security areas touched: 10 (parsing of external input: the new Go package `internal/logstages` reads journal and syslog text that local users can write; `internal/logship` feeds it and holds its state within the collector's memory bound)

## Problem / root cause

See [spec.md](spec.md). Live records skip the import's stages. `internal/logship` hands each mapped, filtered line to `run.emit`:
`fileReader.entry` in `internal/logship/journal.go:518-522` and `filesRun.line` in `internal/logship/files.go:383-386`. Nothing runs
the logic of `SystemLogGrouper` (`src/Vandox.Core/LogParsing/SystemLogGrouper.cs:27-35`).

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | Live records have no `event` and keep the transport priority. | `docs/areas/agent.md` *Journal entries*, *Syslog lines* ("no `event`"); `docs/areas/wire-format.md:330`; no classifier call in `internal/logship` (grep). | confirmed |
| 2 | The five signatures need an event; `mariadb_crash` also needs `got signal N ;$`. | `src/Vandox.Core/Signatures/signatures.yaml:20-29, 64-69` | confirmed |
| 3 | The import's stages run in the order mail, MariaDB, kernel, and an open group is emitted only by `Finish` at the normal end of input. | `SystemLogGrouper.cs:27-49`; `MariaDbLineGrouper.cs:68-72`; `KernelReportGrouper.cs:126-129` | confirmed |
| 4 | A MariaDB entry joins same-key lines up to 60 s before or after the header (`Duration()`). It ends at any header, at a same-key line that does not join, or at `Finish`. Other programs' lines never end it. | `MariaDbLineGrouper.cs:87-108, 154-157` | confirmed |
| 5 | A kernel report joins same-host kernel lines at most 60 s after its first line (earlier ones join too). It ends at an end line, at 2,000 lines, at a start line, at a same-host kernel line beyond 60 s, or at `Finish`. Other hosts' and programs' lines pass through at once. | `KernelReportGrouper.cs:79-121, 148-156` | confirmed |
| 6 | The journal's MariaDB lines all carry `PRIORITY` 6 (the unit's `SyslogLevel=`). | to verify: `journalctl -o export -u mariadb` on the server, by the Product Manager after release. Source of the claim: `docs/areas/log-import.md` *Lines from the journal and syslog* ("not checked on the server"). The plan does not depend on it: it only explains E3, and the configuration area keeps it marked unverified (Dev, step 6) | unverified |
| 7 | journald writes the entries of system users (UID below 1000, such as `mysql`) and kernel messages to the system files; only regular users get `user-<UID>.journal` under `SplitMode=uid`. | journald.conf(5), `SplitMode=`; record 0121 *Context* (server sample: a `user-10000.journal`, the system files share one seqnum ID); MariaDB's own UID was not checked on the server | confirmed |
| 8 | A run works on a clone of the committed state; a dropped run's fork is never committed; a give-up starts from the dropped fork. | `internal/logship/state.go:63-87`; `logship.go:207-272` | confirmed |
| 9 | Saved positions are the last journal sequence number per ID and the offset of the first unshipped line per file. Every save first makes the spool durable. | `positions.go:85-142`; `docs/areas/agent.md:1508-1513` | confirmed |
| 10 | The state file is at most 64 KiB; the worst case today is 256 journal positions and 16 file positions with renamed files. | `internal/agentstate/agentstate.go:31-33`; `agentstate_test.go:1085-1101, 1146-1170` | confirmed |
| 11 | The scheduler has no hook that runs a collector after the stop signal; the agent only calls `store.Flush` after the scheduler has stopped. | `internal/scheduler/scheduler.go:31-43, 120-127`; `internal/agent/agent.go:176-186` | confirmed |
| 12 | The record check has no time bound, so a grouped record with its header's older capture time passes. | `internal/wire/encode.go:78-93` | confirmed |
| 13 | `log_line.event` is in both decoders, and the golden batch carries one; an older backend accepts a batch with events. | `testdata/wire/all-kinds.jsonl` (1 `event`); `WireContractTests.DecodeGoldenBatchReadsEveryLogLineField` | confirmed |
| 14 | The backend acknowledges a batch only after its commit. | `IngestEndpointTests.IngestEndpointValidBatchAnswers204AfterCommit`; project.md area 1 | confirmed |
| 16 | No existing logship test has a mail marker, a MariaDB line or a kernel start line. | grep of `internal/logship/*_test.go` for `mariadbd`, `oom-killer`, `cut here`, `postfix/`, `dovecot` | confirmed |
| 17 | The collector's live memory stays within 24 MiB with 256 user IDs and 8 system IDs, at most two journal IDs and 16 files holding a MariaDB entry and a kernel report each near maximum size, in three copies. | Dev's measurement in a copy with the test fixed (mode `decide`, item 1): peak 1.63 MiB; `TestLogs_HeapStaysBounded` asserts it once the Tester's fix is in | confirmed |
| 18 | The spool accepts records after the scheduler has stopped and before `store.Flush`: it is closed only after the last save. | `internal/agent/agent.go:176-186` (`wg.Wait()`, `store.Flush`, then `closeSpool`) | confirmed |
| 19 | A syslog-file line's capture time is the time written in the line (by rsyslog, or the sender where rsyslog keeps the sender's time); a line dated after its run's start already does not move the file's bookkeeping time. | `internal/logship/files.go:356-386`; `docs/areas/agent.md` *Configured files* (record 0121 option 14) | confirmed |
| 20 | systemd 249 on the server gives `user-10000.journal` a seqnum ID of its own. | record 0121 *Context*; later versions not checked, and the design does not depend on it (*Approach* 2) | confirmed |
| 21 | A refused record is replaced by a gap from its `from` to its capture time, and a gap that would not be after its start falls back to 1 µs at the run start. | `internal/logship/records.go:66-80` | confirmed |

Claims of the issue: confirmed (facts 1-3). Related defect: none found.

## Tests per criterion

Go tests sit next to the code. C# tests mirror the class under test.

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `TestLogs_RecoveryEndAfterTheStartEntryExpiredGetsItsEvent`; `TestLogs_ClassifiedJournalEqualsTheImport` (system-ID journal written from `classify.export`, against `classify-records.jsonl`; records compared in order of capture time, then program, then message; every field except `log`, origin and sequence); `TestLogs_ClassifiedSyslogFileEqualsTheImport` (against `classify-syslog.records.jsonl`; raw and error-log lines against today's live mapping); `JournalExportParserParsesClassifyFixtureAsTheGoldenRecords`; `SyslogParserParsesClassifyFixtureAsTheGoldenRecords`; package tests `TestClassifyMail` (table of the C# `MailLineClassifierTests` cases), `TestParseHeader` (the four header forms), `TestMariaDB` (join, end, bounds, events, recovery), `TestKernel` (start, ends, 2,000 lines, head and tail) | `internal/logship/stages_test.go`; `tests/Vandox.Core.Tests/JournalExportParserTests.cs`; `tests/Vandox.Core.Tests/SyslogParserTests.cs`; `internal/logstages/mail_test.go`, `header_test.go`, `mariadb_test.go`, `kernel_test.go` |
| AC2 | `BuiltInSignaturesFindEventSignaturesInClassifyGoldenRecords` (the detector on both golden files: `postfix_fatal`, `dovecot_fatal`, `mariadb_crash` with `signal`, `mariadb_recovery_start`, `mariadb_recovery_end`) | `tests/Vandox.Core.Tests/BuiltInSignaturesTests.cs` |
| AC3 | `TestLogs_GroupSpansRunsAndBatches` (lines of one entry and one report in three runs, each kept); `TestLogs_DroppedRunJoinsEachLineOnce` | `internal/logship/stages_test.go` |
| AC4 | `TestLogs_CaughtUpEntryClosesAfterSixtySeconds` (closed at clock = first + 60 s + 1 µs, not at + 60 s); `TestLogs_BacklogLineJoinsByCaptureTime` (captured + 50 s, read 90 s later); `TestLogs_LineSixtyOneSecondsLaterClosesTheEntry`; `TestLogs_FutureDatedGroupClosesSixtySecondsAfterItsRun` (a forged kernel start line in a `syslog` file dated a year ahead closes by the clock 60 s after the start of the run that read it); `TestStages_Expire` | `internal/logship/stages_test.go`; `internal/logstages/stages_test.go` |
| AC5 | `TestLogs_StopFlushSendsOpenGroupsAndSavesTheShippedPositions` (clean stop with an open entry and later shipped lines; new collector from the saved state: every line once, the entry possibly as two records); `TestLogs_KillWithoutFlushReadsAgainFromTheOldestHeldLine` (state of an earlier save: only that source's lines from the entry's first line on come again, nothing else); `TestLogs_FlushNotKeptLeavesThePositionsHeldBack`; `TestLogs_RestartWithANewDropRuleLosesNoLine` (held entry, restart without flush and a drop rule that matches its header: every line not dropped by the rule is shipped); `TestScheduler_FlushPutsTheRecordsAndReportsKept`; `TestScheduler_FlushWaitsForAStoppedWorkerOrSkips` (a slow `Collect` across the stop, under `-race`: `Flush` never runs while it does, and is skipped when it outlasts the context); `TestScheduler_FlushHonoursItsContextWhenFlushBlocks`; `TestRun_StopFlushesTheCollectorsBeforeTheLastSave`; backend part: existing `IngestEndpointValidBatchAnswers204AfterCommit`, unchanged (the backend holds no line) | `internal/logship/stages_test.go`; `internal/scheduler/scheduler_test.go`; `internal/agent/agent_test.go`; `tests/Vandox.Backend.Tests/IngestEndpointTests.cs` (existing) |
| AC6 | `TestLogs_FilterDecidesBeforeClassification` (Postfix `warning:` with priority 6 dropped under `notice`; MariaDB journal header dropped under `warning`); `TestLogs_DroppedHeaderLeavesItsLinesToThePreviousEntry` | `internal/logship/stages_test.go` |
| AC7 | `TestLogs_ForgedFloodHoldsOneEntryAndOneReportPerSource` (10,000 forged headers and start lines; other programs' records have lower indexes than the entry's); `TestLogs_HeapStaysBounded` (adapted as fact 17 says) | `internal/logship/stages_test.go`; `internal/logship/logship_test.go` |
| AC8 | `TestLogs_JournalAndFileNeverJoin`; `TestLogs_UserJournalGetsMailClassificationOnly`; `TestLogs_UserFileEntriesOfASharedIDAreNotGrouped` (one seqnum ID over a system and a user file) | `internal/logship/stages_test.go` |
| AC9 | existing `DecodeGoldenBatchReadsEveryLogLineField` and `TestEncodeBatchGolden`, unchanged; existing `IngestEndpointStoredRecordWithOtherContentIsKept`, unchanged (stored records are never updated) | `tests/Vandox.Core.Tests/WireContractTests.cs`; `internal/wire/golden_test.go`; `tests/Vandox.Backend.Tests/IngestEndpointTests.cs` |
| AC10 | `TestLogs_GapEndsTheOpenGroups` (truncation, unreadable entry, vacuumed span, give-up: the groups are sent before the gap, and the next line does not join); `TestLogs_RefusedGroupBecomesAGapOverTheGroup` | `internal/logship/stages_test.go` |

## Approach

**Location.** The agent classifies, after its filter, with a Go port (record 0121 option 17).

**Package `internal/logstages`** (new) is a port of the C# stages, ordinal and on bytes, with the same bounds:

- `mail.go` ports `MailLineClassifier`.
- `header.go` ports `MariaDbLine.TryParse` (four header forms) and `MariaDbEventClassifier` (with its recovery state).
- `message.go` ports `MariaDbMessage`: the join, 16,384 bytes, inner empty lines kept and trailing ones dropped.
- `mariadb.go` ports `MariaDbLineGrouper`.
- `kernel.go` ports `KernelReportGrouper`: head 8,192 bytes, tail, `[N lines omitted]`, 2,000 lines.
- `stages.go` combines them as `SystemLogGrouper` does.

`Expire`, `Close` (the import's `Finish`), `HeldFrom` and `Clone` are the live additions (*Signatures*); a `MailOnly` line gets only `ClassifyMail`. `Clone` copies deeply. Texts are already valid UTF-8 (logship cleans them), so byte counts equal the C# counts. Port from the C#
source, not from the area document; where the two disagree, the C# code wins and the Dev reports the difference.

**Integration in `internal/logship`:**

1. Every line that passes the filter goes to its source's `Stages` instead of `run.emit`, with its `Mark`: the position to save
   so that the line is read again, and the time of the line before it. Before that, logship calls `Expire` with the line's
   capture time. Each output is emitted with its own `From`. A group's `From` is the time of the line before its first line, so
   a refused group becomes a gap over the group (DA 4).
2. Sources: every configured `syslog` file, and every journal seqnum ID. A journal entry read from a user file
   (`user-<UID>.journal`, `user-<UID>@*`) is marked `MailOnly`: it gets `ClassifyMail` and passes the stages without touching their
   state. The rule decides by the file, not by the ID, so it holds however journald assigns IDs (fact 20). Error-log formats get
   no stages (E7). `state.stages` holds a `Stages` only for a source with an open group, created on demand and dropped when its
   last group closes. A `Stages` allocates a grouper's buffers only while its group is open, so 256 user IDs cost nothing.
3. Expiry.
   - Every time is capped at the start of the run that read it: the earlier of the capture time and that run's start (record
     0121 option 14).
   - The **anchor** of a group is its first line's capped time.
   - A group expires when a shipped line of its source has a capped time more than 60 s after the anchor (`Expire` with that
     capped time).
   - It also expires when the source has been read to its end in a run and the clock is more than 60 s past the anchor.

   A future-dated line can neither hold a group nor close one early (DA 2, `decide` a); a flood is residual N1.
4. Closing (`Close`):
   - at the end of reading any journal ID in a run, except the ID whose online file is `system.journal`;
   - at a rotation, when the kept file becomes the renamed part, because the held-back position names a part, and the import
     groups per file. A group open across the moment the rotation is found is sent as two records (mode `decide`, item b);
   - before every gap the collector records for that source (truncation, lost rotation, vacuum, unreadable entry, refused
     file, an ID forgotten with a gap);
   - at a give-up, the run that starts from the dropped fork closes every source before the give-up gap (E10).

   A run that stops on its budget inside an archived ID keeps that ID's groups until the next run reads on. So at most two
   journal IDs hold groups.
5. `state.clone` copies the existing `Stages` deeply, so a dropped run never commits a join (E9).
6. **Saved positions are held back.** `positionsOf` saves a source that holds groups at `Stages.HeldFrom()`, the mark of its
   oldest held line, instead of its read position:
   - for the journal, the sequence number and time before that entry;
   - for a file, that line's offset in the current or the renamed part, and the line before's time.

   Nothing else is added to the state file. The cap of 256 journal positions is unchanged; an ID whose held-back position falls
   off it starts after a restart by the existing rules, with their gap.
7. **Flush at the stop.** `Logs` implements the new `scheduler.Flusher`. After the runners have stopped and before
   `store.Flush`, `agent.Run` calls `Scheduler.Flush` with the stop's 5-second deadline.
   - **No overlap with a run (B4).** A runner also keeps the result channel of a worker that the stop left out (today only a
     timed-out one is kept, `runner.go:246-250`). `Scheduler.Flush` first waits on that channel within its context. A collector
     whose worker has not returned by then is skipped. A skipped flush is a flush not kept.
   - **Bounded.** The collector's `Flush` runs in a worker of its own, as `collect` runs `Collect`. When the context ends first,
     that worker is abandoned, nothing is put, and no observer is told.
   - **No commit before `Kept`.** `Logs.Flush` resolves an earlier pending run as dropped, as `begin` does, and forks the
     committed state. It closes every group in the fork and returns the records, with the fork as its pending run. It commits
     nothing itself. Only `Kept(true)`, called by `Scheduler.Flush` after the records were put, adopts the fork and saves the
     shipped positions. An abandoned or unkept flush leaves the held-back positions.

The MariaDB recovery state of a source outlives its `Stages`: one flag per system ID or file, kept while it is set, and cloned
with the state. A recovery end long after its start therefore still gets its event, as S5 requires (mode `decide`, item c).

So a clean stop repeats nothing. A crash, or a flush that was not kept, reads a holding source again from its oldest held line,
as E2 allows. Nothing read again is discarded, so a changed filter, zone or version cannot lose a held line. The recovery state
is not saved.

**Golden fixtures** (Tester): the classification fixture covers every variant AC1 lists.

- `testdata/journal/classify.export` holds journal export entries of one host: Postfix and Dovecot markers in both Dovecot
  positions, both database rules, an unmarked line, a MariaDB crash report with stack lines and an `sshd` line inside it, a
  recovery start and end, a `mariadb.start`, an entry that ends at 16,384 bytes, and an OOM report with an interleaved line.
- `testdata/logs/classify-syslog.log` holds the same kinds of lines with RFC 3339 times, so neither the zone nor the year is
  involved, plus a raw line.
- `classify-records.jsonl` and `classify-syslog.records.jsonl` (with `event`) are what the C# import writes.

The lines follow the samples the import was built from (`mail.log`, `kern.log-oom`, `mariadb-error.log`, `MariaDbSamples.cs`;
formats checked in records 0088 and 0091). Nothing is captured from the server.

No new user-visible output: the records keep their fields.

## Options considered

The options are in record 0121, options 15 to 23:

- classify at ingest;
- classify each line at ingest without grouping;
- a Go port in the agent (chosen);
- open groups in the state file;
- a stop flush with held-back positions (chosen);
- replay marks (rejected in the plan review);
- one journal-wide grouping state, against stages per ID with user files decided by the file (chosen);
- a saved recovery state;
- expiry from the first capture time alone, against the earlier of it and the run start (chosen).

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Go agent | `internal/logstages/*.go` (new) | the port, as in *Approach* |
| Go agent | `internal/logship/stages.go` (new) | feeding, expiry and closing of the stages; `Flush` |
| Go agent | `internal/logship/journal.go`, `files.go`, `state.go`, `positions.go`, `logship.go`, `run.go` | stages per source, `Close` at gaps, ID ends, renamed ends and give-up; clone; held-back positions |
| Go agent | `internal/scheduler/scheduler.go` | `Flusher`, `Scheduler.Flush` |
| Go agent | `internal/agent/agent.go` | `Scheduler.Flush` between `wg.Wait()` and `store.Flush` |
| testdata | `testdata/journal/classify.export`, `classify-records.jsonl`, `testdata/logs/classify-syslog.log`, `classify-syslog.records.jsonl` | new fixtures (Tester) |
| .NET | none in production | golden tests only |

## Signatures (for the Dev's skeleton)

Package `internal/logstages` (semantics as in *Approach*):

- `const MaxSpan = 60 * time.Second`
- `type Mark struct { Renamed bool; At uint64; Before time.Time }`: the saved position that reads a line again (a journal
  sequence number before it, or a file offset in the current or the renamed part), and the time of the line before it.
- `type Line struct { Rec model.Record; Mark Mark; RunStart time.Time; MailOnly bool }`: a mapped, filtered line.
- `type Output struct { Rec model.Record; From time.Time }`: a record to emit and the start of its replacing gap.
- `type Stages struct` (unexported fields; grouper buffers allocated only while a group is open)
- `func New() *Stages`
- `Stages.Add(l Line, out []Output) []Output`
- `Stages.Expire(t time.Time, out []Output) []Output`: closes the groups whose anchor lies more than `MaxSpan` before t.
- `Stages.Close(out []Output) []Output`
- `Stages.Open() bool`
- `Stages.HeldFrom() (Mark, bool)`: the mark of the oldest held line.
- `Stages.Clone() *Stages`
- `func ClassifyMail(l *model.LogLine)`: sets the priority and event as the import's mail classifier does.

Package `internal/scheduler`:

- `type Flusher interface`: implemented by a collector with something to emit at the stop.
- `Flusher.Flush(ctx context.Context, run Run) []model.Record`
- `Scheduler.Flush(ctx context.Context)`: after the scheduler's run loop returned, for each collector that implements the
  interface, stamps and checks the records, puts them, and reports to an observer whether they were kept.

Package `internal/logship`: `Logs.Flush(ctx context.Context, run scheduler.Run) []model.Record`. Otherwise unexported: stages
per source in the state, and a run method that hands a line to them. No file is rewritten whole.

## Test files

- `internal/logstages/mail_test.go`, `header_test.go`, `mariadb_test.go`, `kernel_test.go`, `stages_test.go`
- `internal/logship/stages_test.go`
- `internal/logship/logship_test.go` (`TestLogs_HeapStaysBounded` adapted)
- `internal/scheduler/scheduler_test.go`
- `internal/agent/agent_test.go`
- `tests/Vandox.Core.Tests/JournalExportParserTests.cs`, `SyslogParserTests.cs`, `BuiltInSignaturesTests.cs`

Existing test call sites of changed signatures: none. The new members are additive. No logship test should change its
expectation (fact 16), and `agent_test.go`'s stop tests keep theirs: a collector without `Flush` changes nothing. A test that
fails for another reason goes back to the Lead.

Files no unit test can cover: none.

## Areas

- Agent (*Log shipping*, *Agent state*): the stages per source, closing, held-back positions, the stop flush, memory, the differences from the import.
- Log import (*System log parsers*, *Duplicates*): live lines get the same stages.
- Wire format (`log_line`): the agent sets `event`.
- Configuration and secrets (`log_shipping`): the consequences of E3.
- Detection, alerts and reports (*Log signatures*): event signatures match live lines.

## Documentation updates

All of these are the Dev's, written once the code is green. The sweep covered `docs/areas/*.md`, `docs/ARCHITECTURE.md`, `.squad/`
and `deploy/agent/agent.yaml`, searching for "no event", "not classified", "classification", the state file's shape and the
package list.

- `docs/areas/agent.md`:
  - *Log shipping*, *Journal entries* (line 1135) and *Syslog lines* (line 1208): replace "no `event`" with the stages.
  - A new paragraph *Classification*: the per-source rule (system IDs; user files mail only; error logs none); the closes of
    E1 (with the run-start anchor), E10 and the ID or renamed end; held-back positions and the stop flush with E2's bounds; E3's
    filter order.
  - *Memory*: the groups in the 24 MiB, as measured (fact 17).
  - *Residuals*: replace "no classification until #290" with the differences from the import (out-of-order line after the close,
    ID end and rotation (two records), the capped times, user files, recovery state at a restart, records ordered at their close).
  - *Residuals*, two more: E1's clock close of a `syslog` file needs the source read to its end, and the times come from the line
    as rsyslog writes them (reception time by default, not checked on the server). A flood of lines dated within the window,
    joining or not, holds a group and the held-back position as long as such a flood lasts, and a crash then reads the whole
    span again (N1, N3).
  - *Agent state* and *Saving*: a source with open groups is saved at its oldest held line; the stop flushes the open groups
    before the last save.
  - *Implementation* and *Related decisions* (0121).
- `docs/areas/log-import.md`: *System log parsers* (line 199) names the agent's port as the second user of the stages, and the
  live path's differences from the import; *Duplicates* (lines 350-353): the live copies now carry the same classification.
- `docs/areas/wire-format.md` line 330: the agent sets `event` and the priorities of the import's stages, and sends MariaDB
  entries and kernel reports as one record.
- `docs/areas/configuration-and-secrets.md`, `log_shipping.min_priority` and `log_shipping.drop`: E3, with the MariaDB
  consequence and the dropped header.
- `docs/areas/detection-and-reports.md` *Log signatures*, *Limitations*: the event signatures read live lines. Forged `postfix/*`,
  `dovecot`, and system-file `mariadbd` and `kernel` lines now make events live too, which later alert rules (#56, #61) must weigh
  (N2). A user file's forged `mariadbd` line no longer joins a server entry live.
- `docs/ARCHITECTURE.md` line 135: add `internal/logstages` to the agent's package list.
- `deploy/agent/agent.yaml`: the `min_priority` comment gets the MariaDB note.
- `.squad/stack.md` *Layout*: add `logstages` to the `internal/` list.
- `.squad/project.md` area 10: `internal/logstages`, with the same bounds and the classification-not-trust note as the C#
  stages, and the held state within the `logs` collector's memory bound.

## Architecture check

- Agent footprint (guarantee: the agent must never make the memory situation worse): the held state is bounded per source and
  checked by the heap test (fact 17). Nothing is spawned, and no right is added (records 0019, 0030).
- No loss without a gap and no duplicate beyond what E2 allows (record 0121): a held line is never covered by a saved position,
  and the rule "no saved position covers a record a crash can still lose" holds unchanged.
- The wire format is unchanged (1.1). Storage, ingest and the live/backfill classification are unchanged.
- Record 0073 is followed: this is a port bound by golden records, not reuse.

## Security considerations

- [ ] Limits:
  - Texts reach the stages already cleaned to valid UTF-8 and cut (16,384 / 1,024 bytes), so every stage bound counts bytes
    after the replacement, as in C#.
  - The header parser reads at most the message.
  - The state file's shape and bounds are unchanged.
- [ ] Exceptions that reach a user-visible text: none. The stages have no error path, and the agent logs nothing per line. A
  flush that fails is not kept and logs nothing new; the held-back positions stay.

## Decision records

- `docs/decisions/0121-log-shipping-journal-and-syslog-files-positions-per-seqnum-id-zstd-commit-when-kept.md`: extended in place
  (unreleased: `git ls-remote --tags origin` lists only `v0.1.0`, which holds records up to 0108). It gains options 15-22, the
  decision and the consequences for #290, revised after the plan review. Its status is set to Proposed and its index row updated.

## Challenge

Devil's Advocate, plan challenge: all four objections were accepted (record 0121, options 19-23).
- Major 1: held-back positions and a stop flush (*Approach* 6-7).
- Major 2: expiry on capped times (*Approach* 3).
- Minor 3: user files are decided by the file (*Approach* 2).
- Minor 4: `Output.From`.

## Security review

- Round 1:
  - B1 and B3 resolved by removing the marks and the replay.
  - B2 accepted (fact 17).
  - N1 and N2 taken (*Documentation updates*).
- Round 2:
  - B4 accepted as required (*Approach* 7, AC5).
  - N3 taken: spec E1 and E2 changed, and the Agent area residual updated.

## Out of scope / follow-ups

As in the spec. No follow-up issue.
