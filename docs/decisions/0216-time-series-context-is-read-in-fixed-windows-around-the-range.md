# 0216: The time-series query reads gap records and backup runs in fixed windows around the range

- **Status:** Proposed
- **Date:** 2026-11-14
- **Area:** Time-series queries
- **Source:** Issue #47
- **Supersedes:** —

## Context

A query lists the gaps and backup runs that overlap its range, including ones that began before it (spec of #47, E12). The backend's
sequence and time gaps are rows with their span (`gaps`, indexed by `to_at`). The agent's own `gap` records keep their span only in the
JSON payload, and a span is not bound to the capture time ([Wire format records](../areas/wire-format-records.md), *`gap`*): a collector
gap ends at most one interval after its capture, but a span of log lines or of refused spool writes can begin well before it. A
`backup_run` start and end are two records, the end captured at the run's last sighting. The query must answer in under one second for
a year and stay bounded by its caps.

## Options considered

1. **Scan forward from the range start without bound** — finds every late-reported span, but a short range years back reads every
   later record of the kind.
2. **A new table of gap record spans, written by the write path and removed by retention** — exact and indexed, but adds work and a
   migration to the ingest write path and the retention removal (security areas 1 and 9) for a rare case.
3. **Fixed windows by capture time** (chosen) — gap records captured from one hour before the range start to seven days after its
   end, backup records from one day before to one day after; each kind read is one page of at most 10,000 records.

## Decision

Option 3. Seven days covers a span the agent records up to a week after it began (a disk fault that lasted days); one day covers a
nightly backup. A full page bounds the list as complete only up to its last capture time less the window. The rules are in
[Time-series queries](../areas/time-series-query.md).

## Consequences

- A gap record whose span begins more than seven days before its capture is listed only for ranges within seven days before that
  capture. A record that names sequence numbers is listed anyway through the final sequence gap it made (the agent's `spool_dropped`
  and give-up gaps all name numbers).
- A backup run longer than a day that spans the whole range, or one whose start lies more than a day before the range with no end
  stored, is not shown for that range.
- Revisit with option 2 if a view needs every late-reported span, for example log-shipping gaps on a log view.
