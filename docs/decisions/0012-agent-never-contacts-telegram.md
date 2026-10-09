# 0012: The agent never contacts Telegram itself

- **Status:** Accepted
- **Date:** 2026-10-03
- **Area:** —
- **Source:** Issue #6
- **Supersedes:** —

## Context

Alerts and reports go to the user through a Telegram bot. The bot token is a secret, and every outbound
destination of the agent widens what the monitored server talks to.

## Options considered

1. **Agent sends alerts directly to Telegram** — works while the backend host is off; the bot token lives on an
   internet-facing server, and alerting logic is split across two binaries.
2. **Only the backend talks to Telegram** — one place for the token and the alert rules; no alerts while
   the backend is off.

## Decision

Option 2: only `vandoxd` sends Telegram messages. The agent talks to the backend's ingest port and nothing
else.

## Consequences

- The bot token never leaves the backend host; the agent's only outbound destination is the ingest port (0006,
  0010).
- While the backend host is off there are no alerts; the data is spooled and analyzed after backfill, and alerts
  are raised for live data only (0022).
