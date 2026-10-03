# 0019: The agent reads /proc itself instead of running top or lsof

- **Status:** Proposed
- **Date:** 2026-10-03
- **Source:** Issue #6
- **Supersedes:** —

## Context

Process and connection snapshots are central to reconstructing memory exhaustion. Until now they were
captured by a script that ran `top` and `lsof`. Under memory pressure, starting external programs is slow
and can fail, and their text output is fragile to parse.

## Options considered

1. **Keep running `top`/`lsof`** — known output; a fork/exec per snapshot, heavy under memory pressure,
   output format depends on tool versions.
2. **Read `/proc` directly in Go** — no child processes, structured data, controllable cost; the parsing
   is the agent's responsibility.

## Decision

Option 2: the agent collects process, memory and connection data by reading `/proc` itself. The legacy
`top`/`lsof` log is only imported as history (0014).

## Consequences

- Snapshots keep working when the server is close to the OOM killer.
- `/proc` parsing needs unit tests with fixtures under `testdata/`.
- Linux only, which matches the monitored server.
