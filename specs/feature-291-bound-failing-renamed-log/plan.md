# Plan: Bound how long a failing renamed log file holds back its newer lines

Source: Issue #291 | [spec.md](spec.md)
Status: Draft
Tier: standard — a behavior change of the `logs` collector's file reading (a bound and a recorded gap) that creates, changes or removes no security control.
Security areas touched: 10. Parsing of external input (the configured log file reader `internal/logship`, its state machine, not a line grammar), 12. Logging and display of external data (one new fixed log text of the collector)

## Problem / root cause

See [spec.md](spec.md), *Problem*. The hold-back sits in `internal/logship/files.go`: `readRenamed` (lines 336-355) and
`finishRenamed` (lines 244-257) return false on a read error, and `read` (lines 316-320) and `rotate` (lines 220-222) then read
nothing else of the configured file; nothing counts the failures.

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | A read error of the renamed file stops the reading of its configured file for the run, current file included, in both paths. | `files.go:318-320` (`read`), `files.go:220-222` with `finishRenamed` `files.go:247-249` | confirmed |
| 2 | Such a run records no gap; the warning `log file not readable` (`read failed`) is logged once and stands. | `files.go:345`, `logship.go:344-353` (`logOutcome`, `fileBad` per file); `docs/areas/agent.md:1364-1366` | confirmed |
| 3 | The issue's "until the agent restarts": a restart reopens the renamed file by its identity and reads on, so a lasting error holds back again. | `files.go:174`, `findRenamed` `files.go:196-211`; `positions.go:78-79` restores `renamed` | refuted |
| 4 | `fileState.lastTime` is moved by lines of both parts, so it can be a current-file line's time while the renamed file is still read. | `files.go:389-391` (`line`), `files.go:321-327` (current read after an unfinished renamed one) | confirmed |
| 5 | `renamedFile` holds no time; the state file saves device, inode, offset and size of it only. | `state.go:33-39`, `positions.go:141-142` | confirmed |
| 6 | The reading state a run works on is a copy (`clone` copies `renamedFile` by value) committed only by `Kept(true)`; a dropped run's copy is never committed. | `state.go:82-113`, `logship.go:203-223`, `logship.go:268-287` | confirmed |
| 7 | Setting `st.renamed = nil` in the fork is enough to let the descriptor go: `release` closes the descriptors no kept state holds. | `state.go:151-166`; `finishRenamed` does the same (`files.go:255`) | confirmed |
| 8 | A run that stopped (budget, time, deadline) does not reach later sources, and `pump` returns `(false, nil)` without reading when the run is stopped or its context ended. | `run.go:125-137`, `files.go:450-452` | confirmed |
| 9 | `pump` advances `ls.read` per full 64 KiB chunk before an error of a later chunk, also inside one long line. | `files.go:449-467` | confirmed |
| 10 | A file whose path cannot be opened, or whose zone is unknown, returns before the renamed file is touched. | `files.go:118-131` | confirmed |
| 11 | `log file readable again` is logged by the first run whose `fproblems[i]` is `""` after a reason; `readFile` sets `""` once the path opened. | `files.go:143`, `logship.go:344-353` | confirmed |
| 12 | `newGap` makes a gap whose end is not after its start end 1 µs after it. | `records.go:51-60` | confirmed |
| 13 | The collector interval is 2 seconds without a pressure interval, so 150 runs are about 5 minutes. | `docs/areas/agent.md:1110` | confirmed |
| 14 | Record 0121 is unreleased (not in `v0.1.0`), so it is extended in place. | `git cat-file -e v0.1.0:docs/decisions/0121-…md` fails; only tag `v0.1.0` | confirmed |

## Acceptance criteria

The criteria are the spec's (AC1 to AC9).

## Tests per criterion

