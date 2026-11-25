# 0227: Availability is the time a recorded state holds, up to a cap, and gap time is unknown

- **Status:** Proposed
- **Date:** 2026-11-25
- **Area:** Web UI
- **Source:** Issue #52
- **Supersedes:** —

## Context

Issue #52 asks for each service's and MariaDB's availability over 30 days and longer. The agent writes a `service_state` record when a
unit's state changes, after a start or a lost sample, and once an hour otherwise ([Agent services](../areas/agent-services.md)). It
writes one `mariadb_status` record per run, every 30 seconds, or every 10 seconds under memory pressure
([Agent MariaDB collector](../areas/agent-mariadb.md)). No record says how long a state lasted. Record
[0028](0028-data-gaps-are-always-recorded.md) requires that missing data reads as unknown, never as normal. Unit names are agent input,
so the number of distinct units in a long range is not bounded by the agent's own cap of 256 per run. The time-series query's gap
list counts every collector's gaps against one cap of 1,000 and merges none, while the `systemd` collector writes one gap per run as long
as it refuses one unit's state: up to 8,640 touching gaps a day.

## Options considered

1. **Time-weighted: a state holds from its record to the next record of the same unit, at most a cap; time inside a recorded gap that
   concerns the collector, and time beyond the cap, is unknown** (chosen). The caps are the heartbeat plus slack: 1 hour 5 minutes for
   services (the overview's staleness limit) and 2 minutes for MariaDB (four intervals). Pros: on-change records and the 10-second pressure
   sampling do not distort the share; an agent that stopped or failed shows as unknown. Cons: a state that came and went between two
   records is not seen, as for the agent itself.
2. **The share of records per state.** Simple, but on-change services records weigh a flapping unit far above a quiet one, and MariaDB's
   10-second sampling under pressure overweights the periods in which trouble is likely. Rejected.
3. **systemd's own accounting** (`ActiveEnterTimestamp` and the like as durations). The agent records only the enter time, not the time
   spent per state, so this needs an agent change; rejected for this issue.
4. **Evaluate every distinct unit of the range.** Rejected: unbounded memory per circuit. The view evaluates at most 256 units, the first
   in ordinal order of their names, and says how many more exist.
5. **Take a section's gaps from the time-series query's gap list and filter them by collector.** Rejected: its cap and page limit are
   reached by other collectors' gaps and by unmerged per-run gaps, so time a collector recorded would read as unknown. Instead each read
   streams its own collector's gaps and merges overlapping and touching spans before a cap of 1,000 spans (chosen). The collector filter
   runs in C# through the one rule (`SeriesGap`), not as a copy of the collector names in SQL; the merge runs in C# on a bounded sorted
   set, not as a window-function query in SQL, because gap records arrive in capture order, not span order.
6. **Make the 1-year services read fit the 10-second deadline at 256 units on the reference host by reading the existing records
   differently.** Rejected: on that host the read takes 13.9 s (2.24 million records), about four times the development host. On the
   development host, stepping the covering index natively takes 314 ms, reading the needed columns into C# without any fold 1.5 s, and
   a lean stream (no `id` tie sort, availability as an integer) still 2.4 s against 3.2 s, about 11 s projected on the reference host.
   SQL variants are slower: `LAG` windows 2.5 s with a new unit-first index and 3.9 s with a sort, a next-record seek per row 4.1 s. A
   share of records for long ranges is option 2. Parallel reads would take all four reader connections. What remains is to keep state
   runs at ingest, which changes the write path; that is a follow-up issue. The 1-year range at 256 units shows E12 instead; a year fits
   the deadline up to about 130 units (the Product Manager's amendment of AC10).

## Decision

The services view computes availability as the time each recorded state holds, up to the cap of its collector, minus the time of the
gaps that concern that collector, merged into spans and capped at 1,000 spans per read. The rest of the range is unknown. One folding helper serves services and MariaDB, and at most 256
units are evaluated. The rules are in [Services view](../areas/web-ui-services-view.md).

## Consequences

- One gap of the `systemd` collector makes every unit unknown for its time, also the gap written while a single unit's state is refused;
  the page names the case in a note.
- Status records are kept for `retention.snapshots` only, so MariaDB's availability reaches back 30 days by default; keeping it longer
  is a follow-up issue (status values as metrics or rollups).
- Streaming every gap record of the window costs time with many gaps; the 10-second deadline bounds it, and the benchmark measures a
  month of per-run gaps.
- The read time grows with the records of the range: at the agent's cap of 256 units a 1-year range passes the 10-second deadline on the
  reference host (E12, [Benchmarks](../BENCHMARKS.md)); a year fits up to about 130 units.
- Services are streamed from 1 hour 5 minutes before the range, so a quiet unit's state is carried into a short range without a second
  read.
