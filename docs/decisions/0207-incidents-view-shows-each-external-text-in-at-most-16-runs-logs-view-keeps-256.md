# 0207: The incidents view shows each external text in at most 16 runs, the logs view keeps 256

- **Status:** Accepted
- **Date:** 2026-11-13
- **Area:** Incidents view
- **Source:** Issue #392
- **Supersedes:** 0193 (amends its consequence that the logs and incidents views keep 256 runs a field: the incidents view now takes
  16 as well; 0193 stays unedited)

## Context

Every external text of the web UI is kept per circuit as the runs of the shared external-text display
([Web UI](../areas/web-ui.md), *External text*), up to 256 runs a field. Each run is a string and an object of its own, so a text whose
plain and escaped characters alternate costs far more than its characters: about 22.9 KiB for a text of 1,024 bytes
([0193](0193-report-page-shows-each-external-text-in-at-most-16-runs.md)). The memory figures of the logs view and the incidents view
counted characters only. With the run cost, the incident page's timeline (up to 1,000 entries, each with a host and up to 8 texts) comes
to about 110 MiB by its caps, while a logs page comes to about 11 MiB instead of 5 MiB. The number of circuits is not bounded and the
container has 512 MB.

## Options considered

1. **A cap of 16 runs for the incidents view, 256 kept for the logs view with restated figures** (chosen) — the incidents view shows the
   same stored incident texts that the report page already shows in 16 runs, and its timeline is where the run cost multiplies; the logs
   view is where raw lines are read, and its restated cost stays below the report page's accepted figure.
2. **Restate both views at 256 runs** — leaves the incident page at about 110 MiB per circuit by its caps, a fifth of the container for
   one open page.
3. **A cap of 16 runs for both views** — brings a logs page back to about 5 MiB, but a log line with many escapes (ANSI color sequences
   start with the control character ESC) would show everything after its 15th change between plain and escaped text in the escape
   style; the logs view is the one place the user reads such lines as they were logged.

## Decision

The incidents view (list and incident page) builds the runs of every external text with `IncidentsView.MaxTextRuns` (16), as the report
page does; the logs view keeps `LogText.MaxRuns` (256). Both views state their memory per circuit with the run cost, from per-text
bounds pinned by tests; the figures are in [Incidents view](../areas/web-ui-incidents-view.md) and
[Logs view](../areas/web-ui-logs-view.md), *Limits and errors*.

## Consequences

- An external text of the incidents view with more than 15 changes between plain and escaped characters shows its rest as one run, in
  the escape style whenever it differs from the text it stands for, with the same characters as before and the limits 0193 names for the
  report page (a literal backslash sequence inside that rest is not told apart from a real escape by its style).
- The incident page's cost is set mostly by its characters; the stored timeline's 4 MiB budget keeps the real amount below the bound
  computed from the caps.
- The logs view keeps its full styling; its pages cost about twice what the characters alone suggested. Lowering it later is a cap of
  its own, as here.
