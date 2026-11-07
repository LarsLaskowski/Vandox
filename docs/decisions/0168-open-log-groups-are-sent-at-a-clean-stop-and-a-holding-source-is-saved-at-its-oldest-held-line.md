# 0168: Open log groups are sent at a clean stop, and a holding source is saved at its oldest held line

- **Status:** Accepted
- **Date:** 2026-10-31
- **Area:** Agent log shipping
- **Source:** Issue #290
- **Supersedes:** —

## Context

The live classification (record 0167) holds lines in open groups for up to 60 seconds of capture time. No held line may be lost
silently, neither at an agent crash nor at a stop; a crash may repeat lines (spec E2). The state file is bounded at 64 KiB and holds at
most 256 journal positions; one MariaDB entry or kernel report per source can reach 16 KiB, for the journal and up to 16 files.

## Options considered

1. **Keep the open groups in the state file** — a restart would resume exactly, but the groups of all sources exceed the state file's
   bound.
2. **Replay marks** — the saved state would keep, per source, the oldest held line and the first positions of the open groups, and a
   restart would read again and send only those groups. Rejected in the plan review: discarding "already sent" output is right only if
   the reading again is identical, and a changed drop rule, zone or agent version breaks that, so held lines would be lost without a
   gap; the marks also need new invariants in the state file (their count, agreement with the position cap).
3. **Send the open groups at a clean stop through a new scheduler hook, and save a holding source's position at its oldest held line**
   (chosen, after the plan review) — a clean stop sends every group, possibly cut in two, and repeats nothing; a crash reads the
   holding source again from that line; nothing read again is ever discarded.

## Decision

Option 3: a source with an open group is saved at its oldest held line, and the agent's stop sends the open groups through a
scheduler hook before the last save. The order and bounds of the stop are in [Agent log shipping](../areas/agent-log-shipping.md) and
[Agent](../areas/agent.md) (*Agent state*).

## Consequences

- A crash repeats the lines of a holding source from its oldest held line; a clean stop repeats nothing.
- The scheduler gains a stop hook that only this collector uses so far. The hook runs after the runners stop and while the spool is
  still open, skips a collector whose worker does not return within the stop's deadline and commits only through the kept callback,
  so a hanging run or flush never overlaps the flush, the commit or the last save (plan review, B4).
