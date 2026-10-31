# Plan: Classify live log lines

Source: Issue #290 | [spec.md](spec.md)
Status: Draft
Tier: standard — the agent gets new behavior (live lines classified and grouped, replay marks in the state file), but no security control is created, changed or removed: the stages are a classification, not a trust boundary.
Security areas touched: 10 (parsing of external input: the new Go package `internal/logstages` reads journal and syslog text that local users can write; `internal/logship` feeds it), 9 (the agent's state file `agent-state.json`, `internal/agentstate`: new strictly decoded replay marks within the 64 KiB bound)

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
| 15 | Golden records written by the C# import and read by Go tests already bind the journal mapping. | `JournalExportParserTests.JournalExportParserParsesLiveFixtureAsTheGoldenRecords`; `internal/logship/journal_test.go:25-80` | confirmed |
| 16 | The existing logship tests use no Postfix or Dovecot marker, no MariaDB line and no kernel start line, so their expectations do not change. | grep of `internal/logship/*_test.go` for `mariadbd`, `oom-killer`, `cut here`, `postfix/`, `dovecot`; `filter_test.go`'s `dovecot` lines have no marker at a marker position | confirmed |
| 17 | The collector's live memory stays within 24 MiB with one open MariaDB entry and one open kernel report at maximum size for the journal and each of 16 files, in the committed state, a fork and the last dropped fork. | to verify: `TestLogs_HeapStaysBounded` extended to those groups, run by the Dev in step 6; a failure goes back to the Lead. Estimate: about 50 KiB per source, 17 sources, 3 copies, about 2.6 MiB | unverified |
| 18 | The state file holds the worst case plus 17 replay marks within 64 KiB. | to verify: `TestEncode_FullLogPositionsFitTheBound` with the marks added, written by the Tester and run by the Dev in step 6. Estimate: about 200 bytes per mark, about 3.4 KiB in total | unverified |

Claims of the issue: "no event and none of the stages live": confirmed (1, 3). "Event-only signatures": confirmed (2). Related defect:
none found.

## Tests per criterion

Go tests sit next to the code. C# tests mirror the class under test.

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `TestLogs_ClassifiedJournalEqualsTheImport` (system-ID journal written from `classify.export`, against `classify-records.jsonl`; records compared in order of capture time, then program, then message; every field except `log`, origin and sequence); `TestLogs_ClassifiedSyslogFileEqualsTheImport` (against `classify-syslog.records.jsonl`; raw and error-log lines against today's live mapping); `JournalExportParserParsesClassifyFixtureAsTheGoldenRecords`; `SyslogParserParsesClassifyFixtureAsTheGoldenRecords`; package tests `TestClassifyMail` (table of the C# `MailLineClassifierTests` cases), `TestParseHeader` (the four header forms), `TestMariaDB` (join, end, bounds, events, recovery), `TestKernel` (start, ends, 2,000 lines, head and tail) | `internal/logship/stages_test.go`; `tests/Vandox.Core.Tests/JournalExportParserTests.cs`; `tests/Vandox.Core.Tests/SyslogParserTests.cs`; `internal/logstages/mail_test.go`, `header_test.go`, `mariadb_test.go`, `kernel_test.go` |
| AC2 | `BuiltInSignaturesFindEventSignaturesInClassifyGoldenRecords` (the detector on both golden files: `postfix_fatal`, `dovecot_fatal`, `mariadb_crash` with `signal`, `mariadb_recovery_start`, `mariadb_recovery_end`) | `tests/Vandox.Core.Tests/BuiltInSignaturesTests.cs` |
| AC3 | `TestLogs_GroupSpansRunsAndBatches` (lines of one entry and one report in three runs, each kept); `TestLogs_DroppedRunJoinsEachLineOnce` | `internal/logship/stages_test.go` |
| AC4 | `TestLogs_CaughtUpEntryClosesAfterSixtySeconds` (closed at clock = first + 60 s + 1 µs, not at + 60 s); `TestLogs_BacklogLineJoinsByCaptureTime` (captured + 50 s, read 90 s later); `TestLogs_LineSixtyOneSecondsLaterClosesTheEntry`; `TestStages_Expire` | `internal/logship/stages_test.go`; `internal/logstages/stages_test.go` |
| AC5 | `TestLogs_CleanRestartWithAnOpenEntrySendsEveryLineOnce` (stop with an open entry and later shipped lines, new collector from the saved state: the entry once, no plain line again); `TestLogs_KillWithoutFlushRepeatsOnlyTheSourceFromTheEntry` (state of an earlier save); `TestLogs_ReplayRangeGoneRecordsAGap`; backend part: existing `IngestEndpointValidBatchAnswers204AfterCommit`, unchanged (the backend holds no line) | `internal/logship/stages_test.go`; `tests/Vandox.Backend.Tests/IngestEndpointTests.cs` (existing) |
| AC6 | `TestLogs_FilterDecidesBeforeClassification` (Postfix `warning:` with priority 6 dropped under `notice`; MariaDB journal header dropped under `warning`); `TestLogs_DroppedHeaderLeavesItsLinesToThePreviousEntry` | `internal/logship/stages_test.go` |
| AC7 | `TestLogs_ForgedFloodHoldsOneEntryAndOneReportPerSource` (10,000 forged headers and start lines; other programs' records have lower indexes than the entry's); `TestLogs_HeapStaysBounded` (adapted: open groups at maximum in every source) | `internal/logship/stages_test.go`; `internal/logship/logship_test.go` |
| AC8 | `TestLogs_JournalAndFileNeverJoin`; `TestLogs_UserJournalGetsMailClassificationOnly` | `internal/logship/stages_test.go` |
| AC9 | existing `DecodeGoldenBatchReadsEveryLogLineField` and `TestEncodeBatchGolden`, unchanged; existing `IngestEndpointStoredRecordWithOtherContentIsKept`, unchanged (stored records are never updated) | `tests/Vandox.Core.Tests/WireContractTests.cs`; `internal/wire/golden_test.go`; `tests/Vandox.Backend.Tests/IngestEndpointTests.cs` |
| AC10 | `TestLogs_GapEndsTheOpenGroups` (truncation, unreadable entry, vacuumed span, give-up: the groups are sent before the gap, and the next line does not join) | `internal/logship/stages_test.go` |

Also: `TestDecode_ReplayMarks` (strict decoding: unknown key, more than 17 marks, more than 2 held positions, a `from` after the
saved position, a bad digest or UUID make the file unusable) and `TestEncode_FullLogPositionsFitTheBound` (adapted) in
`internal/agentstate/agentstate_test.go`, both for AC5.

## Approach

**Location.** The agent classifies live lines, after its filter, with a Go port of the import's stages. The plan reaches this by
elimination (option 17 of record 0121): grouping at ingest would merge records that carry their own sequence numbers.

**Package `internal/logstages`** (new) is a port of the C# stages, ordinal and on bytes, with the same bounds:

- `mail.go` ports `MailLineClassifier`.
- `header.go` ports `MariaDbLine.TryParse` (four header forms) and `MariaDbEventClassifier` (with its recovery state).
- `message.go` ports `MariaDbMessage`: the join, 16,384 bytes, inner empty lines kept and trailing ones dropped.
- `mariadb.go` ports `MariaDbLineGrouper`.
- `kernel.go` ports `KernelReportGrouper`: head 8,192 bytes, tail, `[N lines omitted]`, 2,000 lines.
- `stages.go` combines them as `SystemLogGrouper` does.

A `Stages` with `groups == false` runs the mail classification alone. Every line carries its `Pos`. Every output says whether it is
a group and gives its first `Pos`. `Expire(t)` closes every open group whose first capture time lies more than 60 s before `t`.
`Close` emits the open groups (the import's `Finish`). `Held` returns the first positions of the open groups, oldest first.
`Clone` copies deeply. Texts are already valid UTF-8 (logship cleans them), so byte counts equal the C# counts. Port from the C#
source, not from the area document; where the two disagree, the C# code wins and the Dev reports the difference.

**Integration in `internal/logship`:**

1. Every line that passes the filter goes to its source's `Stages` instead of `run.emit`. Before that, logship calls
   `Expire(line's capture time)` (E1). Each output is emitted with `from` = the previous line's time, as today.
2. Sources:
   - every configured `syslog` file is one source, with `Stages` in `fileState.stages`;
   - error-log formats get no stages (E7);
   - each journal seqnum ID gets its `Stages` in `state.stages[id]`, with `groups` true only when all of the ID's files are system
     files (`system.journal`, `system@*`).
3. Closing:
   - when a source has been read to its end in a run (the end of an ID, or of a file with nothing more to read), logship calls
     `Expire(clock)`;
   - at the end of reading any ID other than the one whose online file is `system.journal`, logship calls `Close`, so at most one
     journal source holds groups;
   - when a renamed file is finished, logship calls `Close` (the import groups per file).
4. Every gap the collector records for a source calls `Close` on that source first, and the gap is emitted after its records:
   truncation, lost rotation, vacuum, unreadable entry, refused file, an ID forgotten with a gap. At a give-up, the run that starts
   from the dropped fork closes every source's groups before the give-up gap (E10).
5. The clone in `state.clone` copies every `Stages`. A dropped run therefore never commits a join (E9).
6. Replay marks. `positionsOf` adds one `ReplayMark` per source that holds groups. The mark carries the source key (seqnum ID, or
   path digest plus whether the line lies in the renamed file), `From` (the position of the oldest held line), `Time` (that line's
   capture time) and `Held` (`Stages.Held`, one or two positions). The saved position stays the shipped position, so a saved
   position never covers a line that is neither in the spool nor in a mark.
7. On restore, a source with a mark reads again from `From` up to the saved position, with run budgets as usual, filter and
   stages as usual. Of what the stages emit there, only groups whose first `Pos` is in `Held` are kept, and only once they close.
   Everything else was sent before the stop, so it is discarded. Reading then goes on normally.
8. If the replay range can no longer be read (the ID has no file holding `From`, or the file's identity changed), there is one
   gap from `Time` to the saved position's time, and no replay.
9. A run that ends while it is still replaying keeps the replay in its fork.

So a clean stop repeats nothing. A crash repeats today's residual plus at most the groups that closed after the last save, which
are lines of their source captured from the group's first line on (E2). The recovery state is not saved; a replay starts it
closed. Stages are per source, so a flood holds at most two groups per source (E4).

**Golden fixtures** (Tester): the classification fixture covers every variant AC1 lists.

- `testdata/journal/classify.export` holds journal export entries of one host: Postfix and Dovecot markers in both Dovecot
  positions, both database rules, an unmarked line, a MariaDB crash report with stack lines and an `sshd` line inside it, a
  recovery start and end, a `mariadb.start`, an entry that ends at 16,384 bytes, and an OOM report with an interleaved line.
- `testdata/logs/classify-syslog.log` holds the same kinds of lines with RFC 3339 times, so neither the zone nor the year is
  involved, plus a raw line.
- `classify-records.jsonl` and `classify-syslog.records.jsonl` (with `event`) are what the C# import writes.

The lines are written after the existing samples the import was built from: `testdata/logs/mail.log`, `kern.log-oom`,
`mariadb-error.log` and `MariaDbSamples.cs`, whose formats records 0088 and 0091 checked against the MariaDB, Postfix and Dovecot
sources. They are not captured from the server; no private value is in them.

No new user-visible output: the records keep their fields.

## Options considered

The options are in record 0121, options 15 to 22:

- classify at ingest;
- classify each line at ingest without grouping;
- a Go port in the agent (chosen);
- open groups in the state file;
- a flush hook at the stop;
- replay marks (chosen);
- one journal-wide grouping state, against one state per system ID (chosen);
- a saved recovery state.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Go agent | `internal/logstages/*.go` (new) | the port, as in *Approach* |
| Go agent | `internal/logship/stages.go` (new) | the feeding, closing, replay and output filter of the stages |
| Go agent | `internal/logship/journal.go`, `files.go`, `state.go`, `positions.go`, `logship.go`, `run.go` | stages per source, `Close` at gaps, ID ends, renamed ends and give-up; clone; marks saved and restored |
| Go agent | `internal/agentstate/agentstate.go` | `ReplayMark`, `LogPositions.Replay`, strict decoding |
| testdata | `testdata/journal/classify.export`, `classify-records.jsonl`, `testdata/logs/classify-syslog.log`, `classify-syslog.records.jsonl` | new fixtures (Tester) |
| .NET | none in production | golden tests only |

## Signatures (for the Dev's skeleton)

Package `internal/logstages` (doc comments as in Go style; field and method semantics as in *Approach*):

- `const MaxSpan = 60 * time.Second`: the most capture time a group may span from its first line.
- `type Pos struct { Renamed bool; At uint64 }`: a journal sequence number, or a file offset in the current or the renamed file.
- `type Line struct { Rec model.Record; Pos Pos }`: a mapped, filtered line, whose record holds a log line.
- `type Output struct { Rec model.Record; Group bool; First Pos }`: a record the stages emit.
- `type Stages struct` (unexported fields): runs the mail classification and, when groups is set, the MariaDB and kernel
  grouping of one source.
- `func New(groups bool) *Stages`
- `Stages.Add(l Line, out []Output) []Output`
- `Stages.Expire(t time.Time, out []Output) []Output`
- `Stages.Close(out []Output) []Output`
- `Stages.Held() []Pos`: the first positions of the open groups, oldest first, at most 2.
- `Stages.HeldSince() (Pos, time.Time, bool)`: the oldest held line's position and capture time.
- `Stages.Clone() *Stages`
- `func ClassifyMail(l *model.LogLine)`: sets the priority and event as the import's mail classifier does.

Package `internal/agentstate`:

- `const MaxReplayMarks = 1 + MaxLogFiles`
- `type ReplayMark struct { SeqnumID string; PathSHA256 string; Renamed bool; From uint64; Time time.Time; Held []uint64 }`.
  It names exactly one of a journal ID and a file digest, and Held holds 1 or 2 positions. The JSON keys are the field names in
  snake case (seqnum_id, path_sha256, renamed, from, time, held), encoded like the existing positions.
- `LogPositions.Replay []ReplayMark`: optional, JSON key replay.

Package `internal/logship`: unexported only. The skeleton adds a stages field per file state and a map of stages per journal ID
to the state, and a run method that hands a line to its source's stages; it replaces the direct emit of a line. No file is
rewritten whole.

## Test files

- `internal/logstages/mail_test.go`, `header_test.go`, `mariadb_test.go`, `kernel_test.go`, `stages_test.go`
- `internal/logship/stages_test.go`
- `internal/logship/logship_test.go` (`TestLogs_HeapStaysBounded` adapted)
- `internal/agentstate/agentstate_test.go` (`worstCaseLogs` and `equalLogs` gain marks; `TestDecode_ReplayMarks` new)
- `tests/Vandox.Core.Tests/JournalExportParserTests.cs`, `SyslogParserTests.cs`, `BuiltInSignaturesTests.cs`

Existing test call sites of changed signatures: `agentstate_test.go` `worstCaseLogs` and `equalLogs`, adapted by the Tester. No
logship test should change its expectation (fact 16). A test that fails for another reason goes back to the Lead.

Files no unit test can cover: none.

## Areas

- Agent (*Log shipping*, *Agent state*): the stages per source, closing, replay marks, memory, the differences from the import.
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
    E1, E10 and the ID or renamed end; the replay marks with E2's bounds; E3's filter order.
  - *Memory*: the groups in the 24 MiB, as measured (fact 17).
  - *Residuals*: replace "no classification until #290" with the differences from the import (out-of-order line after the close,
    ID and renamed end, user journals, recovery state at a restart, records ordered at their close).
  - *Agent state*, line 1481: add `replay` to the JSON shape and the strict rules.
  - *Implementation* and *Related decisions* (0121).
- `docs/areas/log-import.md`: *System log parsers* (line 199) names the agent's port as the second user of the stages, and the
  live path's differences from the import; *Duplicates* (lines 350-353): the live copies now carry the same classification.
- `docs/areas/wire-format.md` line 330: the agent sets `event` and the priorities of the import's stages, and sends MariaDB
  entries and kernel reports as one record.
- `docs/areas/configuration-and-secrets.md`, `log_shipping.min_priority` and `log_shipping.drop`: E3, with the MariaDB
  consequence and the dropped header.
- `docs/areas/detection-and-reports.md` *Log signatures*, *Limitations*: the event signatures read live lines. A user journal's
  forged `mariadbd` line no longer joins a server entry live.
- `docs/ARCHITECTURE.md` line 135: add `internal/logstages` to the agent's package list.
- `deploy/agent/agent.yaml`: the `min_priority` comment gets the MariaDB note.
- `.squad/stack.md` *Layout*: add `logstages` to the `internal/` list.
- `.squad/project.md`:
  - area 9: the state file holds replay marks (at most 17, strictly decoded) and stays at most 64 KiB;
  - area 10: `internal/logstages`, with the same bounds and the classification-not-trust note as the C# stages.

## Architecture check

- Agent footprint (guarantee: the agent must never make the memory situation worse): the held state is bounded per source and
  checked by the heap test (fact 17). Nothing is spawned, and no right is added (records 0019, 0030).
- No loss without a gap and no duplicate beyond the existing residual (record 0121): replay marks keep the rule "no saved
  position covers a record a crash can still lose".
- The wire format is unchanged (1.1). Storage, ingest and the live/backfill classification are unchanged.
- Record 0073 (the agent does not reuse the .NET parsers): followed. This is a port bound by golden records, as for the syslog
  and error-log grammars.

## Security considerations

- [ ] Limits:
  - Texts reach the stages already cleaned to valid UTF-8 and cut (16,384 / 1,024 bytes), so every stage bound counts bytes
    after the replacement, as in C#.
  - The header parser reads at most the message.
  - The replay marks' `Held` is at most 2, and the marks are at most 17; both are checked when the state file is decoded,
    before use.
- [ ] Exceptions that reach a user-visible text: none. The stages have no error path. A mark that cannot be used becomes a gap,
  and the agent logs nothing per line.

## Decision records

- `docs/decisions/0121-log-shipping-journal-and-syslog-files-positions-per-seqnum-id-zstd-commit-when-kept.md`: extended in place
  (unreleased: `git ls-remote --tags origin` lists only `v0.1.0`, which holds records up to 0108). It gains options 15-22, the
  decision and the consequences for #290. Its status is set to Proposed and its index row updated.

## Out of scope / follow-ups

As in the spec. No follow-up issue.
