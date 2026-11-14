# 0214: Time-series queries share one resolution and one time axis, raw samples bridged between other lines' times

- **Status:** Proposed
- **Date:** 2026-11-14
- **Area:** Time-series queries
- **Source:** Issue #47
- **Supersedes:** —

## Context

The v0.3.0 views (#48 to #52) chart metric series over ranges from minutes to years. Three sources hold the values: raw values (30 days
by default), 5-minute rollups (one year) and hourly rollups (three years) ([Retention and rollups](../areas/retention.md)). The chart
takes one ascending list of times and one value per time per series, and a missing value breaks the line
([Web UI](../areas/web-ui.md), *Charts*); a missing value must never read as normal (record 0028). Collectors stamp their own run
start, so `system` (30 s), `filesystem` and `clock` (60 s) and `self` (60 s) never share raw capture times. The performance criterion is
a one-year query under one second on the reference host ([`BENCHMARKS.md`](../BENCHMARKS.md)).

## Options considered

1. **A resolution per line** — each line at its finest source; an overlay of lines with different steps or times cannot share the
   chart's axis.
2. **One resolution for all lines, steps only (no raw)** — every line on a common step grid; a step finer than the sampling interval
   is empty between samples and would read as a gap, so short ranges could not show single samples.
3. **One resolution for all lines; raw on the union of the lines' times, a time without the line's own sample bridged** (chosen) —
   raw shows every sample and still breaks a line only where data is missing; the chart needs a third point state.
4. **Raw by expected count from the collector interval** — needs the interval of every series, which the store does not hold
   (imported data has none).

## Decision

A query returns all lines at one resolution: raw when the range starts within the raw period and the lines' values fall on at most
the target number of distinct times (counted by reading at most one time more), otherwise the smallest step of a fixed ladder
(5 minutes to 1 day, then whole days) for which at most the target number of UTC-aligned steps overlap the range, merged from the
5-minute or the hourly rollups. At raw resolution the axis is the union of the lines' capture times, and a line's point at a time
without its own sample is *between samples*, drawn through, unlike a gap. The rules are in
[Time-series queries](../areas/time-series-query.md).

## Consequences

- The chart gains the *between samples* state, passed as a list of bridged indexes per series; the vendored uPlot draws through an
  `undefined` value and breaks only at `null` (read in `uPlot.esm.js`, `findGaps` and the linear path builder).
- A step reads every rollup bucket of its lines in the range, so the cost grows with the range (8,760 hourly buckets per line and
  year) and not with the raw values; a range of many years is slower but bounded by the hourly period.
- Raw is never chosen for a range that starts before the raw period, even where imported values or the windows of OOM kills keep raw
  values; and a range before the hourly period has no buckets and shows empty lines (an imported history older than
  `retention.rollups_1h` is not charted).
- Revisit if a view needs per-line axes (a scatter of unrelated samples) or a daily rollup tier for multi-year ranges.
