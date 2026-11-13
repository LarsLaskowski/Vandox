# Plan: Dispose the GZipStream that Scanner.StreamAsync opens for gzip content

Source: Issue #433
Tier: standard — the fix changes the control flow of `Scanner.StreamAsync` (a disposal scope around the classification) and creates, changes or removes no security control.
Security areas touched: 9. File writes and paths derived from external input (`src/Vandox.Import/` — only the lifetime of a decompressor in pass 1; no path, write or limit changes)

## Problem / root cause

`Scanner.StreamAsync` (`src/Vandox.Import/Scanner.cs:471`) creates `new GZipStream(content, CompressionMode.Decompress)`
inside the `if (format == ContentFormat.Gzip)` block, wraps it in a `PrefixStream` (line 475) and hands that to
`ClassifyAsync` (line 478). No code calls `Dispose` on it: the `PrefixStream` and every later wrapper is a `FilterStream`
that leaves its inner stream to the caller, and `StreamAsync` itself never disposes it. The zlib inflater of every scanned
gzip file or gzip archive entry is released only by the finalizer.

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | `StreamAsync` creates the `GZipStream` without `leaveOpen` and never disposes it | `Scanner.cs:459-479` | confirmed |
| 2 | `PrefixStream` does not dispose its inner stream | `PrefixStream.cs` has no `Dispose`; `FilterStream.cs` has none either and suppresses CA2213 for `_inner` ("The caller owns the inner stream"), record 0205 | confirmed |
| 3 | `HashingStream` disposes only its hash | `HashingStream.cs:149-157` | confirmed |
| 4 | `TarReader` is opened with `leaveOpen: true`, over a `TarHeaderGuardStream` (a `FilterStream`) | `Scanner.cs:150` | confirmed |
| 5 | The leak holds on every path: tar, every unrecognized reason, parsed, failure | `ClassifyAsync`, `Scanner.cs:491-531`: no branch disposes `content`; an exception from `ReadHeadAsync` or `ClassifyAsync` leaves the same way | confirmed |
| 6 | The raw stream stays with its owner: the file by `RegularFileAsync` (`await using`, `Scanner.cs:394-395`), a tar entry's data stream by the `TarReader` | `Scanner.cs:220`, `394-395` | confirmed |
| 7 | The import pass already disposes its decompressor, created with `leaveOpen: true` | `ImportPass.cs:80-82`, disposed at `210-211`, `285-286`, `326-327` | confirmed |
| 8 | Disposing the `GZipStream` with `leaveOpen: true` does not touch its inner stream | `GZipStream(Stream, CompressionMode, bool)` documentation: `leaveOpen` true leaves the stream open after the `GZipStream` is disposed | confirmed |
| 9 | A disposal placed before `ClassifyAsync` has finished fails the existing gzip tests: a read of a disposed `GZipStream` throws `ObjectDisposedException`, which `IsFailure` (`Scanner.cs:91-94`) does not catch and `Importer.RunAsync` (`Importer.cs:38`, `54`, `74`) does not catch either, so the run throws | code read | confirmed |
| 10 | No unit test can observe the disposal today: the `GZipStream` is a local of a private method, handed to no parser (`Detect` gets the head only, `Scanner.cs:545`), and disposing it with `leaveOpen: true` changes nothing a caller can see | `Scanner.cs:459-599` | confirmed |
| 11 | Related defects: none. Every other stream the scan and import pass open is disposed by its creator or is a `FilterStream` without resources (`ErrorTrackingStream`, `TarHeaderGuardStream`) | `Scanner.cs`, `ImportPass.cs` read in full for `new`/`Dispose` | confirmed |

## Acceptance criteria

- AC1: For gzip content — a gzip file or a gzip entry of an archive — `Scanner.StreamAsync` creates the `GZipStream` with
  `leaveOpen: true` and disposes it once `ClassifyAsync` has returned or thrown, on every path: tar archive, listed as
  unrecognized (compressed twice, empty, unsupported, no parser), hashed by a parser, and failed (corrupt or truncated
  gzip, an error of the raw content).
- AC2: The `GZipStream` stays open until the classification is done: a gzip file is still hashed and imported, a
  `.tar.gz` archive's entries are still read, a gzip entry of a tar archive is still imported, and gzip inside gzip is
  still listed as compressed twice.
- AC3: The gzip layer leaves the raw content to its owner: after a gzip entry the archive's next entry is still read,
  and after a corrupt gzip file the next file is still imported.

## Tests per criterion

