# 0009: Remote actions only as signed commands from a fixed local action list

- **Status:** Proposed
- **Date:** 2026-10-03
- **Source:** Issue #6
- **Supersedes:** —

## Context

Later, Vandox should be able to react to a known failure (for example restart a crashed service). Any
remote execution path is a high-value target: whoever controls the backend or the transport must not be
able to run arbitrary commands on the server.

## Options considered

1. **Arbitrary remote shell commands** — flexible; turns the backend into a root shell on the server.
2. **Signed commands naming an entry of a fixed action list defined locally on the server, executed after
   confirmation** — only pre-approved actions can run; adding an action requires a change on the server.
3. **No remote actions at all** — safest; every reaction is manual.

## Decision

Option 2: remote actions exist only as signed commands that reference an action from a fixed list
configured locally on the monitored server, and only after the user has confirmed them. The agent pulls
them (0006) and verifies the signature before executing. Not part of v0.1.0 (0020).

## Consequences

- A compromised backend can at most trigger listed actions, and only with a valid signature.
- A signing key has to be managed (a security area in `.squad/project.md`).
- New actions require a change on the server, by design.