All tests are in `internal/logship/files_test.go` (package `logship_test`), driven by the existing `world`. A read failure is made
through the new seam `logship.SwapReadAt` (export_test.go), which fails the reads of the descriptor whose inode is the renamed
file's (test helper `failRenamedReads`, written by the Tester in `files_test.go`, with a predicate on the offset and a switch to
let reads through again). The existing `TestFiles_ReadErrorOfTheRenamedFileKeepsTheNewFileWaiting` stays unchanged: it fails two runs
only (below the bound) and its restart still reads everything.

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `TestFiles_RenamedFileReadAgainBeforeTheBoundLosesNothing` (149 failing runs, then reads pass: renamed lines then the new file's, in order, no gap, one `log file not readable` and one `log file readable again`) | `internal/logship/files_test.go` |
| AC2 | `TestFiles_RenamedFileFailingAtTheBoundIsGivenUpWithOneGap` (run 149 returns nothing; run 150 returns the held partial line as it is, one gap to the run's start, then the new file's lines; the saved position has no `renamed`; subtests: a held partial line, no held line; a `syslog` group open in the renamed part is closed before the gap) | `internal/logship/files_test.go` |
| AC3 | `TestFiles_GiveUpGapStartsAtTheRenamedPartsLastTime` (subtests: lines of the new file dated later were read while the renamed file still grew — the gap starts at the renamed file's last line; no renamed line read since the rotation — at the last line before it; a renamed line dated after its run — at the line before it; after a restart — at the saved `last_time`) | `internal/logship/files_test.go` |
| AC4 | `TestFiles_RenamedFailCountStartsAnewWhenTheOffsetMoves` (subtests: each run reads whole lines then fails — not given up after 300 runs; a line longer than 64 KiB read a chunk per run then failing — not given up; 149 failures, a run that reads without error, 149 more — not given up) and `TestFiles_RunsThatDoNotReadTheRenamedFileLeaveTheCount` (149 failures, runs with the zone unreadable and with the path missing, then one failure gives up) | `internal/logship/files_test.go` |
| AC5 | `TestFiles_SecondRotationWhileTheRenamedFileFailsGivesItUpAtTheBound` (a third file at the path: at the bound the renamed file is given up, the second file becomes the renamed one and the third is read from its start, in order) | `internal/logship/files_test.go` |
| AC6 | `TestFiles_RestartStartsTheRenamedFailCountAnew` (100 failures, restart, 149 failures — not given up, the 150th — given up; and a renamed file gone at the restart gives today's gap) | `internal/logship/files_test.go` |
| AC7 | `TestFiles_DroppedGiveUpRunIsRepeatedWithOneGap` (run 150 refused with `w.refuse()`, the next run gives up again; the kept records hold exactly one give-up gap and every new-file line once) | `internal/logship/files_test.go` |
| AC8 | `TestFiles_GiveUpOfOneFileLeavesTheOtherFilesAndTheJournal` (two files, one failing renamed file: the other file and the journal ship every line in each run, and the other file's renamed file is finished as usual) | `internal/logship/files_test.go` |
| AC9 | `TestFiles_GiveUpLogsAFixedWarningWithTheFileIndexOnly` (attributes exactly `file` = `0`; `log file readable again` in the give-up run when the new file reads; logged again by the run after a dropped give-up run; the text holds no path) | `internal/logship/files_test.go` |

## Approach

1. **Seam.** `pump` reads through a package variable `readAt` (default `(*os.File).ReadAt`), swapped in tests by `SwapReadAt` in
   `export_test.go` like `SwapRecordCheck`. `hashOfHead` keeps its direct read.
2. **State.** `renamedFile` gains `fails int` (kept failing runs in a row) and `last time.Time` (the renamed part's last time).
   `rotate` sets `last: st.lastTime` when it makes the renamed file; `restoreFile` sets it to the restored `lastTime`. Neither is
   saved; `positionsOf`, `sameFile` and `agentstate` are unchanged. `clone` copies both by value already (fact 6).
3. **Time.** In `line`, when `renamed` is true, `fr.st.renamed.last` moves under the same rule as `st.lastTime` (only when the line
   is not dated after `r.start`). `st.lastTime` keeps today's behavior.
4. **Count.** `readRenamed` and `finishRenamed` note `before := rn.line.read` and hand `pump`'s result to `countRenamedRead`:
   - error and `rn.line.read > before` → `fails = 0`, reason `read failed`, return not given up;
   - error without progress → `fails++`; below `renamedFailsToGiveUp` (150) → reason `read failed`, not given up; at 150 →
     `giveUpRenamed`, given up (the reason is not set, so the reason `""` of the opened path stands);
   - no error and (`done` or progress) → `fails = 0`; no error, no progress, not done (a stopped run) → unchanged.
   `readRenamed` returns true after a give-up, so `read` goes on with the current file unless the run stopped; `finishRenamed`
   returns true, so `rotate` goes on with the rotation (AC5).
5. **Give-up** (`giveUpRenamed`, modelled on `truncate`): ship `rn.line.held` through `fr.line(…, renamed=true)` when it is not
   empty (ignoring its stop, as `truncate` does), `r.closeStages(fileKey(i))`, `r.emitGap(rn.last, r.start)`, `st.renamed = nil`,
   `r.renamedGivenUp[i] = true`. The held line moves `rn.last` first, so the gap starts after it.
6. **Logging.** `run` gains `renamedGivenUp []bool` (made in `newRun`). `logOutcome` logs, per file and before its readable/not
   readable switch, `l.log.Warn(msgRenamedGivenUp, "file", i)` with `msgRenamedGivenUp = "renamed log file given up"`. Layout: one
   slog line, level warning, the single attribute `file`; it comes before the `log file readable again` of the same run. Logged per
   run that gives up (a dropped run's warning is not undone: spec E3).
7. **Dropped runs.** Nothing extra: the count lives in the fork, so a dropped give-up run is repeated from the committed count 149;
   at the fourth dropped run the existing span give-up takes over (spec E3).

## Options considered

The product options (no bound, wall-clock bound, skipping only the bad part, saving the time) are settled in the spec and record 0121
(options 24 to 27). Implementation options:
- **Count in `Logs`, beside the journal's `l.fail` / `countFailure`** (`logship.go:310-322`): it survives a dropped run and would count
  runs whose give-up was undone. Lost to the count in the fork (spec NFR).
- **A seam in `Sources` (a reader per file)** instead of a package variable: it widens the production API for a test need.
  `SwapRecordCheck` sets the precedent of a package-level seam swapped from `export_test.go`. Lost.
- **Fail reads by closing the descriptor** (the existing test's way): it cannot make a failure pass again (AC1, AC4). Kept only for
  the existing test.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| agent (Go) | `internal/logship/files.go` | `readAt` seam in `pump`; `countRenamedRead`, `giveUpRenamed`; `readRenamed`, `finishRenamed`, `rotate`, `line` changed |
| agent (Go) | `internal/logship/state.go` | `renamedFile.fails`, `renamedFile.last` |
| agent (Go) | `internal/logship/positions.go` | `restoreFile` sets `renamed.last` |
| agent (Go) | `internal/logship/run.go` | `run.renamedGivenUp`, made in `newRun` |
| agent (Go) | `internal/logship/logship.go` | constants `renamedFailsToGiveUp`, `msgRenamedGivenUp`; `logOutcome` logs the give-up |
| agent (Go) | `internal/logship/export_test.go` | `SwapReadAt` |

## Signatures (for the Dev's skeleton)

Methods are written as (*filesRun).name(...): declare them as func (fr *filesRun) name(...).

- `var readAt = (*os.File).ReadAt` (files.go; the method expression, of type func(*os.File, []byte, int64) (int, error))
- `(*filesRun).countRenamedRead(ctx context.Context, rn *renamedFile, before int64, done bool, err error) bool` (files.go; true when the renamed file was given up)
- `(*filesRun).giveUpRenamed(ctx context.Context)` (files.go)
- `type renamedFile struct { f *os.File; id identity; line lineState; size int64; fails int; last time.Time }` (state.go; two fields added)
- `type run struct { …; renamedGivenUp []bool }` (run.go; one field added)
- `const renamedFailsToGiveUp = 150` and `const msgRenamedGivenUp = "renamed log file given up"` (logship.go)
- `func SwapReadAt(read func(f *os.File, p []byte, off int64) (int, error)) (restore func())` (export_test.go)

The signatures of readRenamed, finishRenamed, rotate, line, restoreFile and logOutcome stay as they are. No file of the change is final
as it stands; the skeleton adds the members above with empty bodies (`countRenamedRead` returns false, `giveUpRenamed` does nothing).

## Test files

- `internal/logship/files_test.go` — every test of *Tests per criterion* and the helper `failRenamedReads` (Tester).
- `internal/logship/export_test.go` — `SwapReadAt` (Dev, in the skeleton: it is the seam's test half).

Existing test call sites of a changed signature: none (no exported signature changes; `renamedFile` and `run` are unexported and
built only in production code).

Files no unit test can cover: none.

## Areas

Agent ([docs/areas/agent.md](../../docs/areas/agent.md)), *Log shipping*: the bound and the give-up (below).

## Documentation updates

All by the Dev once the code is green, against that code. Sweep done for every "only these"/list statement the change extends:

- `docs/areas/agent.md`
  - *Rotation* (lines 1271-1274): the sentence "While the renamed file cannot be read … never shipped after the newer file's" gains the
    bound: at the 150th kept run in a row whose read fails without moving its offset the rest is given up with one gap and the file at
    the path is read on in that run; what starts the count anew and what leaves it; the renamed part's last time (set at the rotation,
    moved only by its lines; after a restart the saved last time).
  - *A file's last time* (lines 1291-1299): the renamed part's last time follows the same bound; the list of gaps whose start is bounded
    (line 1297) gains the give-up of a renamed file.
  - *Gaps of the log collector* (lines 1343-1362): a new bullet for the give-up of a renamed file (from the renamed part's last time to
    the run's start, its held line shipped as it is first).
  - The paragraph "No gap is recorded while a source cannot be read" (lines 1364-1367): a read error of a renamed file records no gap
    until the bound.
  - *Logging* table (lines 1372-1379): row warning `renamed log file given up` | `file` | each run that gives a renamed file up; the
    paragraph after it (lines 1381-1384): "the next run tries again" gains "for at most 150 runs, then the rest is given up".
  - *Classification*, *When a group closes* (lines 1416-1418): the give-up gap joins the gaps before which a group closes.
  - *Residuals (record 0121)* (lines 1460-1461): the hold-back bullet becomes: the count is not saved, so an agent restarted within
    every 150 runs never gives up, and after a restart the gap starts at the saved last time, which can lie after lines it loses.
- `deploy/agent/README.md`, *What it logs* (lines 481-484): add `renamed log file given up` (with the index of the file).
- `.squad/project.md`, area 12 (line 276): the list of the collector's fixed texts gains `renamed log file given up` with the index of
  the file.
- `docs/ARCHITECTURE.md`: no change (no guarantee or flow changes; grep for "renamed", "read error" found nothing).
- `docs/UNIT_TESTS.md`: no change.

## Architecture check

The guarantee *No data gaps unless explicitly recorded* holds: the lines given up are covered by one gap, which starts at or before
every lost line except after a restart (stated residual, record 0121). *Collector isolation* holds: the give-up affects one
configured file only (AC8). No new file is opened or written; the state file keeps its shape and bounds; the agent's memory grows by
one `int` and one `time.Time` per renamed file per state copy.

## Security considerations

- [ ] No limits on parsed input are added or changed; the line rules and the 16,385-byte held line are unchanged.
- [ ] No exception or error text reaches a user: the read error is reported only as the fixed reason `read failed`, the give-up only
  as the fixed text `renamed log file given up` with the file's index; no path, file content or error text is logged.

## Decision records

- `docs/decisions/0121-log-shipping-journal-and-syslog-files-positions-per-seqnum-id-zstd-commit-when-kept.md` (Accepted, unreleased)
  — extended in place for #291: *Source*, options 24 to 27, a *Decision* paragraph and two *Consequences* bullets; its index row is
  unchanged.

## Out of scope / follow-ups

As in the spec, *Out of scope*. No follow-up issue.
