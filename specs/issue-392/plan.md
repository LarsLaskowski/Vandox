# Plan: State the run cost in the memory per circuit of the logs and incidents views

Source: Issue #392
Tier: standard — a behavior change (the incidents view shows its external text in at most 16 runs) that creates, changes or removes no security control: which characters are escaped and how they are written stays the same.
Security areas touched: 12 (Logging and display of external data: the grouping into runs of the incidents view's external text)

## Problem / root cause

Every external text of both views is built with `LogText.Runs(text, maxChars)`, which takes `LogText.MaxRuns` (256)
(`src/Vandox.Backend/Logs/LogText.cs:17` and `:40-43`). A run is a `TextRun` object plus a string, so a text whose plain and escaped
characters alternate costs several times its characters. The memory figures of `docs/areas/web-ui-logs-view.md:113-116` and
`docs/areas/web-ui-incidents-view.md:116-121` count characters only. Record 0193 capped the report page at 16 runs and left these two
views to this issue. By the caps, the run cost brings a logs page from 5 MiB to about 11 MiB, and the incident page's timeline to
about 110 MiB.

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | The logs view states about 5 MiB a page from characters only | `docs/areas/web-ui-logs-view.md:113-116` | confirmed |
| 2 | The incidents view states up to 1,000 timeline entries from characters only | `docs/areas/web-ui-incidents-view.md:116-121` | confirmed |
| 3 | Every field of both views takes up to 256 runs | `LogText.cs:17,40-43`; `LogLineView.cs` (`From`: source, host, program, message), `TemplateRowView.cs` (`From`), `LogsPage.razor.cs:306-308`; `IncidentDetailView.cs:185,247,263-264,285-287,304-306,327-329`; `IncidentRowView.cs:86,99` | confirmed |
| 4 | A 1,024-byte alternating text costs about 22.9 KiB with 256 runs | record 0193 (measured in the plan review of #304, stored text included); reading agrees: 255 runs at roughly 72 bytes (`TextRun`, its string, the list slot) plus a rest of about 1,190 characters | confirmed |
| 5 | The report page caps at 16 runs | `Components/Reports/ReportContent.razor.cs:78`, `ReportsView.MaxTextRuns` | confirmed |
| 6 | The logs pages keep only the runs, not the stored records | `LogsPage.razor.cs:318,421-422` | confirmed |
| 7 | A source is ASCII of at most 128 bytes and never escaped (one run) | `DataRecord.cs:90` (`Check.Name`), `Check.cs:298` | confirmed |
| 8 | Host and program are any text of at most 1,024 bytes, a message at most 16,384 | `LogLine.cs:77,94` (`Check.Short`, `Check.Text`) | confirmed |
| 9 | A template text is at most 1,024 bytes, keeps format characters (escaped) | `docs/areas/log-templates.md:44,120` | confirmed |
| 10 | A timeline entry has at most 8 texts and numbers, each text cut to 1,024 bytes, shown at 512 characters; at most 1,000 entries within 4 MiB of JSON; the type is a catalog id | `SignatureCatalog.cs:23` (`MaxFields`), `SignatureDetector.cs:28,207`, `TimelineEntry.From`, `IncidentLimits.MaxTimelineBytes`, `IncidentsView.MaxTimelineTextChars`, `BackupSighting.ToEntry` | confirmed |
| 11 | Retained runs only: a 16,384-byte alternating message at 256 runs is about 48 KiB, a 1,024-byte one at 2,048 characters about 21 KiB; at 16 runs about 5.3 KiB, and at 512 characters about 2.3 KiB | to verify: the Tester's tests of AC2 and AC3 (step 5/6) report each measured value; now an estimate from reading (#4) | unverified |
| 12 | Record 0193 is released (append-only) | `git tag --contains` of its adding commit: `v0.2.0` | confirmed |
| 13 | No existing incidents-view test builds a text of more than 16 runs | `IncidentPageTests.cs:192-207`, `IncidentRowViewTests.cs:29-30`, `IncidentDetailViewTests.cs:25-` | confirmed |

All claims of the issue are confirmed. Related defect for the Dev's doc update: the logs view's figure counts 2,048 characters for
the source (at most 128 ASCII bytes) and "four capped short fields" for a template row, which has three.

## Acceptance criteria

- AC1: Every external text the incidents view builds — the list rows (victim, growth programs) and the incident page (kills, hypothesis
  programs and backup programs, consumers and connection commands, timeline type, host and texts, metric extremes, templates) — has at
  most `IncidentsView.MaxTextRuns` (16) runs, the last one holding the rest as `LogText.Runs(text, cap, 16)` builds it.
- AC2: A 1,024-character text whose characters alternate between `a` and U+0001 retains with its runs of the incidents view at most
  6,144 bytes at the 2,048-character cap and at most 3,072 bytes at the 512-character timeline cap (runs only); a plain text of the same
  length retains less.
- AC3: With the logs view's 256 runs, an alternating message of 16,384 characters retains at most 57,344 bytes with its runs at the
  16,384-character cap, and an alternating text of 1,024 characters at most 24,576 bytes at the 2,048-character cap (runs only); a plain
  text of the same length retains less.

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `IncidentDetailViewShowsEveryExternalTextInAtMostSixteenRuns`, `IncidentRowViewShowsVictimAndProgramsInAtMostSixteenRuns` | `tests/Vandox.Backend.Tests/IncidentDetailViewTests.cs`, `tests/Vandox.Backend.Tests/IncidentRowViewTests.cs` |
| AC2 | `LogTextRunsOfAlternatingIncidentFieldRetainBoundedHeap`, `LogTextRunsOfAlternatingTimelineTextRetainBoundedHeap` | `tests/Vandox.Backend.Tests/LogTextTests.cs` |
| AC3 | `LogTextRunsOfAlternatingLogMessageRetainBoundedHeap`, `LogTextRunsOfAlternatingLogFieldRetainBoundedHeap` | `tests/Vandox.Backend.Tests/LogTextTests.cs` |

## Approach

- `IncidentsView` gets the constant `MaxTextRuns` = 16 in its *Constants* region. All 15 `LogText.Runs` calls of
  `IncidentDetailView` and `IncidentRowView` (fact 3) pass it as the third argument; the caps and `LogText` are unchanged. The chart
  labels (`LogText.Escape`) build no runs and stay.
- The logs view is unchanged in code; only its figures are restated.
- AC1 tests: a stored incident (and a summary) whose every external field is `Alternating(1024, 'a', (char)1)`; each built field has
  at most 16 runs, at least one has exactly 16, and the concatenated runs of a field equal `LogText.Escape(text, cap)` (the shown
  characters are unchanged). The input asserts it still holds U+0001 (`.squad/team.md`, *Escapes in files*).
- AC2/AC3 tests measure the runs only, since the views keep no stored text: build the source text once, sample
  `HeapSampler.RetainedBytes()`, build its runs `count` times into a kept list, sample again, divide by `count` (2,560 texts; 256 for the
  16,384-character message, about 12 MB). The two existing pins of #356 stay as they are. The Tester reports every measured value
  (fact 11); a bound the measurement exceeds goes to the Lead (mode `decide`), not into a changed limit.
- No new user-visible output.

## Options considered

1. **Cap the incidents view at 16 runs, keep 256 in the logs view and restate both figures** (chosen).
2. **Restate both at 256 runs** (the issue's first option) — the incident page stays at about 110 MiB per circuit by its caps.
3. **Cap both at 16** (the issue's second option, applied to both) — a log line with many escapes (ANSI color sequences start with ESC)
   would show its rest after 15 changes in the escape style, in the view where raw lines are read, to save about 6 MiB per circuit.

Details in record 0207.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Vandox.Backend | `Incidents/IncidentsView.cs` | new constant `MaxTextRuns` |
| Vandox.Backend | `Incidents/IncidentDetailView.cs`, `Incidents/IncidentRowView.cs` | pass `IncidentsView.MaxTextRuns` to every `LogText.Runs` call |

## Signatures (for the Dev's skeleton)

- `public const int MaxTextRuns = 16;` in `public static class IncidentsView` (XML summary: the most runs of an external text of the
  incidents view; the last one holds the rest).

## Test files

`tests/Vandox.Backend.Tests/IncidentDetailViewTests.cs`, `tests/Vandox.Backend.Tests/IncidentRowViewTests.cs`,
`tests/Vandox.Backend.Tests/LogTextTests.cs`.

Existing test code that calls a changed signature: none.

Files of the change no unit test can cover: none.

## Areas

- Incidents view: external text in at most 16 runs; memory per circuit restated with the run cost.
- Logs view: memory per circuit restated with the run cost.
- Web UI: *External text* names the incidents view beside the report page.

## Documentation updates

All by the Dev, against the code and the Tester's measured bounds:

- `docs/areas/web-ui.md`, *External text* (line 94): "(the report page: 16)" becomes "(the report page and the incidents view: 16)".
- `docs/areas/web-ui-incidents-view.md`: *External text* says each text is shown in at most 16 runs, the rest as one run (record 0207);
  *Limits and errors* restates the memory per circuit from the AC2 bounds — a field up to 6 KiB, a timeline text up to 3 KiB with
  their runs (bounds pinned by tests), so a timeline of 1,000 entries, each a host and at most 8 texts (the type is a catalog id), takes
  up to about 30 MiB by the caps, and the 4 MiB budget of the stored timeline keeps it below that; "as with the 5 MiB of a logs page"
  takes the new logs figure; *Related decisions* adds 0207; *Implementation* names `IncidentsView.MaxTextRuns`.
- `docs/areas/web-ui-logs-view.md`, *Limits and errors*: the memory per circuit with the run cost from the AC3 bounds — a message up to
  56 KiB and a host or program up to 24 KiB with their 256 runs (bounds pinned by tests), the source at most 128 ASCII characters, about
  104 KiB a line and about **11 MiB** for 100 lines and 11 context lines; the template page 200 rows of a template text and a program of
  up to 24 KiB each plus the chart, about **9.5 MiB**; the related defect above corrected. *Related decisions* adds 0207.
- No `CHANGELOG.md` entry (the PR checklist asks for none; 0193's comparable change has none), no `docs/ARCHITECTURE.md` change.

## Architecture check

`docs/ARCHITECTURE.md` states nothing on runs or per-circuit memory. Guarantee 12 of `.squad/project.md` holds: every character that
was escaped is still escaped and written as text; only the styling of the rest after 15 runs is coarser, as on the report page.

## Security considerations

- [x] No limits on parsed input change; the run cap applies to the displayed runs after escaping.
- [x] Exceptions: none new (`IncidentsView.MaxTextRuns` is a valid argument of `LogText.Runs`).

## Decision records

- `docs/decisions/0207-incidents-view-shows-each-external-text-in-at-most-16-runs-logs-view-keeps-256.md` (Proposed; amends 0193)

## Out of scope / follow-ups

- A tighter incident figure from the 4 MiB timeline budget, the render tree's copy of the text, and the logs view's run cap.
