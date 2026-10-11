# Plan: Print the import summary when pass 2 is interrupted outside the parser

Source: Issue #191
Status: Draft
Tier: standard — a behavior change on the cancellation path of the log import (the run now returns a summary instead
of throwing); it changes no file access, path handling, parsing bound or output escaping, so no security area is
touched.

## Problem / root cause

Pass 2 (`src/Vandox.Import/ImportPass.cs`) turns a cancellation into an interrupted run only where it expects one: in
the parser (`ParseAsync`, line 112), the skip to the continuation point (`ImportContentAsync`, line 466) and the store
calls (`BeginAsync` line 354, `StoreReason` line 544, the emitter's sticky `Error`). Two other places that honor the
token let the `OperationCanceledException` escape:

1. **Between archive entries.** `ReadArchiveAsync` calls `reader.GetNextEntryAsync(false, cancellationToken)` at
   **line 283** (the issue says 277, which is the `OpenRegular` call of the same method). With a cancelled token it
   throws `TaskCanceledException`; `ImportArchiveAsync` (line 248) catches only `IsFileFailure` exceptions, and
   `RunAsync` (line 48) catches nothing.
2. **During the verification read.** `SettleAsync` calls `emitter.VerifyAsync(cancellationToken)` (line 143), which
   reads the unread rest of the content (`CopyToAsync` -> `ContentStream.ReadAsync` -> `FileStream.ReadAsync`). Its
   catches (lines 153 and 157; the issue says 151) take only I/O exceptions and, when `emitter.Error` is set, the
   store's error. A cancelled read with no store error escapes through `JudgeAsync`, `ImportContentAsync` and
   `ImportPlainAsync`/`ImportEntryAsync` (whose catches are `IsFileFailure` only).

`Importer.RunAsync` calls the pass outside any `try` (`src/Vandox.Import/Importer.cs` line 69), so the exception leaves
`Importer.RunAsync`; `ImportCommand.RunAsync` catches it at `src/Vandox.Backend/Cli/ImportCommand.cs` line 76, logs
"import interrupted" and returns 1 without printing a summary.

Claims of the issue, checked:

- `GetNextEntryAsync` lets the cancellation escape between entries — **confirmed** (by reading, and by a scratch run:
  `TarReader.GetNextEntryAsync` with a cancelled token throws `TaskCanceledException`). Line number off (283, not 277).
- `SettleAsync` catches only I/O exceptions — **confirmed**, with one refinement: a cancellation of the line-hash write
  (`ContentStream.WriteQueuedAsync` -> `RecordEmitter.WriteLineHashesAsync`) sets `emitter.Error` and is handled by the
  second catch; only a cancelled **read** escapes. Scratch run: `FileStream.ReadAsync` (handle, `bufferSize` 1, as
  `SecureRoot.OpenRegular` builds it) throws `TaskCanceledException` and `CopyToAsync` an `OperationCanceledException`
  with a cancelled token. The read happens only when the parser left content unread (`ContentStream.ReadAsync` returns 0
  without reading once the hashed size is reached). Line number off (153, not 151).
- `Importer.RunAsync` does not catch it after the scan — **confirmed** (line 69).
- `ImportCommand` logs "interrupted" and returns 1 without the summary — **confirmed** (lines 76-80).
- `docs/areas/log-import.md` (*Result*) says the summary is still printed — **confirmed** (line 443: "the summary is
  still printed when there is one"; line 435: the summary says "whether the run was interrupted").

Related observations (not changed here):

- Between two **plain** files the cancellation is already handled: the next file's `BeginAsync` fails with "interrupted"
  through `StoreReason` (with `FakeImportStore`, which ignores the token, the parser's read raises it instead). So an
  interruption between plain files lists the next file as failed "interrupted", while one between archive entries
  lists no further entry. Both are honest; the area text below covers both.
- `StoreReason` (line 546) treats an `OperationCanceledException` from the store as an interruption even when the run's
  token is not cancelled; `Importer` then reports `Interrupted = false` with that exception as the run error ("import
  failed"). No current store throws it without the token, so this is noted only.

## Acceptance criteria

- [ ] AC1: Cancelled between two recognized entries of a tar archive (after the first entry finished), `Importer.RunAsync`
  returns (does not throw) a run with `Summary.Interrupted == true` and `Error` an `OperationCanceledException`; the
  first entry is in `Summary.Files` as `Imported` with its records; the second recognized entry is not in
  `Summary.Files` and none of its records is stored.
- [ ] AC2: Cancelled after the parser returned with content left unread, so the cancellation hits the verification read,
  `Importer.RunAsync` returns a run with `Summary.Interrupted == true` and `Error` an `OperationCanceledException`; the
  file is in `Summary.Files` as `Failed` with reason exactly `interrupted`, and it is not completed in the store (a later
  run without cancellation imports it to the end, each record stored once).
- [ ] AC3 (backstop): any `OperationCanceledException` that still escapes pass 2 while the run's token is cancelled ends
  the run as interrupted: `Importer.RunAsync` returns a run with `Summary.Interrupted == true`, `Error` the exception,
  and the summary of the files that have an outcome. An `OperationCanceledException` while the token is **not**
  cancelled is not caught by this backstop (unchanged).
- [ ] AC4: `vandoxd import` interrupted during the verification read (AC2's situation, end to end with the real store)
  exits with 1, logs "import interrupted" on standard error and prints the summary on standard output, containing
  `The import was interrupted; run it again to continue.`

## Approach

1. `ImportPass.SettleAsync`: extend the first catch's filter so that an `OperationCanceledException` while
   `cancellationToken.IsCancellationRequested` is returned like an I/O exception:
   `catch (Exception exception) when (exception is IOException or InvalidDataException or EndOfStreamException || (exception is OperationCanceledException && cancellationToken.IsCancellationRequested))`.
   `JudgeAsync` already turns a returned error under a cancelled token into `_fatal = new OperationCanceledException(cancellationToken)`
   and the reason "interrupted" (and a cancelled line-hash write still goes through `emitter.Error` -> `StoreReason`, same
   outcome). Writing the filter positively keeps RH3001/S1125 quiet; the Code Officer may reflow it, the semantics are fixed.
2. `ImportPass.ImportArchiveAsync`: add a second catch after the `IsFileFailure` one:
   `catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { _fatal = new OperationCanceledException(cancellationToken); }`.
   The existing `if (_fatal is not null) return end;` then leaves the unreached entries without an outcome, as after an
   interruption inside an entry today.
3. `Importer.RunAsync`: wrap the pass-2 call (line 69) in `try` with
   `catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested) { error = exception; }`,
   as the scan call above it does, so `Summary.Interrupted`, `Summarize` and `Finished` run for any cancellation that a
   future code path lets escape. Points 1 and 2 stay necessary: without them the file being verified would be missing
   from the summary (no outcome) although records of it were stored.
4. `ImportCommand` is unchanged: it prints the summary whenever `Summary.Interrupted` is set (line 126); its catch at line
   76 stays for the cancellation of `SqliteStore.OpenAsync`.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Vandox.Import | `src/Vandox.Import/ImportPass.cs` (`SettleAsync`, `ImportArchiveAsync`) | catch the cancellation (body changes only) |
| Vandox.Import | `src/Vandox.Import/Importer.cs` (`RunAsync`) | backstop catch around pass 2 (body change only) |
| docs | `docs/areas/log-import.md` | *Result*: interruption bullet |

## Signatures (for the Dev's skeleton)

None: no new or changed member; only method bodies change. Step 4 (*Skeleton*) has nothing to build.

## Test files

- `tests/Vandox.Import.Tests/ImporterArchiveTests.cs` — AC1: `ImporterReturnsInterruptedSummaryWhenCancelledBetweenArchiveEntries`.
  Setup: `TestInputs.Tar` with two recognized entries (e.g. `a.log` with `TestInputs.Log(2)`, `b.log` with
  `TestInputs.Log(3, "LOG b")`); the `tweak` sets `Progress` to a callback that records the event and cancels the run's
  `CancellationTokenSource` on the first `ProgressEvent.FileFinished`. Fails on the current code with
  `TaskCanceledException` out of `RunAsync`.
- `tests/Vandox.Import.Tests/ImporterStoreTests.cs` (where the other interruption tests live):
  - AC2: `ImporterReturnsInterruptedSummaryWhenCancelledDuringTheVerificationRead`. Setup: `ProbeParser("test", stopAfter: 1)`
    registered through the `tweak` (`options.Parsers = new ParserRegistry([probe])`), `options.BatchRecords = 1`,
    `harness.Store.AfterBatch` cancels the token on the first batch; the file must be **larger than 16 KiB** (the
    `LogLineReader` read buffer, so content is still unread when the parser stops) and have **fewer than 4,096 lines**
    (so no line-hash row is queued and the cancellation hits the read, not the store), e.g. `TestInputs.Log(2000)`.
    Then a second run with an uncancelled token completes it (resume check of AC2). Fails on the current code with
    `TaskCanceledException`.
  - AC3: `ImporterReturnsInterruptedSummaryWhenACancellationEscapesPassTwo`. Setup: two plain files; `Progress` cancels the
    source and throws `new OperationCanceledException(token)` on the first `ProgressEvent.FileStarted` (a path no catch
    of `ImportPass` covers). Assert `Interrupted`, `Error` is the thrown exception, and the run returned. Second case
    (same file or `[DataRow]`-free second method, Tester's choice):
    `ImporterLetsACancellationExceptionWithoutCancelledTokenEscape` — the callback throws `OperationCanceledException`
    without cancelling; `Importer.RunAsync` throws it (pins that the backstop's filter is on the token).
- `tests/Vandox.Backend.Tests/ImportCommandTests.cs` — AC4: `ImportCommandPrintsSummaryWhenInterruptedDuringTheVerificationRead`,
  with a new parser double `tests/Vandox.Backend.Tests/CancelAfterReadParser.cs` (claims `LOG` heads like
  `CancellingParser`, reads a few bytes of the input, cancels its `CancellationTokenSource` and returns without
  exception). Input `a.log` with at least two lines so content is left unread. Assert exit code 1, the log line and the
  summary text, and that the summary names `a.log` with the reason `interrupted`.

Existing test code that calls a changed signature: none.

## Areas

`docs/areas/log-import.md`, *Result*: the behavior on interruption is stated once. Owner: **Dev** (step 6). Insert
after the *The summary* bullet (before *Exit code*):

> - **Interruption** (Ctrl-C) anywhere in pass 2 — while a file is parsed, verified or stored, between two files or
>   between the entries of an archive — stops the run and still prints the summary of what was done, which says that
>   the run was interrupted. The file being imported when the interruption arrives is listed as failed with the
>   reason `interrupted` and is resumed by the next run. Entries of an archive not reached are not listed; a plain file
>   reached after the interruption may be listed as failed with the reason `interrupted`, and is likewise imported by the
>   next run.

## Documentation updates

Only the area document above (owner Dev). No `README.md`, no `docs/ARCHITECTURE.md` change.

## Architecture check

No guarantee in `docs/ARCHITECTURE.md` or `.squad/project.md` is touched. Resumability (record 0069) is preserved: the
interrupted file is not completed (the completing batch is written only after a successful verification), so the next
run resumes it by count. Exit code 1 on interruption (record 0072) is unchanged. The summary is written through the
existing `ImportSummaryWriter` (`Terminal.Quote`), so security area 12 is unaffected.

## Security considerations

- [x] No limits: no byte or length limit is added or changed.
- [x] Exceptions reaching the user: the caught `OperationCanceledException` reaches the user only as the existing reason
  text `interrupted` in the summary and the existing log message "import interrupted"; never its message.

## Decision records

None: no decision beyond the obvious fix (the code is brought in line with `docs/areas/log-import.md` and record 0072).

## Challenge

Devil's Advocate: 0 major, 1 minor.

- **Minor — the area bullet contradicts *Related observations*.** The planned sentence "recognized files not reached are
  not listed" is wrong for plain files: after an interruption between two plain files, the next file's `BeginAsync`
  (`ImportPass.cs` line 354) fails through `StoreReason` (line 546, `cancellationToken.IsCancellationRequested`), so that
  file is listed as failed `interrupted`. **Accepted** (re-checked against the code). The bullet in *Areas* now says that
  unreached archive entries are not listed and that a plain file reached after the interruption may be listed as failed
  with the reason `interrupted` and is imported by the next run ("may": whether a file is reached first depends on where
  the token is observed). No change to the acceptance criteria, the approach, the tests or the tier.

## Out of scope / follow-ups

- The `StoreReason` handling of a store `OperationCanceledException` without a cancelled token (see *Related
  observations*): unreachable with the current stores, no follow-up issue.
