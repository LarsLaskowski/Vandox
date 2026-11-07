# 0189: The install scripts read spool.directory of a kept configuration with a fail-closed line reader

- **Status:** Accepted
- **Date:** 2026-10-27
- **Area:** Agent installation
- **Source:** Issue #42
- **Supersedes:** —

## Context

`install.sh` keeps an existing `/etc/vandox/agent.yaml` and must create, and `uninstall.sh --purge` delete, the spool directory it
names. The scripts run as root; the spool directory is owned by the agent's user, who could replace a path element with a link. The
issue adds no change to the agent binary.

## Options considered

1. **A new agent flag that prints the configuration** — changes the binary, which the feature does not.
2. **A small reader of the block form `spool:` / `directory:`, failing closed on every other form, accepting only one directory
   directly below a root-owned `/var/lib/vandox`** (chosen) — root never creates or changes ownership along a path an agent-owned
   directory could redirect with a link; a deeper path would need a checked walk of every element.

## Decision

Option 2. The accepted forms and the messages are in [Agent installation](../areas/agent-installation.md).

## Consequences

- A kept configuration whose `spool` block is written in another YAML form is refused by the scripts, though the agent reads it.
- `spool.directory` is limited to one directory below `/var/lib/vandox`, which is also where the unit allows writes.
