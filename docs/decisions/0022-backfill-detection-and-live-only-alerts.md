# 0022: Backfill is recognized from the data; alerts only on live values

- **Status:** Accepted
- **Date:** 2026-10-03
- **Source:** Issue #6
- **Supersedes:** —

## Context

After the NAS was off, the agent sends current data and backfills hours of spooled data (0018). Alerting
on backfilled values would send a burst of stale alerts for problems that are long over. The backend has
to tell live data from backfill reliably, without trusting a flag the agent could get wrong.

## Options considered

1. **Agent marks batches as backfill** — simple; relies entirely on the agent's state.
2. **Backend classifies every record from the data itself** — capture time compared with receive time,
   plus gaps in the sequence numbers; works even if the agent restarted or its state is wrong.

## Decision

Option 2: `vandoxd` classifies every record as live or backfilled from its capture time, its receive time
and gaps in the sequence numbers. Alert rules are evaluated on live data only; backfilled data is stored
and analyzed but never alerts.

## Consequences

- No alert storm after a NAS downtime; incidents during the downtime still appear in the analysis and in
  the report (0024).
- The classification depends on the agent's clock being reasonably correct; the threshold between live
  and backfilled has to be chosen and tested.
