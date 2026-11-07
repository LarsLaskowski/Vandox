# 0157: A boot is recognized from the saved boot ID, and the previous uptime is the one the agent saved

- **Status:** Accepted
- **Date:** 2026-10-22
- **Area:** Agent kernel events and boots
- **Source:** Issue #36
- **Supersedes:** —

## Context

#36 wants reboots recorded with the previous uptime, so an outage analysis sees how long the server ran before it went down. The
kernel's boot ID changes with every boot, and the agent keeps a state file across restarts
([0156](0156-one-agent-state-file-in-the-spool-directory-written-by-a-state-job.md)). The journal of the previous boot also holds that
boot's last entry, and the kernel collector already reads the journal
([0119](0119-kernel-messages-from-the-binary-journal-files-read-in-pure-go-without-decompression.md)).

## Options considered

1. **The previous uptime from the uptime the agent saves** (chosen) — independent of the journal; a lower bound, at most about a
   minute short when the agent ran until the boot ended.
2. **The previous uptime from the last journal entry of the previous boot** — exact to the last entry, but a search per boot in the
   journal for one number.

## Decision

The kernel collector recognizes a boot by comparing the current boot ID with the one in the agent's state and records a `boot` event
with the previous boot's saved uptime. The rules are in [Agent kernel events and boots](../areas/agent-kernel-events.md) (*Boots*) and
the record in [Wire format records](../areas/wire-format-records.md) (*`kernel_event`*).

## Consequences

- Boots the agent did not run in are not recorded, and the previous uptime is a lower bound.
- The `state` job refreshes the saved uptime only for the boot that is the current one, so a new boot's uptime never overwrites the
  previous boot's.
- When the boot is written to the state is decided in
  [0159](0159-boot-and-backup-run-changes-reach-the-state-only-once-their-run-is-kept.md).
