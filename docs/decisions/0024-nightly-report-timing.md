# 0024: Nightly report at 06:00, or after the backfill if the backend host was off

- **Status:** Accepted
- **Date:** 2026-10-03
- **Source:** Issue #6
- **Supersedes:** —

## Context

A daily summary of the night tells the operator whether anything happened. If the backend host was off overnight
(typically 22:00–09:00), the data for the night arrives only by backfill after it is switched on again.

## Options considered

1. **Fixed time only** — predictable; a report after a night with the backend host off would be empty or misleading.
2. **Fixed time, or after the backfill completes when the backend host was off** — always based on complete data.

## Decision

Option 2: the nightly report is sent at 06:00; if the backend host was off at that time, it is sent as soon as the
backfill has completed.

## Consequences

- The backend must know when the backfill is complete (0018, 0022).
- The report is sent by the backend via Telegram (0012); writing it may use the optional AI (0008).
