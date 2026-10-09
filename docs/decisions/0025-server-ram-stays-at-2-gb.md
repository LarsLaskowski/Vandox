# 0025: The server's RAM stays at 2 GB

- **Status:** Accepted
- **Date:** 2026-10-03
- **Area:** —
- **Source:** Issue #6
- **Supersedes:** —

## Context

The server (Ubuntu 22.04, Plesk 18) has about 2 GB RAM, and its known failure mode is memory exhaustion,
the OOM killer, and MariaDB, Plesk and mail going down. More RAM would be the obvious fix.

## Options considered

1. **Upgrade the RAM** — removes the pressure; ongoing cost, and the causes stay unknown.
2. **Keep 2 GB and relieve through swap, backup tuning and a service inventory** — no extra cost; needs
   evidence of what consumes memory, which is what Vandox provides.

## Decision

Option 2: the RAM stays at 2 GB. Relief comes from swap, tuning the backups, and an inventory of running
services (with reversible disabling, 0026).

## Consequences

- Vandox itself must be frugal on the server; the agent's memory and CPU use is a design constraint.
- The analysis must show memory consumers over time to guide the tuning.
