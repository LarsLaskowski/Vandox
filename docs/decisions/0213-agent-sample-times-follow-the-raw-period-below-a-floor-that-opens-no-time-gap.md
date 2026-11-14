# 0213: Agent sample times follow the raw period, below a floor that opens no time gap

- **Status:** Accepted
- **Date:** 2026-11-13
- **Area:** Storage
- **Source:** Issue #46
- **Supersedes:** —

## Context

`agent_sample_times` holds one row per distinct capture time of an agent's stored metric values; the timeline finds time gaps from a
new time's previous and next stored time ([Storage writing](../areas/storage-writing.md), *Time gaps*). With the `system` collector
every 30 seconds that is several thousand rows a day per agent, so kept forever the table grows without bound, against the spec's N3.
Pruned like the raw values, a late value below the raw horizon finds no true neighbours: the next stored time can lie weeks later, so
the timeline would open a time gap over data that existed and was removed, against spec E3 and the guarantee *No data gaps unless
explicitly recorded* (record 0028). And an agent silent for longer than the raw period would find no previous time when it returns,
so its outage would open no time gap.

## Options considered

1. **Keep every row** — exact, but unbounded: about one row per capture time per agent forever.
2. **Prune with the raw values, no further rule** — bounded, but a late value below the horizon opens a time gap over removed data,
   and a return after a long silence opens none.
3. **Prune with the gap rows, at the gap horizon (record 0209)** — fewer wrong gaps, but bounded only by the longest finite period
   (three years by default), comparable to the 5-minute rollups in size, and still wrong below that horizon.
4. **Prune below the raw horizon, keep each agent's newest row, and record the pruned floor** (chosen) — bounded by the raw period;
   the newest row makes a return after a long silence open its time gap; below the floor a new time only narrows a time gap.

## Decision

Removal deletes an agent's sample times below the raw horizon except its newest and raises the stored sample-time floor before its
first delete, so a pass split into several steps never leaves deleted times above the floor; a sample time below the floor narrows, splits or closes the time gap that contains it but adds no row and opens no time
gap. The rules are in [Retention and rollups](../areas/retention.md) and [Storage writing](../areas/storage-writing.md).

## Consequences

- The time gaps recorded while the raw values were stored stay as gap rows (record 0209); only their neighbour lookup is gone.
- Residual: a backfill below the floor that lies before every time the agent ever sent, or between two removed times more than two
  minutes apart that had no gap, opens no time gap; such a span holds no raw values any more and its rollups are what remains.
- With `retention.raw` `unlimited` nothing is pruned and the floor stays absent.
