# Plan: Bound the report page's memory for the commands of the new listeners

Source: Issue #356
Tier: security — besides the run cap that bounds memory, the change corrects how the shared external-text display marks the rest run as escaped (output escaping, a guard: `.squad/routing.md`, *Tiers*); raised from `standard` in the revision for Security's finding 1.
Security areas touched: 12. Logging and display of external data (the shared external-text display `LogText`, used by the report page, the logs view and the incidents view)

## Problem / root cause

`ReportContent.Text` (`src/Vandox.Backend/Components/Reports/ReportContent.razor.cs:78`) builds the runs of every external piece of the
report with `LogText.Runs(piece.Text, LogText.MaxShortChars)`, which allows up to `LogText.MaxRuns` = 256 runs
(`src/Vandox.Backend/Logs/LogText.cs:17`, `:40-43`). Each run is a string plus a `TextRun` object plus a list slot (`Build`,
`LogText.cs:61-112`), so a text whose plain and escaped characters alternate retains about 22.9 KiB for 1,024 bytes, while its
characters are about 6 KiB. `ExternalText` keeps the runs as its parameter for the life of the circuit. With 2,560 commands in the
*New listeners* section that is about 60 MB per circuit; the outage texts take the same path.

Related defect (Security, plan review): `Build` marks the rest run escaped by length only, `rest.Length != text.Length - index`
(`LogText.cs:92`), so a rest whose escapes (longer) and cut (shorter) cancel out in length is shown plain, and a real control character
in it looks like a literal `\u0001` typed into the text. A lower run cap makes the rest path common, so the fix is part of this change.

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | The report page builds runs of every external piece with the shared cap of 256 runs and 2,048 characters. | `ReportContent.razor.cs:78`; `LogText.cs:17`, `:27`, `:40-43` | confirmed |
| 2 | A command of 1,024 bytes with alternating plain and escaped characters takes about 22.9 KiB with its runs. | Issue; `docs/areas/web-ui-report-page.md:100` (measured in #304's plan review); recomputed by reading `Build`: 128 "a" strings of 24 B, 127 escape strings of 40 B, 256 `TextRun` of 32 B, a 256-slot array of about 2 KiB, the source of about 2 KiB and a rest of about 1,180 characters, about 22.4 KiB | confirmed |
| 3 | 40 findings of 64 listeners hold about 60 MB per circuit, not the 16 MB `web-ui.md` stated before #304. | The figure 60 MB and the old 16 MB stand in `web-ui-report-page.md:101-105` today (the 16 MB moved there from `web-ui.md`); the sum is 2,560 commands (20 + 20 findings of 64 listeners; the change-only kind has no commands, record 0149) times 22.9 KiB | confirmed |
| 4 | The number of circuits is not bounded and the container has 512 MB. | `docs/areas/web-ui.md:168-170` | confirmed |
| 5 | With a cap of 16 runs a 1,024-byte alternating command retains about 7.3 KiB: source 2,072 B, 8 "a" and 7 escape strings (472 B), 16 `TextRun` (512 B), list and array (176 B), and the rest of about 2,000 shown characters with the cut marker (about 4.1 KiB). | to verify: the Tester's heap test of AC2 measures it (bound 8,192 B), steps 5–6; computed from `Build` (`LogText.cs:61-112`): after 15 runs the rest is `Escape(text[index..], maxChars - used)`, capped at 2,048 characters in total | unverified |
| 6 | A 32-byte alternating user name retains about 1.4 KiB with 16 runs (about 2.4 KiB with 256 runs, under the 3 KiB bound of today's test). | to verify: the Tester's adapted heap test of AC3 measures it (bound 2,048 B), steps 5–6; computed as row 5 | unverified |
| 7 | The rest run is shown with the same characters: `Build` writes `Escape(text[index..], maxChars - used)`, which is the concatenation of the same pieces and marker. | `LogText.cs:84-91`, `:53-56` | confirmed |
| 8 | The page's pieces are compared against the Markdown by text, not by run boundaries, so a coarser run split keeps that test green. | `ReportContentTests.ReportContentShowsTheSameContentAsTheMarkdown`; `ReportPieces.FromHtml` | confirmed |
| 9 | Fewer runs also mean fewer render frames per text: `ExternalText.razor` writes one text frame per plain run and an element, attribute and text per escaped run. | `src/Vandox.Backend/Components/Logs/ExternalText.razor` (the size of those frames is not measured and stays under "plus the markup and the render tree") | confirmed |
| 10 | The logs and incidents views use the same 256 runs and state memory figures that count characters only. | `LogLineView.cs:74-77`, `IncidentDetailView.cs:247-329`; `web-ui-logs-view.md:113-116`, `web-ui-incidents-view.md:116-121` (related defect, follow-up below) | confirmed |
| 11 | The rest run can be marked plain although it holds escapes: 7 pairs of `'a'` and `(char)1`, then `'a'`, `(char)1` and 2,022 `'a'` (2,038 characters) with 16 runs and a cap of 2,048 give a rest of an escape, 1,992 `'a'` and `[cut: 30 more characters]`, 2,023 characters like the source rest, so `Escaped` is false. | `LogText.cs:84-92` read: after 15 runs `used` is 50, the rest cap 1,998; Security reproduced it in its plan review (`last.Escaped=False`) | confirmed |
| 12 | On the report page the defect of row 11 cannot be reached through a stored text: commands and outage texts are at most 1,024 bytes, so a cut rest is longer than the source rest; the logs view's messages (cap 16,384) and the 256-run path of every view can reach it. | `docs/areas/web-ui-report-page.md:91-101`; `LogLineView.cs:77`; computed from `Build` | confirmed |

## Acceptance criteria

- AC1: On the report page every external text is shown with at most `ReportsView.MaxTextRuns` (16) runs; its shown characters are
  exactly those of `LogText.Escape(text, LogText.MaxShortChars)` (cut marker included), and the text after the 15th run forms one
  run, shown in the escape style whenever it differs from the source text it stands for.
- AC2: A command of 1,024 one-byte characters whose plain and escaped characters alternate retains at most 8,192 bytes of heap
  together with its report runs (`LogText.Runs(text, LogText.MaxShortChars, ReportsView.MaxTextRuns)`); a plain command of the same
  length retains less.
- AC3: A user name of 32 one-byte characters whose plain and escaped characters alternate retains at most 2,048 bytes of heap together
  with its report runs; a plain name of the same length retains less.
- AC4: `LogText.Runs(text, maxChars, maxRuns)` returns at most `maxRuns` runs, the last holding the rest as `LogText.Escape` writes it,
  and throws `ArgumentOutOfRangeException` for `maxRuns` below 1; `LogText.Runs(text, maxChars)` keeps 256 runs.
- AC5: The rest run (the run written after `maxRuns - 1` runs) is escaped exactly when its text differs ordinally from the source text
  it stands for, also when escapes and the cut leave it as long as that text (fact 11); a rest equal to its source text is plain.

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `ReportContentCapsTheRunsOfExternalText` | `tests/Vandox.Backend.Tests/ReportContentTests.cs` |
| AC2 | `LogTextRunsOfAlternatingCommandRetainBoundedHeap` | `tests/Vandox.Backend.Tests/LogTextTests.cs` |
| AC3 | `LogTextRunsOfAlternatingShortNameRetainBoundedHeap` (adapted) | `tests/Vandox.Backend.Tests/LogTextTests.cs` |
| AC4 | `RunsAreAtMostTheGivenNumber`, `RunsRefuseFewerThanOneRun`; existing `RunsAreAtMostTwoHundredFiftySix` kept | `tests/Vandox.Backend.Tests/LogTextTests.cs` |
| AC5 | `RunsMarkARestOfEqualLengthAsEscaped`, `RunsKeepAPlainRestPlain`; AC1's second case | `tests/Vandox.Backend.Tests/LogTextTests.cs`, `tests/Vandox.Backend.Tests/ReportContentTests.cs` |

Cases:

- AC1: a listener section (`ReportSamples` builders, as `ReportContentShowsListenerCommandAsText`) whose command is 1,024 characters
  alternating `'a'` and `(char)1`, built in code (no escape literal); assert the input holds `(char)1`, that the command's
  `<span class="external">` holds exactly 8 `class="escape"` spans (16 runs: 8 plain, 7 escaped, one escaped rest), that its decoded
  text equals `LogText.Escape(command, LogText.MaxShortChars)` and ends with `[cut: 439 more characters]` (15 runs use 50 shown
  characters, the rest shows 285 pairs of the remaining 1,009 characters), and that a short command with one escape still renders as
  before (`a`, `\u0001` span, `a`). Second case (AC5 on the page): a document built with the test's `Item` helper whose external piece is
  the input of fact 11, built in code from `(char)1`; assert the input holds `(char)1` and that the last `class="escape"` span of its
  code element holds exactly `LogText.Escape(text[15..], 1998)` and ends with `[cut: 30 more characters]`.
- AC2/AC3: the existing `RetainedPerText` helper gains the run cap as a parameter and calls the three-argument `Runs`; the warm-up call,
  2,560 texts and the plain comparison stay as in today's test. AC3's doc comment and bound change from 3,072 to 2,048 bytes.
- AC4: a text of 40 alternating characters with `maxRuns` 4 gives 4 runs, the last equal to `LogText.Escape(text[3..], ...)`; `maxRuns`
  0 and -1 throw.
- AC5: the input of fact 11 with `maxRuns` 16 and cap 2,048: 16 runs, the last escaped and equal to `LogText.Escape(text[15..], 1998)`
  (the input asserted to hold `(char)1`; the guard that fails without the fix is the escaped flag of the rest). A plain rest: 15 runs of
  alternating `'a'` and `(char)1`, then 100 `'a'`, with `maxRuns` 16 and cap 2,048: the last run is the 100 `'a'` and not escaped.

## Approach

- `LogText` gets the overload `Runs(text, maxChars, maxRuns)`, which checks `maxRuns >= 1`
  (`ArgumentOutOfRangeException.ThrowIfLessThan`) and calls `Build(text, maxChars, maxRuns)`; the two-argument `Runs` calls it with
  `MaxRuns`. `Build` keeps writing the rest as one run after `maxRuns - 1` runs, but marks it escaped by content instead of length:
  it takes the source rest once (`text[index..]`), escapes it, and passes `string.CompareOrdinal(rest, remaining) != 0` as the flag (a
  positive condition, as the analyzers require). This fixes the flag for every caller, the 256-run views included. The other runs are
  already marked by what they hold: a plain run holds only unescaped pieces, an escaped run only escapes, the cut marker is escaped.
- `ReportsView` gets the constant `MaxTextRuns` = 16, next to `PageOutages`.
- `ReportContent.Text` calls `LogText.Runs(piece.Text, LogText.MaxShortChars, ReportsView.MaxTextRuns)`. No other caller changes: the
  logs and incidents views keep 256 runs.
- No new user-visible text; the page looks the same except that a text past 15 run changes shows its rest as one run, in the escape
  style whenever it differs from the source text; in every view a rest of equal length that holds escapes is now escape style.

## Options considered

1. **Cap the runs of the report page's external text at 16** — chosen: the run count is the cost (fact 2 vs 5), the shown text stays
   the same, it bounds every external text of the page (outage texts included) and the render frames with it.
2. **A shorter shown length of the commands on the page** (the issue's second example) — lost: an alternating command cut to 1,024
   shown characters still fills about 146 runs, so most of the cost stays, and the page would cut where the download does not.
3. **Lower page caps of findings** (the issue's third example) — lost: rejected in record 0149 (option 2) for the forensic window, and
   it leaves the outage texts unbounded by it.
4. **Cut the command at composition, like the user name** — lost: changes the Markdown report and download and drops forensic content.
5. **Cap the runs for every view in `LogText.MaxRuns`** — lost: the logs and incidents views state their own memory and are outside
   this issue; they get the follow-up below.

Record: [0193](../../docs/decisions/0193-report-page-shows-each-external-text-in-at-most-16-runs.md).

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Vandox.Backend | `src/Vandox.Backend/Logs/LogText.cs` | new overload of `Runs` with a run cap; the rest run's escaped flag by content (`Build`) |
| Vandox.Backend | `src/Vandox.Backend/Reports/ReportsView.cs` | new constant `MaxTextRuns` |
| Vandox.Backend | `src/Vandox.Backend/Components/Reports/ReportContent.razor.cs` | `Text` uses the report's run cap |
| Vandox.Backend.Tests | `tests/Vandox.Backend.Tests/LogTextTests.cs`, `ReportContentTests.cs` | tests of AC1–AC5 |

## Signatures (for the Dev's skeleton)

- `LogText`: `public static IReadOnlyList<TextRun> Runs(string text, int maxChars, int maxRuns)`
- `ReportsView`: `public const int MaxTextRuns = 16;`

The skeleton's overload throws `NotImplementedException`; `LogText.cs` and `ReportsView.cs` are the only files it rewrites.

## Test files

- `tests/Vandox.Backend.Tests/LogTextTests.cs`
- `tests/Vandox.Backend.Tests/ReportContentTests.cs`

Existing test code that calls a changed signature: none (`Runs(string, int)` keeps its signature). The test kept unchanged and adapted:
`LogTextRunsOfAlternatingShortNameRetainBoundedHeap` and its helper `RetainedPerText` are adapted by the Tester (AC3); every other
`ReportContentTests` case has texts of fewer than 16 runs and stays unchanged.

Files no unit test can cover: none.

## Areas

- [Report page](../../docs/areas/web-ui-report-page.md), *Limits and errors*: the run cap of 16 for the page's external text and the
  restated memory per circuit.
- [Web UI](../../docs/areas/web-ui.md), *External text*: the run cap per view and when the rest run is in the escape style.
- [Listener changes](../../docs/areas/listener-changes.md), *Limits and errors*: the page figure "up to about 82 MB per circuit".

## Documentation updates

All by the Dev, once the code is green, with the figures from the bounds the tests pin (not from the computed values above):

- `docs/areas/web-ui-report-page.md`, *Limits and errors*, *Memory per circuit*: state that the page shows each external text in at
  most 16 runs (the rest in one run, see [Web UI](web-ui.md), *External text*) and restate the outages bullet (the runs' worst case per text is now the AC2 bound for a
  stored text of 1,024 bytes, so 1,100 texts of 50 outages, about 9 MB) and the new-listeners bullet (2,560 commands at the AC2
  bound, about 21 MB; 2,560 user names at the AC3 bound, about 5.2 MB; the changed lines restated with the AC3 user bound; the
  section's new total); remove the two #356 follow-up sentences and the "earlier figure of about 16 MB" sentence; link record 0193
  under *Related decisions*.
- `docs/areas/web-ui.md`, *External text*: "A field holds at most 256 runs, unless a view states fewer; the rest of the field is
  written as one text in the last run, in the escape style whenever it differs from the field's own text."
- `docs/areas/listener-changes.md`, *Limits and errors*, *Memory*: replace "up to about 82 MB per circuit" by the new section total.
- No `CHANGELOG.md` entry (written per release, `docs/CONTRIBUTING.md`, *Cutting a release*); `README.md` and `docs/ARCHITECTURE.md`
  state no run cap or page memory (searched for "runs", "circuit", "82 MB", "60 MB", "22.9": hits only in the three area documents).

## Architecture check

No guarantee of `.squad/project.md` is touched; `docs/ARCHITECTURE.md` states no page memory. The display rule that external text is
shown as text with visible escapes stays: the rest run is produced by the same `Escape`, and its escape style now follows its content
(fact 11), so the display rule of `docs/areas/web-ui.md`, *External text*, holds again for the rest run.

## Security considerations

- [ ] Every byte or length limit on parsed input says whether it applies before or after decoding — the cap counts runs of the
  displayed (escaped) text; the 2,048-character cap is on the displayed text, as today; no limit on parsed input changes.
- [ ] Every exception that can reach a user-visible failure reason or message — none: the new `ArgumentOutOfRangeException` is
  reachable only through a constant of 16.
- [ ] Inside the rest run a literal backslash sequence cannot be told apart from a real escape by its style; the characters are still
  escaped and written as text (record 0193, *Consequences*).

## Decision records

- `docs/decisions/0193-report-page-shows-each-external-text-in-at-most-16-runs.md` (Proposed), indexed; record 0149's last
  consequence (unreleased, `git ls-remote --tags origin` shows only `v0.1.0`, which does not contain it) now links it.

## Challenge

- Minor 1 (AC1 too broad, the rest is not always escaped): accepted — AC1 now says the rest is escape style whenever it differs from its
  source text, and the escaped flag itself is fixed to follow that rule (AC5, Security finding 1); record 0193 and the `web-ui.md`
  sentence say the same.
- Minor 2 (`[cut: ` is the start of the marker): accepted — AC1's case asserts the whole marker `[cut: 439 more characters]`.

## Security review

- Finding 1 (blocking, the rest marked plain when escapes and cut offset in length): accepted — `Build` marks the rest by an ordinal
  comparison with the source rest (*Approach*), AC5 with its two `LogTextTests` cases and the second AC1 case pin it, *Approach* no
  longer says `Build` is unchanged, and the tier is raised to `security`. Accepted forms re-checked: a plain run (only unescaped pieces),
  an escaped run (only escapes), the cut marker (always escaped), the rest equal to its source (plain, correct), the rest that differs
  (escapes, a cut or both: escaped); no form is left whose flag rests on length.
- Finding 2 (non-blocking, the residual of the coarser rest): accepted — record 0193's first consequence carries the sentence verbatim,
  and *Security considerations* points to it.

## Out of scope / follow-ups

- Proposed issue, title "[Backend] State the run cost in the memory per circuit of the logs and incidents views", body: "The logs view
  (`docs/areas/web-ui-logs-view.md`, about 5 MiB a page) and the incidents view (`web-ui-incidents-view.md`, up to 1,000 timeline
  entries) state memory per circuit from characters only, but every field is kept as up to 256 runs, which costs about 22.9 KiB for a
  1,024-byte text whose plain and escaped characters alternate (#356). Restate their figures with the run cost, or cap their runs as
  record 0193 does for the report page, and pin the per-text cost with a test."

## Challenge notes

Minor objections of the Devil's Advocate, verbatim:

1. minor — AC1 is worded too broadly ("the runs after the 15th form one escaped run"). `Build` flags the rest as escaped only when its output differs in length from the remaining text (LogText.cs:92). Tester and Dev: read AC1 as "the runs after the 15th form one run, escaped unless the rest is plain text shown whole". Docs: the record 0193 consequence and the `web-ui.md` sentence must not claim the rest is always in the escape style.
2. minor — The AC1 case says the decoded command "ends with the marker `[cut: `". That text sits at the start of the marker, not the end. Tester: assert the decoded text ends with `[cut: 439 more characters]`, or that it contains `[cut: ` and ends with ` more characters]`.
