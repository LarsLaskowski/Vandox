# 0206: Test assemblies run their tests one at a time, stated explicitly

- **Status:** Proposed
- **Date:** 2026-11-12
- **Area:** —
- **Source:** Issue #430
- **Supersedes:** —

## Context

MSTEST0001 (*Explicitly enable or disable tests parallelization*) reports a test assembly that carries neither
`[assembly: Parallelize]` nor `[assembly: DoNotParallelize]`; the template's rule baseline
([0203](0203-two-language-toolchain-on-the-templates-shared-dotnet-rules.md)) lists it, pending on issue #430. Without
the attribute MSTest runs the tests of one assembly sequentially, and no run settings of the repository
(`coverlet.runsettings`) turn parallelization on, so all four test projects run one test at a time today.

Several tests read process-wide state that a concurrent test would disturb: heap bounds through
`GC.GetTotalMemory(true)` (Backend `HeapSampler`, Core `KernelReportGrouperTests` and eight more Core classes),
allocation bounds through `GC.GetTotalAllocatedBytes(true)` (Backend `IngestEndpointConcurrencyTests`,
`IngestEndpointTests`, Core `BatchDecoderTests`, `JournalExportReaderTests`, `MariaDbErrorLogParserTests`), duration
ratios and timeout races with `Stopwatch` (Core `LogTemplateNormalizerTests`, `MailLineClassifierTests`, Storage
`SqliteStoreTests`, `WriteThroughputTests`), and `SqliteConnection.ClearAllPools()`, which clears every pool of the
process, in the `TempDirectory` of Backend, Storage and Import. Import has no heap, allocation or timing bound, but its
`ClearAllPools()` call has not been shown harmless beside a concurrent test that holds a pooled connection.

## Options considered

1. **`[assembly: Parallelize]` in every project** — rejected: heap and allocation bounds and duration ratios would
   count the work of concurrent tests and fail at random.
2. **`[assembly: Parallelize]` in Import only, `DoNotParallelize` elsewhere** — rejected for now: the safety of the
   process-wide pool clearing under concurrency is unmeasured, the suite is small, and the gain unmeasured; flipping the
   one attribute later needs only a repeated-run check of the suite.
3. **`[Parallelize]` with `[DoNotParallelize]` on the measuring test classes** — rejected: every new heap, allocation
   or timing test would have to remember the opt-out, and forgetting it fails only at random.
4. **Switch MSTEST0001 off** — rejected: the rules are set only in the template's blocks (`.squad/stack.md`,
   *Analyzer gate*).
5. **`[assembly: DoNotParallelize]` in each test project** — chosen.

## Decision

Each of the four .NET test projects declares `[assembly: DoNotParallelize]` in its own `MSTestSettings.cs`, so its
tests keep running one at a time, now by declaration instead of by MSTest's default.

## Consequences

- No test changes behavior; measuring tests may keep reading process-wide counters.
- The suites stay as slow as today; a project that wants parallel tests changes its one attribute after a
  repeated-run check, and moves process-wide measurements out of its parallel tests first.
- A new test project needs the same file (`docs/UNIT_TESTS.md`, *Where tests live*).
