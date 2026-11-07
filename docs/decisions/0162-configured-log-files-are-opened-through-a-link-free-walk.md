# 0162: Configured log files are opened through a link-free walk

- **Status:** Accepted
- **Date:** 2026-10-31
- **Area:** Agent log shipping
- **Source:** Issue #37
- **Supersedes:** —

## Context

#37 ships the `syslog`-format files the operator configures. The agent reads them through the group `adm` and can read files a
service user cannot; a service user can replace its own log file, or a directory above it, with a link to another file
(`/etc/shadow`), and the agent must never ship that file. The agent uses the standard library only besides its few dependencies, and
a check followed by a separate open leaves a window in which the path can change.

## Options considered

1. **`openat2(RESOLVE_NO_SYMLINKS)`** — one call, but needs `golang.org/x/sys/unix` as a direct dependency.
2. **`os.Root`** — the standard library's confinement, but it follows links inside the root.
3. **Opening each element of the path with `openat` and `O_NOFOLLOW` from `/`** (chosen) — the same refusal as option 1 with the
   standard library, without a window between check and open.

## Decision

Option 3: the `logs` collector opens a configured file only through a walk that refuses a link in any element of its path. The walk,
the further checks on the opened file and the warning for a refused file are in [Agent log shipping](../areas/agent-log-shipping.md)
and [Agent rights](../areas/agent-rights.md).

## Consequences

- A renamed log file is read until its writer has moved, and its position is saved, because rsyslog reopens its files only after all
  renames. After a restart the renamed file is searched by name in its directory and accepted only on the identity of the
  descriptor that was opened, so no window lies between a check and the open; the same walk reaches the directory.
- A configured path that runs through a link is never shipped; the operator names the real path instead.
