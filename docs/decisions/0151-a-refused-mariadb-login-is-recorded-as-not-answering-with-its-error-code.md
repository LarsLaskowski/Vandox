# 0151: A MariaDB login the server refuses is recorded as not_answering with its error number

- **Status:** Accepted
- **Date:** 2026-10-21
- **Area:** Agent MariaDB collector
- **Source:** Issue #35
- **Supersedes:** —

## Context

The MariaDB collector ([0118](0118-mariadb-collector-hand-written-client-captured-statements.md)) logs in over the local socket every
run. A server that is full ("too many connections"), shutting down, or that refuses the agent's account answers the login with an error
packet. An outage analysis needs exactly that state. The `mariadb_status` record kind exists since format 1.0 (released with v0.1.0)
with the availabilities `up`, `down` and `not_answering`; under 0043 a 1.0 decoder rejects an unknown enumeration value.

## Options considered

1. **A gap** — no wire change, but loses the "too many connections" state an outage analysis needs.
2. **A new availability `refused`** — a 1.0 decoder rejects an unknown value, so it is not a minor change under 0043.
3. **`not_answering` with the new optional field `error_code`** (chosen) — additive, and a reader that ignores the field still reads
   a true statement.

## Decision

A login refused with an error packet is recorded as `not_answering` with the server's error number in `error_code`. The rules are in
[Agent MariaDB collector](../areas/agent-mariadb.md) and the field in [Wire format records](../areas/wire-format-records.md)
(*`mariadb_status`*).

## Consequences

- The error numbers of a refused login were built from the protocol documentation, not captured (0118, residual risk).
- A reader that wants to tell a refusal from a silent server reads `error_code`; one that ignores it sees the server as not answering.
