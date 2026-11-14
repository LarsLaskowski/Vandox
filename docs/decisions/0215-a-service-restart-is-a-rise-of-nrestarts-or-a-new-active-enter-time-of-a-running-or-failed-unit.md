# 0215: A service restart is a rise of NRestarts, or a new active-enter time of a running or failed unit with no boot between

- **Status:** Proposed
- **Date:** 2026-11-14
- **Area:** Time-series queries
- **Source:** Issue #47
- **Supersedes:** —

## Context

Issue #47 asks for restarts as chart markers, and #52 for restarts per service over 30 days. The agent records a unit's `service_state`
on every change, after its own start, after a lost sample and hourly, with systemd's `active_enter_at` and `NRestarts`
([Agent services and backup runs](../areas/agent-services.md)). `NRestarts` counts only the restarts systemd makes under `Restart=`; a
manual `systemctl restart` moves only `active_enter_at`, and so does a plain start and every unit's start after a reboot.

## Options considered

1. **Only a rise of `restarts`** — misses every manual restart.
2. **Every later `active_enter_at`** — counts starts from a stopped state and every tracked unit after a reboot (about ten false
   restarts beside each boot marker).
3. **A rise of `restarts`, or a later `active_enter_at` while the previous record was `active`, `activating`, `reloading`,
   `deactivating` or `failed` and no boot was stored between the two records** (chosen) — catches automatic and manual restarts,
   including a restart of a failed unit, and leaves out starts and boots.

## Decision

Option 3. The previous record is the unit's last `service_state` before the one judged: inside the range from the same read, before it
from one backward scan of at most 10,000 `service_state` records for all units together. A boot lies between when the two records'
boot IDs differ or a boot record is stored between them; an enter time that is newly present counts as moved. The comparison is made
on the parsed values. The rules and the marker's time are in [Time-series queries](../areas/time-series-query.md).

## Consequences

- A restart while the agent was stopped is found at its first record after the start, when the boot check allows it.
- A unit started from `inactive` by hand is a start, not a restart; #52 shows availability from the states themselves.
- A unit whose previous record lies beyond the 10,000 records scanned back (a unit silent for weeks, or forged unit names) has none, so
  its first record in the range is no restart; the scan stays bounded however many units appear.
- The restart is timed at its new enter time only when that lies in the range and not after its record, so a forged enter time cannot
  place a marker outside the range.
