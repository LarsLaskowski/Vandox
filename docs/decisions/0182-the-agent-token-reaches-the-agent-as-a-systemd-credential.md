# 0182: The agent token reaches the agent as a systemd credential

- **Status:** Accepted
- **Date:** 2026-10-27
- **Area:** Agent installation
- **Source:** Issue #42
- **Supersedes:** —

## Context

The agent needs its ingest token at start (record 0050), and the token must never leak (security area 8): not into the process
environment, an argument or a file the agent's user can read at rest. The target is Ubuntu 22.04 with systemd 249.

## Options considered

1. **`VANDOX_AGENT_TOKEN_FILE` naming a file the agent's group reads** — the token sits readable for the agent's user at rest.
2. **`EnvironmentFile=`** — the token lands in the process environment, where `VANDOX_AGENT_TOKEN` would also be shown by tools.
3. **`LoadCredential=` from a root-only file, the path written out without `%d`** (chosen) — systemd hands the service a private copy
   for its lifetime; `%d` is avoided so the unit does not depend on when systemd learned the specifier.

## Decision

Option 3: the token file is root-only, and the unit hands it to the agent with `LoadCredential=`, the agent reading the copy through
`VANDOX_AGENT_TOKEN_FILE`. The paths are in [Agent installation](../areas/agent-installation.md) (*The unit in short*).

## Consequences

- A compromised agent can read its own token while it runs, but no other user and no tool that lists environments sees it.
- `install.sh` reads the token from standard input, never from an option, and writes the root-only file.
