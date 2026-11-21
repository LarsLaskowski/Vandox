# 0224: The processes view stacks program rollups on the history chart and compares programs of one source

- **Status:** Proposed
- **Date:** 2026-11-21
- **Area:** Processes view
- **Source:** Issue #50
- **Supersedes:** —

## Context

Issue #50 asks which programs use the memory and the CPU over any period and what changed between two moments. The Product Manager
chose to build on the per-program rollups of #458 ([0223](0223-process-snapshots-are-folded-into-per-program-buckets-beside-a-source-total.md)),
so the charts outlive `retention.snapshots`. The rollup read returns gaps as `null` in every line at once and the step's minimum and
maximum per program. The web UI's stacked chart (`createStacked`, `ChartData`) refuses a gap and reports no drag or click, and the
incidents chart relies on that refusal; the history chart (`createHistory`) has the drag, the click, the moment mark and the
minimum-to-maximum legend, but no stacking. The moment read of the history view ([0222](0222-the-history-view-reads-the-moments-snapshots-and-the-metric-list-through-its-own-bounded-reads.md))
takes the closest snapshot of any source, while the processes view shows one source at a time.

## Options considered

1. **A stacked mode of the history chart** (`HistoryChartView.Stacked`, one scale from 0, filled bands, a gap step breaking every
   band) **and an optional source on the moment's stamp read** — reuses drag, click, mark, legend and the bounded stamp reads; the
   incidents chart and its gap refusal stay untouched.
2. **Gaps, events and the legend added to `createStacked` and `ChartData`** — changes the contract the incidents chart relies on
   and copies the history chart's event code.
3. **A third chart module and component for the processes view** — duplicates the history chart's interop, events and disposal.
4. **The charts read from the process snapshots** while they are kept, the rollups only beyond — two sources of truth for one line,
   a jump at `retention.snapshots`, and a decode of every snapshot of the range.
5. **The moment panel of the history view as it is** (closest snapshot of any source) — would show another source's programs beside
   a chart of one source, and the comparison could mix two sources.
6. **Comparison of processes** instead of programs — a snapshot lists only the top 25 processes, so a missing process is not known
   to be gone.
7. **A click that writes the shown source into the address** — the panel's source would be explicit, but an address with `at` and
   no `source` (a bookmark, a typed address) and a failed chart read would still need a rule, and the first click would pin the source
   so the default stops following the source with the latest bucket.

## Decision

The processes view draws both charts with the history chart in a stacked mode from `IProgramRollupReader`, never from snapshots, and
reads the moment's and the comparison's snapshots through the history view's stamp read narrowed to the source the charts show,
read again when that source changes. The comparison
is by program, and it marks no program `new` or `gone` when either snapshot does not list every program by #458's definition, which
moves from the fold into `ProcessSnapshot.ListsEveryProgram` so both use one rule. The rules are in the
[Processes view](../areas/web-ui-processes-view.md).

## Consequences

- The history chart's script and interop carry a second mode; the incidents chart keeps its gap refusal.
- A program cut or capped by the fold reads lower without a flag, and the finest step is 5 minutes: the view shows what #458 keeps.
- The snapshot panels reach only as far back as snapshots are kept; the charts as far as `retention.rollups_1h`.
- The history view's moment panel stays "any source"; *History of this moment* can show another source's snapshot.
- Revisit when per-process history or connections per program (#51) are wanted.
