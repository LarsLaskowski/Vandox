# Plan: Meet MSTEST0001 (explicit test parallelization)

Source: Issue #430
Tier: trivial — four assembly attributes that keep today's sequential test runs and the rule's pending entry, no change to behavior or control flow, but the choice against parallel runs needs a decision record.
Security areas touched: build configuration (`.editorconfig`, the pending MSTEST0001 line removed by `dotnet-rules.py`)

## Problem / root cause

No test assembly declares its parallelization, so MSTEST0001 fires once per test project (`tests/Vandox.*.Tests`, no location). Whether each may run in parallel is decided in record 0206.

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | Four MSTEST0001 findings, one per test project, level `note`, no location | SARIF logs of the gate's Release build (`tests/Vandox.{Backend,Core,Import,Storage}.Tests/obj/roslyn.sarif`, 16:05 today); no `[assembly:` line in any test source since (grep) | confirmed |
| 2 | Tests run sequentially today | rule text in the SARIF: "By default, MSTest runs tests within the same assembly sequentially"; `coverlet.runsettings` and CI `ci.yml:75,127` set no parallelization; no `testconfig.json` | confirmed |
| 3 | Backend, Core and Storage read process-wide counters or time | `HeapSampler.cs:49`, `IngestEndpointConcurrencyTests.cs:177-196`; `KernelReportGrouperTests.cs:490` (and 8 Core classes), `BatchDecoderTests.cs:2086`; `SqliteStoreTests.cs:48` (busy-timeout race), `WriteThroughputTests.cs:62` | confirmed |
| 4 | Import has no heap, allocation or timing bound, but clears all SQLite pools of the process | grep of `tests/Vandox.Import.Tests`; `TempDirectory.cs:35` `SqliteConnection.ClearAllPools()` | confirmed |
| 5 | `Microsoft.VisualStudio.TestTools.UnitTesting` is a global using in the test projects | `obj/Release/net11.0/*.GlobalUsings.g.cs:2` | confirmed |
| 6 | Without the pending line MSTEST0001 falls to MSTest's default `suggestion` (the baseline does not set it) | `.editorconfig:569-571` ("Pending rules the baseline does not set"); `mstest.analyzers/4.5.3` `MSTestAnalysisMode` = `Default`, `mstest-default.globalconfig:7` | confirmed |
| 7 | The *Analyzer gate* never reports MSTEST0001 | `analyzer-check-dotnet.py:67-69` skips a result without location | confirmed |
| 8 | A file holding only an assembly attribute and a comment passes Reihitsu and Sonar | to verify: *Analyzer gate* on the new files, Tester then Code Officer, steps 6–7 | unverified |

## Acceptance criteria

- AC1: Each of the four test projects carries `[assembly: DoNotParallelize]`, and a full Release build of `Vandox.slnx` reports no MSTEST0001.
- AC2: `DOTNET_RULES_PENDING` holds no `MSTEST0001` entry, and `python3 .squad/tools/dotnet-rules.py --check` exits 0.
- AC3: No test changes behavior: the existing suites pass unchanged.

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | check: after the *Analyzer gate*'s build, no `MSTEST0001` result in any `**/obj/roslyn.sarif` (`grep -l MSTEST0001` lists only rule descriptors; a Python read of `runs[].results[].ruleId` prints nothing) — Tester, step 6; the gate itself cannot see it (fact 7) | — |
| AC2 | CI: `dotnet-rules.py --check` and `config-check.py` (orchestrator, step 7) | — |
| AC3 | CI: *Test* (all four projects) | — |

No new unit test: an assembly attribute is no executable line.

## Approach

Tester adds one file per test project, `tests/Vandox.<Project>.Tests/MSTestSettings.cs` (the name MSTest's project template uses), holding only a one-line comment naming why and the attribute, no `using` (fact 5), no namespace:

```
// <reason>
[assembly: DoNotParallelize]
```

Reasons, exact: Backend `Heap and allocation bounds read process-wide GC counters (record 0206)`; Core `Heap and allocation bounds read process-wide GC counters, and duration ratios the clock (record 0206)`; Storage `Timeout races and throughput read the clock, and TempDirectory clears every SQLite pool (record 0206)`; Import `TempDirectory clears every SQLite pool of the process (record 0206)`.

Orchestrator (as in #429) sets `DOTNET_RULES_PENDING = {}` in `.squad/tools/squad_settings.py` and runs `python3 .squad/tools/dotnet-rules.py`, which drops the pending block from `.editorconfig`. No Dev task.

## Options considered

Per project `Parallelize` or `DoNotParallelize`, `Parallelize` with class opt-outs, switching the rule off — weighed in record 0206; `Parallelize` for Import lost on an unmeasured process-wide pool clear and an unmeasured gain.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Vandox.Backend.Tests, Vandox.Core.Tests, Vandox.Import.Tests, Vandox.Storage.Tests | `MSTestSettings.cs` (new) | assembly attribute |
| — | `.squad/tools/squad_settings.py`, `.editorconfig` | pending entry removed, block regenerated |

## Signatures (for the Dev's skeleton)

None: no API member changes.

## Test files

The four `MSTestSettings.cs` above. Existing test call sites: none. Files no unit test can cover: none.

## Areas

None: no change in behavior.

## Documentation updates

- `docs/UNIT_TESTS.md`, *Where tests live*, .NET bullet (Tester): add that every test project has a `MSTestSettings.cs` with `[assembly: DoNotParallelize]`, since heap, allocation and timing tests read process-wide state (link record 0206), and a new test project adds the same file.
- Sweep of `docs/ARCHITECTURE.md`, `docs/areas/`, `.squad/stack.md` for parallel or sequential test runs: no hit. No changelog in the PR template.

## Architecture check

No guarantee of `.squad/project.md` is touched; production code is unchanged.

## Security considerations

- [x] No limits.
- [x] None.

## Decision records

- `docs/decisions/0206-test-assemblies-run-their-tests-one-at-a-time-stated-explicitly.md` (Proposed)

## Out of scope / follow-ups

- Squad lesson (step 12, `squad-upstream`): `analyzer-check-dotnet.py:68` drops location-less diagnostics, so a pending assembly-level rule (MSTEST0001) is never reported, against `.squad/stack.md` ("which the gate still reports in a changed file"); and the baseline does not set MSTEST0001, so after this change a new test project without the attribute is caught by nothing.

## Security notes

Security (plan review, APPROVED), non-blocking N1 (fix now, in step 6): AC1 can only be checked by reading the SARIF, since the Analyzer gate cannot see the rule. The Tester's report should name the exact command and quote its output, for example `python3 -I -c '...results...ruleId=="MSTEST0001"...' tests/*/obj/roslyn.sarif` printing nothing after the gate's Release build. Without that, the Reviewer has to count AC1 as not run. The gap the Lead raised (the gate skips location-less diagnostics, `.squad/tools/analyzer-check-dotnet.py:67-69`, and the baseline leaves MSTEST0001 at MSTest's default `suggestion`) is a squad lesson, not this change's to close: removing the pending entry weakens nothing, and a test project without the attribute still runs sequentially.
