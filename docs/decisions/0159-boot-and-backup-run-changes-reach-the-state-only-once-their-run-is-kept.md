# 0159: A boot and a backup run's change reach the agent's state only once the scheduler reports their run kept

- **Status:** Accepted
- **Date:** 2026-10-22
- **Area:** Agent
- **Source:** Issue #36, Issue #300
- **Supersedes:** —

## Context

The agent's state file ([0156](0156-one-agent-state-file-in-the-spool-directory-written-by-a-state-job.md)) holds the current boot
([0157](0157-boots-recognized-from-the-saved-boot-id-previous-uptime-from-the-saved-uptime.md)) and the open backup run
([0158](0158-backup-runs-as-a-record-kind-of-their-own-recognized-by-process-rules.md)), and every save makes the spool durable first
([0175](0175-the-state-file-syncs-the-spool-before-every-save.md)). In the first form (#36) the `kernel`
and `backups` collectors changed the state inside their runs: a save between a run and the hand-over of its records, followed by a
power loss, lost the `boot` event or the backup start or end for ever while the state already held the change (#300). The scheduler
can tell a collector whether a run's records were kept.

## Options considered

1. **The change takes effect when the scheduler reports the run's records kept** (chosen) — the change reaches the state only after
   the hand-over, so the spool sync before every save makes the record durable before any saved state holds it, and a dropped or
   abandoned run changes nothing, so the next run decides again.
2. **Written inside the run and restored when the next run learns its predecessor was lost** (the first form of backup runs) — a save
   between the run and its hand-over followed by a power loss lost the record, and a save between a dropped run and the next one
   survived a restart.
3. **Written inside the first run** (the first form of boots) — a save before the hand-over followed by a power loss lost the event
   for ever.
4. **Committed only at the next kept run**, as for the journal position — the stored change lags one more run, so a restart gives a
   second start within a wider window, and a stop between the kept run and the next one leaves a boot unsaved.
5. **Accept the loss** — a dropped end leaves a backup run without an end for ever.

## Decision

The `kernel` collector's boot and the `backups` collector's change to the open run take effect in the agent's state only when the
scheduler reports the run that returned them kept. The rules are in [Agent kernel events and boots](../areas/agent-kernel-events.md)
(*Boots*), [Agent services and backup runs](../areas/agent-services.md) (*Backup runs*, **Lost runs**) and the
[Agent](../areas/agent.md) area (*Scheduling*, the observer; *Agent state*).

## Consequences

- Between a run's hand-over and the save of its boot or backup change (at most one state interval) a stop without the last save
  (`kill -9`) gives the boot event or the backup start or end once more at the restart; nothing is lost.
- Because the state holds the previous boot until the event is kept, the `state` job refreshes the saved uptime only of the boot that
  is the current one; refreshing any saved boot wrote the new boot's uptime into the previous boot.
