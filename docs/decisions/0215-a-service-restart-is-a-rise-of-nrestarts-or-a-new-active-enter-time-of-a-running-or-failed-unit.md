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

Option 3. The previous record is the unit's last `service_state` before the one judged, of any age, found by a seek on the kind index;
the comparison is made on the parsed values. The rules and the marker's time are in [Time-series queries](../areas/time-series-query.md).

## Consequences

- A restart while the agent was stopped is found at its first record after the start, when the boot check allows it.
- A unit started from `inactive` by hand is a start, not a restart; #52 shows availability from the states themselves.
- `restarts` is compared as a double: counts above 2^53 (only forged ones) compare approximately.
- At most 256 units of a query are sought back; a unit beyond that (only possible with forged records, the agent tracks at most 64
  configured entries) has no previous record and yields no restart from its first record in the range.
