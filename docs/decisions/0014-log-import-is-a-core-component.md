# 0014: Log import is a core component

- **Status:** Accepted
- **Date:** 2026-10-03
- **Area:** Log import
- **Source:** Issue #6
- **Supersedes:** —

## Context

Outages are reconstructed from logs at least as much as from metrics. Past outages are documented in the
server's existing logs, including a legacy log written by a script that periodically captured `top` and
`lsof` output. Without importing that history, the analysis would start empty.

## Options considered

1. **Only metrics, logs later** — less to build first; no forensics for past or log-only events.
2. **Continuous log shipping only** — new events covered; history lost.
3. **Historical import and continuous shipping as a core component** — history and new events in one
   store; parsers for several log formats needed from the start.

## Decision

Option 3: importing historical logs (including the legacy `top`/`lsof` log) and continuously shipping
new log lines are core parts of Vandox, not add-ons.

## Consequences

- Parsers for journal, syslog, MariaDB, mail, Plesk, web server and the legacy format are needed early;
  log parsing is a security area.
- Imported historical data must be kept apart from live data so it never raises alerts (0022).
- Logs are stored with full-text search (0007) for 90 days.
