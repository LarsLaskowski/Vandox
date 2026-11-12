# Plan: Meet CA2213 (disposable fields are disposed)

Source: Issue #429
Tier: trivial — six justified suppressions and the rule's severity, no change to behavior or control flow, but the ownership choice needs a decision record.
Security areas touched: build configuration (`.editorconfig`, CA2213 raised from `suggestion` to `warning`); 9 File writes and paths derived from external input (`src/Vandox.Import/FilterStream.cs`, a suppression only)

## Problem / root cause

CA2213 reports six disposable fields the declaring type's `Dispose` never disposes; five hold objects their callers own, one is a gate whose disposal would break a waiting render (record 0205).

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | Six CA2213 findings, the issue's sites | Release build with SARIF (analyzer gate's build line): ChartInterop.cs:24 `_gate`, JsonLineLoggerProvider.cs:14 `_writer`, ActionSequence.cs:14 `_current`, GzipMemberReader.cs:61 `_input`, FilterStream.cs:10 `_inner`, HookedStream.cs:10 `_inner` | confirmed |
| 2 | The writer is standard error, shared by two factories | BackendApp.cs:97,111; StoreCommand.cs:32,46; ImportCommand.cs:34,49 | confirmed |
| 3 | The decoder leaves its input open | BatchDecoder.cs:63-64 doc, :82 | confirmed |
| 4 | The caller owns the run | ActionSequence.cs:45; PageRunner.cs:97 `using var run` | confirmed |
| 5 | FilterStream callers dispose the inner streams themselves | ImportPass.cs:283-288; Scanner.cs:150-151, 395; the `GZipStream` at Scanner.cs:471 is not disposed on any path (follow-up issue) | confirmed |
| 6 | Tests read the inner stream after the wrapper | ContentStreamTests.cs:313,370 (`inner.Consumed`) | confirmed |
| 7 | The gate never creates a wait handle; disposing it fails a waiting render's `Release` (ChartInterop.cs:101) | no `AvailableWaitHandle` in the repository; SemaphoreSlim creates its handle only there and throws `ObjectDisposedException` from `Release` after `Dispose` (runtime source) | confirmed |
| 8 | Each diagnostic sits on the field declaration, so a pragma around it suppresses it | SARIF line numbers = declaration lines | confirmed |

## Acceptance criteria

- AC1: With CA2213 at the baseline's `warning`, a full Release build of `Vandox.slnx` reports no CA2213.
- AC2: `DOTNET_RULES_PENDING` holds no `CA2213` entry, and `python3 .squad/tools/dotnet-rules.py --check` exits 0.
- AC3: No behavior changes: the existing suites pass unchanged.

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | CI: *Analyzer gate* (Code Officer, step 7) and the CI build | — |
| AC2 | CI: `dotnet-rules.py --check` and `config-check.py` (orchestrator, step 7) | — |
| AC3 | CI: *Test* — existing `ChartInteropTests`, `JsonLineLoggerTests`, `GzipMemberReaderTests`, `ContentStreamTests`, `TarHeaderGuardStreamTests`, `PageRunnerTests` | — |

No new unit test: the edits add no executable line.

## Approach

Dev wraps each field declaration (after its XML comment, as `SignatureFieldType.Integer`) in this form, comment texts exact:

```
#pragma warning disable CA2213 // <comment>
<field>
#pragma warning restore CA2213
```

- `ChartInterop._gate`: `The gate never creates its wait handle; disposing it would fail a render still waiting when the interop is disposed`
- `JsonLineLoggerProvider._writer`: `The caller owns the writer: standard error, shared by the bootstrap and the service logger`
- `ActionSequence._current`: `The caller of BeginAsync owns and disposes the run`
- `GzipMemberReader._input`: `The caller owns the input; BatchDecoder leaves it open`
- `FilterStream._inner`: `The caller owns the inner stream`
- `HookedStream._inner` (Tester, test path): `The test owns the inner stream and reads it after the wrapper`

Then the orchestrator writes the Dev's reported edit of `.squad/tools/squad_settings.py` (`DOTNET_RULES_PENDING = {"MSTEST0001": "#430"}`) and runs `python3 .squad/tools/dotnet-rules.py`, which rewrites the CA2213 line of `.editorconfig` to `warning` and drops its `# pending` comment; then *Format* and the gates.

## Options considered

In record 0205: dispose every field, remove the disposable field types, switch the rule off (all rejected), suppress with the owner named (chosen).

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Vandox.Backend | ChartInterop, JsonLineLoggerProvider, ActionSequence | suppression |
| Vandox.Core | GzipMemberReader | suppression |
| Vandox.Import | FilterStream | suppression |
| Vandox.Import.Tests | HookedStream | suppression |
| repository | `.squad/tools/squad_settings.py`, `.editorconfig` | pending entry gone, severity `warning` |

## Signatures (for the Dev's skeleton)

None: no API member changes.

## Test files

None new. Existing test code that calls a changed signature: none.

Files of the change no unit test can cover: none (no executable line changes).

## Areas

None: no change in behavior.

## Documentation updates

- `.squad/stack.md`, *Writing code*, after the CA2100 bullet (Dev reports, orchestrator writes): "- a disposable field the type does not own (a wrapped stream, a writer or a run its caller disposes) is declared between `#pragma warning disable CA2213 // <who owns it>` and `#pragma warning restore CA2213`; a field the type owns is disposed in its `Dispose` (record 0205);"
- Changelog: none (not user-visible).

## Architecture check

No guarantee touched: no ownership or disposal order changes.

## Security considerations

- [x] No limits.
- [x] No exceptions: none reaches a user.

## Decision records

- `docs/decisions/0205-disposable-fields-meet-ca2213-through-suppressions-that-name-their-owner.md` (Proposed)

## Out of scope / follow-ups

- MSTEST0001 (#430).
- Disposing the `GZipStream` in `Scanner.StreamAsync` (follow-up issue, Lead decision on Security's plan-review note).

## Security notes

Security (plan review, APPROVED), non-blocking: plan fact 5 is inaccurate. It cites Scanner.cs:464-475 as proof that FilterStream callers dispose the inner streams, but `var gzip = new GZipStream(content, CompressionMode.Decompress);` at Scanner.cs:471 is never disposed on any path (Tar, Unrecognized or `RecognizeAsync`); `HashingStream` does not dispose its inner stream and `TarReader` is `leaveOpen: true`, so the zlib inflater's state is freed only by the finalizer, once per gzip file scanned. The suppression neither causes nor hides this, and CA2000 is not set in `.editorconfig`. Smallest fix: change fact 5's evidence to "ImportPass.cs:283-288; Scanner.cs:150-151, 395; the `GZipStream` at Scanner.cs:471 is not disposed (follow-up issue)". Recommendation: open a linked issue to dispose `gzip` in `StreamAsync` (`await using`, with the wrapped `PrefixStream` left open through `leaveOpen: true`); it is a behavior change in an area-9 path and does not belong in this trivial-tier change.
