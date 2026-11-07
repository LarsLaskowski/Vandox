# 0163: The state file keys a configured log file by the SHA-256 of its path

- **Status:** Accepted
- **Date:** 2026-10-31
- **Area:** Agent log shipping
- **Source:** Issue #37
- **Supersedes:** —

## Context

The `logs` collector saves a position per configured file in the agent's state file, which is strictly decoded and bounded in size
(16 KiB when #37 was planned). The operator may configure up to 16 paths of up to 1,024 bytes each.

## Options considered

1. **Store the configured paths in the state file** — readable, but 16 paths of 1,024 bytes at JSON's worst escaping exceed the
   state file's bound.
2. **Store a SHA-256 of the path** (chosen) — every path takes the same 64 hex digits, so the bound depends only on the number of
   positions.

## Decision

Option 2: a configured file's position is saved under the SHA-256 of its configured path. The state file's format is in
[Agent](../areas/agent.md) (*Agent state*), the positions in [Agent log shipping](../areas/agent-log-shipping.md).

## Consequences

- The state file's size bound follows from the number of positions alone.
- The state file does not show which path a position belongs to; a reader hashes the configured paths to find out.
