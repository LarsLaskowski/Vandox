# 0221: The history view draws every metric on its own scale with two drawn axes and the step's extremes in the legend

- **Status:** Proposed
- **Date:** 2026-11-16
- **Area:** Web UI
- **Source:** Issue #49
- **Supersedes:** —

## Context

The history view overlays up to 8 chosen metrics of very different magnitudes (bytes, percent, counts) in one chart, as
issue #49 asks ("overlay of arbitrary metrics with independent axes"). The chart takes its width from its container and must work
down to 360 CSS pixels ([Web UI](../areas/web-ui.md), *Layout and navigation*); a drawn y axis takes about 50 pixels. Above raw
resolution the time-series query returns per point the minimum, average and maximum of a step
([Time-series queries](../areas/time-series-query.md)), and a line of averages hides a spike shorter than a step, which is what an
outage reconstruction looks for. The chart reports drags and clicks back to the server, where the page decides what they mean.

## Options considered

1. **One scale per metric, the first two axes drawn, the others read from the legend** — every metric readable at its own
   magnitude; two axes fit 360 pixels (one below 480); a metric without an axis is read from the legend under the pointer.
2. **One axis per drawn axis for all 8 metrics** — about 400 pixels of axes, more than a phone's width.
3. **One axis per unit** — the query returns no unit, and two byte metrics of very different size (the agent's RSS and the
   total memory) would share one scale, flattening the smaller one.
4. **Small multiples (one chart per metric)** — readable at any width, but no overlay, which the issue asks for.
5. **A shaded minimum-to-maximum band per line** — shows spikes directly, but up to 64 overlaid bands hide the lines.
6. **Average only** — hides spikes without saying so.

## Decision

Option 1: each metric has its own scale, the axes of the first two metrics are drawn (left and right, the right one only from 480
pixels of chart width), and above raw resolution the legend shows the step's minimum, average and maximum under the pointer with
a note on the chart that each point is an average. The browser reports a drag of at least 5 pixels as a range and a shorter press as
a click with the time span of 8 pixels; the server validates both values and snaps a click to a marker. The rules are in
[History view](../areas/web-ui-history-view.md).

## Consequences

- The chart sends the step extremes along with the averages, three values per point and line (at most 64 lines of 1,000 points at
  a step), so a render is larger than the averages alone.
- Metrics after the second have no drawn axis; their values are read from the legend.
- Drag and click are browser code without unit tests; the server side (validation, snapping, the address) is tested, the browser
  part is read by the Reviewer and tried in a browser by the Product Manager.
- Revisit when a view needs more than 8 metrics or a spike view of its own (a band for one chosen line).
