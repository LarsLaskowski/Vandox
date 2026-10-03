# 0026: Services are only disabled reversibly; hosting-provider agents are never touched

- **Status:** Proposed
- **Date:** 2026-10-03
- **Source:** Issue #6
- **Supersedes:** —

## Context

The service inventory (0025) will find services that are not needed. Removing them saves memory but can
break Plesk or the hosting provider's management and support access.

## Options considered

1. **Uninstall unneeded services** — maximum savings; hard to undo, risk of breaking Plesk updates.
2. **Disable reversibly** (e.g. `systemctl disable`/`mask`, documented) — easy to undo; packages stay
   installed.

## Decision

Option 2: services are only disabled in a reversible, documented way. Agents of the hosting provider are
never disabled or changed.

## Consequences

- Every change can be rolled back.
- The service inventory must identify hosting-provider agents and exclude them from any action,
  including future remote actions (0009).
