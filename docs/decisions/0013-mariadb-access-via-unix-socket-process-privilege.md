# 0013: MariaDB access through a unix_socket user with only the PROCESS privilege

- **Status:** Accepted
- **Date:** 2026-10-03
- **Area:** Network and security model
- **Source:** Issue #6
- **Supersedes:** —

## Context

MariaDB is among the first services to die when the server runs out of memory. The agent should record
its state (status variables, process list) to reconstruct outages, without holding a database password
and without access to customer data.

## Options considered

1. **Use the Plesk admin or root account** — everything visible; a password with full rights on the
   server and full data access.
2. **Dedicated user with a password** — limited rights; a secret to store and rotate.
3. **Dedicated user `vandox-agent` authenticated by `unix_socket` with only the `PROCESS` privilege** — no
   password; authentication by the operating-system user the agent runs as; no access to table data.

## Decision

Option 3: the agent connects over the local socket as MariaDB user `vandox-agent`, identified via
`unix_socket` and granted only `PROCESS`.

The resulting rules are in the [Network and security model](../areas/network-and-security-model.md) area.

## Consequences

- No database secret on disk; the agent must run as OS user `vandox-agent`.
- The agent sees server status and the process list, including query text of running statements, but
  cannot read tables.
- Setting up the user is part of the agent's installation instructions.
