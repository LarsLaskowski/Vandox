# Plan: The decoder's time-ratio test holds under the full coverage run

Source: Issue #381
Tier: tests — only `tests/Vandox.Core.Tests` and `docs/UNIT_TESTS.md` change; no production code.
Security areas touched: none

## Problem / root cause

`BatchDecoderTests.BatchDecoderTimeIsInProportionToTheLine` (`tests/Vandox.Core.Tests/BatchDecoderTests.cs:1573`) divides the
decoder's time, which coverlet instruments (Vandox.Core), by `JsonDocument.Parse`'s, which it does not (System.Text.Json). The
bound `MaxTimeFactor = 50` (line 30) is below three times the ratio the correct code reaches under coverage, which
`docs/UNIT_TESTS.md` (lines 124–129) requires: the reference runs faster in the full run (warmed by earlier tests), the
decoder does not, so the ratio rises to 50–56. At the 4 MiB line (line 1576) no bound both meets
that rule and catches the quadratic check it guards against (fact 4).

## Verified facts

Scratch copy, `dotnet test tests/Vandox.Core.Tests`; ratio = decoder / parse.

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | Fails in the full coverage run, passes alone | full + coverage: 43.2, 50.1 (fails), 43.2; alone + coverage: 35.9, 36.7, 34.2 | confirmed |
| 2 | Caused by load from parallel tests | no `[assembly: Parallelize]` in `tests/` (MSTest runs them one by one); decoder 1042–1085 ms in both runs, parse 21–25 ms full vs 29–31 ms alone | refuted |
| 3 | Coverage inflates only the numerator | without coverage: decoder 255–280 ms, ratio 8.5–11.7 | confirmed |
| 4 | Quadratic variant (0090 option 12: `WireKeyCheck.Release` always `set.Clear()`) at 4 MiB | full + coverage 218, 228; alone + coverage 166, 179; alone plain 147 — against 3 × 55.7 = 167 | confirmed |
| 5 | `WireShapes.ShortKeys` yields at most 67 + 67² + 67³ = 305,319 keys; `ObjectFillingBytes` takes 300,000 (`WireShapes.cs:63`), about 2.4 MB | alphabet 0x20–0x7E without `"` `\` and upper case = 67 | confirmed |
| 6 | An 8 MiB line fits the limits | `MaxBatchBytes` default 16 MiB (`WireLimits.cs:18`) | confirmed |
| 7 | At 8 MiB the correct ratio stays as in 1/3, the quadratic one roughly doubles (its cost grows with the square of the line) | to verify: Tester, step 6, the runs of AC1 and AC2 | unverified |
| 8 | Record 0090 (released, in `v0.1.0`) names the 4 MiB line | `0090…md:120–123` | confirmed |

## Acceptance criteria

- AC1: `BatchDecoderTimeIsInProportionToTheLine` passes in the full `Vandox.Core.Tests` coverage run
  (`--settings coverlet.runsettings`), alone with coverage and alone without, three runs each; `MaxTimeFactor` is at least
  three times the highest correct ratio measured there and in the issue (55.7), so at least 170.
- AC2: with the quadratic variant of fact 4 in a scratch copy, the same test fails in each of those three configurations
  (one run each), the ratio above `MaxTimeFactor`.

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `BatchDecoderTimeIsInProportionToTheLine` (adapted, `[TestCategory("AC1")]`) | `tests/Vandox.Core.Tests/BatchDecoderTests.cs` |
| AC2 | manual: Tester, step 6, scratch copy of the branch head; every ratio of AC1 and AC2 goes into the hand-back for `log.md` | — |

If no bound meets both AC1 and AC2, the Tester hands back (Lead, `decide`).

## Approach

- `WireShapes`: `Enumerate` yields quadruples after the triples; `ObjectFillingBytes` takes as many keys as its size needs
  instead of a fixed 300,000. Keys keep their order, so every existing shape stays byte-identical.
- The test: line `8 << 20`, `MaxLineBytes = 16 << 20`, `MaxTimeFactor` the value AC1 gives (170 unless a measurement
  raises it, rounded up to a multiple of 10). Summaries: 8 MiB because the quadratic cost grows with the square of the line,
  instrumentation only linearly; `MaxTimeFactor` names the three-times rule.

## Options considered

1. Raise the bound at 4 MiB — loses: fact 4, no bound meets both.
2. Decoder on a benign line of the same size as reference (cancels instrumentation) — loses: at 4 MiB the quadratic variant is
   about 5.4 times it under coverage against a bound of about 3.6.
3. Decoder on twice the input — loses: linear about 2, bound ≥ 6, quadratic about 3.4.
4. `[DoNotParallelize]`, more warm-ups — loses: nothing runs in parallel (fact 2); the warm reference is the true one.
5. Skip the test under coverage — loses: changes a pipeline command.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Vandox.Core.Tests | `BatchDecoderTests.cs`, `WireShapes.cs` | see *Approach* |

## Signatures (for the Dev's skeleton)

none

## Test files

`tests/Vandox.Core.Tests/BatchDecoderTests.cs`, `tests/Vandox.Core.Tests/WireShapes.cs`. Other callers of `ObjectFillingBytes`
(line 1486, shapes 2 and 5) keep identical output.
Code no unit test can cover: none.

## Areas

none (no change in behavior).

## Documentation updates

- `docs/UNIT_TESTS.md` (Tester), lines 124–129: the highest ratio is measured in the project's full coverage run as well
  as alone (a warmed reference runs faster), and the input is large enough that the faulty variant exceeds the bound.

Sweep: nothing in `docs/areas/` or `docs/ARCHITECTURE.md` names this test; no changelog (not user-visible).

## Architecture check

None touched.

## Security considerations

- [x] No limits on parsed input change (a test's own `MaxLineBytes`).
- [x] None.

## Decision records

none: 0090's decision stands; its 4 MiB sentence is released history; the why goes into the test's summary.

## Out of scope / follow-ups

none