No test can fail before the fix (fact 10): AC2 and AC3 are guarded by existing tests, which the Tester tags with the
criterion id (`[TestCategory("AC2")]` / `[TestCategory("AC3")]`, `docs/UNIT_TESTS.md`, *Criterion ids*) and does not
otherwise change.

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | manual: the Reviewer in step 8, on the branch head diff — the `GZipStream` is constructed with `leaveOpen: true`, its only disposal is an `await using` whose scope ends after the `ClassifyAsync` call, and no path returns from the gzip branch outside that scope | — |
| AC2 | ImporterDecompressesGzipFiles; ImporterListsFilesItCannotImport; ImporterImportsArchiveEntries; ImporterContinuesGzipFilesAndArchiveEntriesPerDecompressedContent | `tests/Vandox.Import.Tests/ImporterDirectoryTests.cs`, `tests/Vandox.Import.Tests/ImporterArchiveTests.cs` |
| AC3 | ImporterImportsArchiveEntries; ImporterFailsCorruptGzip | `tests/Vandox.Import.Tests/ImporterArchiveTests.cs` |

## Approach

Only `StreamAsync` changes. The gzip layer gets one owner, an `await using` whose scope encloses the `ClassifyAsync`
call; a scope inside the `if` block would end before the classification and fail AC2 (fact 9). Shape for the Dev:

1. Read and sniff the head and build `PrefixStream(head, raw)` as today.
2. When the format is not gzip, call `ClassifyAsync` with it and return.
3. Otherwise set `item.Location.Gzip` and trim the name as today, create
   `new GZipStream(content, CompressionMode.Decompress, leaveOpen: true)`, follow it with
   `await using var gzipDisposal = gzip.ConfigureAwait(false);` (the repository's pattern, `Scanner.cs:151`, `395`), read
   and sniff the inner head, and call `ClassifyAsync` with `PrefixStream(innerHead, gzip)` inside that scope.

A private helper for the gzip branch is the Dev's choice, provided the `await using` and the `ClassifyAsync` call stay
in the same method. Disposal in decompress mode reads and writes nothing, so it cannot replace an exception of the
classification. No user-visible output changes.

## Options considered

1. **`await using` in `StreamAsync`, `leaveOpen: true`** (chosen) — what the issue proposes and what `ImportPass` does
   (fact 7).
2. **`leaveOpen: false` (the default) with the same disposal** — rejected: it would dispose the `PrefixStream` over the
   raw content, harmless today only because `FilterStream` ignores its disposal (record 0205); `leaveOpen: true` states
   the ownership the code relies on and matches the import pass.
3. **A unit test that observes the disposal, as the issue asks** — needs a seam the test can reach, since the
   `GZipStream` is private to the call (fact 10): either an internal decompressor factory on `ImportOptions` that the
   test replaces with a recording wrapper, or an internal helper taking the classification as a delegate, whose test
   checks `CanRead` of the stream it hands over. Rejected: both add an internal member that exists only for the test,
   and neither removes the review check, since whether `StreamAsync` uses the hook on every path is again read in the
   diff. The part of the fix that can break behavior — disposal too early — is caught by the existing tests (AC2).

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Vandox.Import | `Scanner` (`src/Vandox.Import/Scanner.cs`) | `StreamAsync` owns and disposes the `GZipStream` |
| Vandox.Import.Tests | `ImporterDirectoryTests`, `ImporterArchiveTests` | criterion tags on existing tests (Tester) |

## Signatures (for the Dev's skeleton)

None: no new or changed API member. `StreamAsync` keeps its signature; the skeleton rewrites no file.

## Test files

`tests/Vandox.Import.Tests/ImporterDirectoryTests.cs` and `tests/Vandox.Import.Tests/ImporterArchiveTests.cs` (tags
only).

Existing test code that calls a changed signature: none.

Files of the change no unit test can cover: none (the changed lines run in the tests of AC2 and AC3; only the disposal
itself is unobservable, AC1).

## Areas

None: no change in behavior. `docs/areas/log-import.md` describes formats, limits and outcomes, none of which changes.

## Documentation updates

None. The sweep of `docs/areas/` and `docs/ARCHITECTURE.md` for gzip, decompression and disposal found no statement this
fix changes; `CHANGELOG.md` is written per release (`docs/CONTRIBUTING.md`, *Cutting a release*), and the PR template's
checklist asks for nothing else here.

## Architecture check

No guarantee of `docs/ARCHITECTURE.md` or `.squad/project.md` is touched: pass 1 reads the same bytes in the same order;
only the decompressor's native state is released at the end of each file instead of by the finalizer.

## Security considerations

- [x] No limits: no byte or length limit is added or changed.
- [x] None: no new exception reaches a failure reason; the disposal in decompress mode throws nothing the
  classification did not.

## Decision records

- none: no decision beyond the obvious fix (the rejected test seam is a test strategy, recorded in this plan).

## Out of scope / follow-ups

None.
