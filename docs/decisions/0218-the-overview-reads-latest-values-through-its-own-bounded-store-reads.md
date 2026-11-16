# 0218: The overview reads the latest values through its own bounded store reads, not the time-series query

- **Status:** Proposed
- **Date:** 2026-11-16
- **Area:** Web UI
- **Source:** Issue #48
- **Supersedes:** —

## Context

The overview page (`/`) shows the current state of the monitored server: the newest live data, the newest batch received, an
open backfill, the agent's spool, the latest state of every service unit, the latest value of a dozen metrics and MariaDB's latest
status ([Overview page](../areas/web-ui-overview.md)). It reads again every 30 seconds on every open page, through the views' read
gate of two concurrent store calls ([Web UI](../areas/web-ui.md), *Store reads of the views*). The time-series query of #47
([Time-series queries](../areas/time-series-query.md)) answers lines over a range for charts. The store has no column or index that
holds "the last live record" or "the newest receive time", and the records table is not indexed by receive time.

## Options considered

1. **The time-series query over a recent range** — reuses #47, but it answers a range, not a latest value: a value older than the
   range is missing instead of stale, a range of hours leaves raw resolution for rollups that lag up to 5 minutes, it does not read
   `mariadb_status` or the live flag, and one query takes a gate slot per store read.
2. **A new column or index kept by the write path** (the newest live capture time in `agent_streams`, an index on live metrics) — an
   exact answer in one seek, but it changes the batch write path and its throughput criterion for a page that only reads.
3. **Own bounded reads over the existing indexes** (chosen) — one reader with four calls: freshness (the agent stream with the
   highest-numbered record's receive time, the open sequence gaps, the newest live record among the newest 20,000 metric records),
   the latest value per label set of a list of series, the latest `mariadb_status` members, and the latest state per unit. Each call
   is one gate slot and bounded by rows, not by the stored data.

## Decision

The overview reads through `IOverviewReader` over the existing indexes, without a schema change; the newest live record is searched
only among the newest 20,000 metric records and reported as older than the oldest of them when none is live, and the newest batch is
read as the receive time of the agent's highest-numbered record.

## Consequences

- No write-path or schema change; each refresh costs five gate slots in turn (four overview calls and one incident summary call).
- "Last live data" is a lower bound (`before <time>`) when more than 20,000 newer metric records are all backfilled (after a long
  backfill whose current data stays backfilled, or an agent clock far off).
- The receive time of the highest-numbered record can be older than the newest backfill batch by up to one live send period; a
  backfill without live batches does not occur, because the live stream goes first.
- An agent whose newest records were removed by retention is still read, without a receive time: *Last data received* shows `unknown`, marked `stale`.
- Every read ignores records captured more than 90 seconds after the page's now, so a clock far ahead hides no current value; such
  records are not shown.
- The latest values hold at most the requested number of label texts and count the others by hash, so a scan of large distinct label
  sets stays bounded in memory; a hash collision can undercount the sets by one.
- `max_connections` is shown only as a parsed integer, so no stored variable text reaches the page outside the external-text display.
- Revisit with option 2 when an alert rule (#56) needs the last live time on every batch.
