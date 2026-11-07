# 0173: The spool opens its files relative to its directory's descriptor

- **Status:** Accepted
- **Date:** 2026-10-23
- **Area:** Agent spool
- **Source:** Issue #38, Issue #301
- **Supersedes:** —

## Context

The spool directory also holds the agent's state file. Anything that can plant an entry in the directory could otherwise make the
agent open, write, remove or rename another file through a link, or make it read a file it did not write. The agent uses the standard
library besides its few dependencies. Issue #301 showed that a free-space check by path reads another file system once the path is
swapped after the start.

## Options considered

1. **`os.Root`** — the standard library's confinement to a directory; it follows a symbolic link whose target stays inside the
   directory even with `O_NOFOLLOW`, so a spool-named link to `agent-state.json` would be opened and written.
2. **`golang.org/x/sys/unix` for `Fstatat` with `AT_SYMLINK_NOFOLLOW`** — a new direct dependency for one check.
3. **`syscall.Openat` with `O_NOFOLLOW`, `Unlinkat` and `Renameat` relative to the directory's descriptor, each file trusted only
   after a check on its own descriptor** (chosen) — standard library only; a link fails to open, a hard link fails the link count, and
   removing or renaming a name never touches a target. `syscall` exports no `Fstatat` on amd64, so the check before a removal or
   rename is the same open.

## Decision

Option 3, never `os.Root`; the free-space check reads the file system of the directory's descriptor (#301), never the path. The checks
a file must pass are in [Agent spool](../areas/agent-spool.md).

## Consequences

- A link or a foreign file in the spool directory is refused or treated as damage, never followed.
- Swapping the configured path after the start changes neither the files the spool writes nor the file system it measures.
