# 0156: The agent keeps one state file in the spool directory, written by a state job

- **Status:** Accepted
- **Date:** 2026-10-22
- **Area:** Agent
- **Source:** Issue #36
- **Supersedes:** —

## Context

#36 needs state that outlives an agent restart: the boot the agent last ran in with its uptime, for the boot event
([0157](0157-boots-recognized-from-the-saved-boot-id-previous-uptime-from-the-saved-uptime.md)), the kernel collector's journal position
([0155](0155-the-kernel-journal-position-is-committed-only-after-a-kept-run-with-a-re-read-cap.md)) and the open backup run
([0158](0158-backup-runs-as-a-record-kind-of-their-own-recognized-by-process-rules.md)). The agent had no file write of its own yet; any
file it writes is a security area (9). The spool, which owns `spool.directory`, was still to come.

## Options considered

1. **One state file in `spool.directory`, written by a `state` job** (chosen) — one atomic write at most every few seconds, through the
   scheduler like every collector.
2. **One file per collector** — more files and more writes.
3. **Writes from each collector run** — more writes and concurrent writers of one file.

## Decision

The agent keeps one small state file in `spool.directory` and writes it only from a `state` job of its own. The format, the rules for
reading and saving it and its bounds are in the [Agent](../areas/agent.md) area (*Agent state*).

## Consequences

- The state file is the agent's first file write; the spool must leave it out of what it counts and deletes.
- Without a usable state every start records a `boot` event of the current boot, and the journal is read from its end.
- Later features (the log positions of log shipping) keep their state in the same file.
