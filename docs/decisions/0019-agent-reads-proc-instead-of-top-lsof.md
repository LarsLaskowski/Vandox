# 0019: The agent reads /proc itself instead of running top or lsof

- **Status:** Accepted
- **Date:** 2026-10-03
- **Area:** Agent
- **Source:** Issue #6
- **Supersedes:** —

## Context

Process and connection snapshots are central to reconstructing memory exhaustion. They were captured by a script that ran `top` and `lsof`. Under memory pressure, starting external programs is slow and can fail, and their text output is fragile to parse.

## Options considered

1. **Keep running `top`/`lsof`** — known output, but a fork/exec per snapshot, heavy under memory pressure, and the format depends on tool versions.
2. **Read `/proc` directly** (chosen) — no child processes, structured data, controllable cost; the parsing is the agent's responsibility.

## Decision

Option 2. The legacy `top`/`lsof` log is only imported as history (0014). See the [Agent](../areas/agent.md) area (*Collecting*).

## Consequences

- Snapshots keep working when the server is close to the OOM killer.
- `/proc` parsing needs unit tests with fixtures under `testdata/`.
- Linux only, which matches the monitored server.
