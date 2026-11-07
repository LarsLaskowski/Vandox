# 0161: Log shipping commits its positions when the scheduler keeps a run

- **Status:** Accepted
- **Date:** 2026-10-31
- **Area:** Agent log shipping
- **Source:** Issue #37
- **Supersedes:** —

## Context

#37 ships logs without loss or duplicates across a restart (spec S4). A collector's run hands its records to the scheduler, which may
drop a run; the kernel collector commits its position at the start of its next run, once the previous run is known to
be kept ([0155](0155-the-kernel-journal-position-is-committed-only-after-a-kept-run-with-a-re-read-cap.md)). A saved position must
never cover records a crash can still lose.

## Options considered

1. **Commit at the start of the next run only, as the kernel collector does** — no change to the scheduler, but a stop cannot know
   whether the last run was kept, so a restart repeats or loses that run (S4).
2. **A scheduler callback when a run's records were kept** (chosen) — commits at once and lets the agent save the state once more
   when it stops.

## Decision

The `logs` collector commits its positions when the scheduler reports the run kept (a new optional callback), gives up a span after
the fourth dropped run as the kernel collector does, and the agent saves its state once more when it stops. The rules are in
[Agent log shipping](../areas/agent-log-shipping.md) and [Agent](../areas/agent.md) (*Scheduling*, *Agent state*).

## Consequences

- A collector whose positions must survive a stop needs the save at the stop and an urgent save within one state interval: the
  urgent threshold became half the state interval, because the `state` job's start times jitter on its grid.
- A crash repeats at most one state interval plus one collector interval of lines (spec E1); exact delivery across a crash would need
  the spool to store positions with the records (record 0175).
