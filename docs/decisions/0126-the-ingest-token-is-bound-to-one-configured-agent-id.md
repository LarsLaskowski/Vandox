# 0126: The ingest token is bound to one configured agent ID

- **Status:** Accepted
- **Date:** 2026-10-25
- **Area:** Ingest endpoint
- **Source:** Issue #40
- **Supersedes:** —

## Context

The ingest endpoint (#40) implements the request of record 0124 for the agent's sender (record 0125). The backend has one agent token,
`VANDOX_AGENT_TOKEN` (record 0050 keeps it single until several agents, #89), but the consumer duties of the
[Wire format](../areas/wire-format.md) area require the header's `agent_id` to be checked against the agent the token belongs to. The
token is compared on every request from the tailnet.

## Options considered

1. **Any agent ID with the token** — no new option, but a token copied to a second server mixes two servers' data into the one the web
   UI shows, and the consumer duty is not met.
2. **The first agent ID stored binds the token** — no option, but the binding is invisible, survives a reinstall with a new ID and
   needs a reset path.
3. **An option `ingest.agent_id` naming the agent, ingest disabled while it or the token is unset** (chosen) — one more setting,
   explicit and fail-closed; #89 replaces it with a list of agents and tokens.

## Decision

Option 3: the endpoint accepts a batch only when the bearer token matches the configured token and the header's `agent_id` equals
`ingest.agent_id`. The token is compared through the SHA-256 digests of both values, because a fixed-time comparison of the strings
returns early on a different length; the digests have a fixed length, at the cost of one hash per request. The statuses and log lines
are in [Ingest endpoint](../areas/ingest-endpoint.md), the option in [Configuration options](../areas/configuration-options.md).

## Consequences

- An operator sets `ingest.agent_id` to the agent's `agent_id`; until then the service runs for the web UI and the import, and ingest
  answers that it is unavailable (logged at start).
- A wrong `ingest.agent_id` is answered as a refused token, which the agent logs; the backend's log line names the presented ID.
