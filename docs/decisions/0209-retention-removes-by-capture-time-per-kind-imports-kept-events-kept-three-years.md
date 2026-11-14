# 0209: Retention removes by capture time per record kind; imported data is kept, events are kept three years

- **Status:** Accepted
- **Date:** 2026-11-13
- **Area:** Storage
- **Source:** Issue #46
- **Supersedes:** —

## Context

The database grows without bound (`docs/ARCHITECTURE.md`, *Storage and retention*). Issue #46 names periods for raw data, rollups,
logs, snapshots and incidents, but the stored kinds are more: OOM kills, boots, service states, backup runs, gap records and gap rows,
listener findings, template statistics, and the imported history that the v0.1.0 forensics report rests on. The guarantee *No data
gaps unless explicitly recorded* (record 0028) needs the gaps of a summarized span to outlive its raw values, and the history view
(#49) marks events over a year. The Product Manager answered the open choices on 2026-11-13 (spec, *Open questions*).

## Options considered

1. **Age by receive time** — an old import or a long backfill would stay a whole period after it arrived, and a period would not mean
   "how far back the data reaches".
2. **Age by capture time, every origin alike** — bounded, but the imported history and the report built on it would disappear 90 days
   after the events they describe.
3. **Age by capture time per kind, imports kept, events and gaps kept as long as the hourly values** (chosen, the Product Manager's
   answers Q1 a and b) — the period means the age of the data, the finite and idempotent import stays whole, and a span keeps its
   gaps and markers as long as its hourly values.

## Decision

The running service removes records of origin `agent` and `backend` once their capture time is older than the period of their kind
(`retention.raw`, `logs`, `snapshots`, `events`), the rollups and template statistics by `retention.rollups_5m` and `rollups_1h`, and
incidents by `retention.incidents`; records of origin `import` are never removed. Which kind falls under which period, and what is
kept beyond it, is in [Retention and rollups](../areas/retention.md).

## Consequences

- A record already past its period when it arrives is stored, folded into its buckets and removed by a later pass.
- The template statistics count as hourly values; an hour that still holds a stored line keeps them, and `vandoxd templates` recounts
  only such hours and keeps the others, so a rebuild after a change of the normalization rules leaves old templates in expired hours.
- An incident with a note is never removed (record 0107 asks retention to move or keep notes).
- Gap records and gap rows follow the longest finite period of all data kinds and rollups, never a shorter `events` alone, and an
  open or final sequence gap is never removed, so a span with data or rollups keeps its gaps.
- A forged capture time far in the future keeps its record until then; the free-space floor `ingest.min_free` bounds the effect.
- Several agents (#89) would keep the same periods per agent.
