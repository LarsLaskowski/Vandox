# 0185: The agent unit's system-call filter is built from @system-service plus adjtimex

- **Status:** Accepted
- **Date:** 2026-10-27
- **Area:** Agent installation
- **Source:** Issue #42
- **Supersedes:** —

## Context

Record 0122 requires a system-call filter that denies six named calls, and the `clock` collector reads `adjtimex` (record 0111). The
Go runtime may make calls at start that a narrow group omits. systemd 249 ignores an unknown unit key with only a log line, so a
hardening setting can silently vanish.

## Options considered

1. **`@system-service` minus `@privileged` and `@resources`** — `@privileged` holds `@clock` and so `adjtimex`, and `@resources` holds
   calls the Go runtime may make at start; re-adding them by name is fragile.
2. **`@system-service` plus `adjtimex`, minus the six calls of record 0122** (chosen) — what the Agent rights area requires; a draft
   scored 2.2 `OK` in `systemd-analyze security`.

## Decision

Option 2. The filter and the rights it serves are in [Agent rights](../areas/agent-rights.md) and
[Agent installation](../areas/agent-installation.md).

## Consequences

- A new collector that needs a call outside `@system-service` adds it by name, a `security`-tier change.
- The CI job on systemd 249 (record 0188) checks the unit with `systemd-analyze`.
