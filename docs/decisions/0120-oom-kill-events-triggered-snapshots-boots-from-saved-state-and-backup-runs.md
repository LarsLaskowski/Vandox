# 0120: OOM kill events with triggered snapshots, boots from saved state, and backup runs

- **Status:** Proposed
- **Date:** 2026-10-22
- **Area:** Agent
- **Source:** Issue #36
- **Supersedes:** —

## Context

#36 wants an OOM kill to be recorded within seconds together with a process and connection snapshot linked to it, reboots with the
previous uptime, and backup runs from configurable process patterns, so that a chain such as 2 Oct 2026 (a backup with `pzstd`,
the kill of `pzstd`, then of `mariadbd`) reads as one sequence. The scheduler (0110) runs every collector at a fixed rate and never
starts one twice; the agent had no state that outlives a restart. The Product Manager set the default backup rule to `pzstd` only.

## Options considered

1. **An extra run requested from the scheduler (`Scheduler.Trigger`)** (chosen) — the deadline, the gap rules and "never started
   twice" stay the scheduler's; under pressure mode the extra run replaces the next regular one.
2. **Collectors take the extra snapshot themselves, outside the scheduler** — no scheduler change, but a second run beside a stuck
   one and no gap for a lost snapshot, against 0029 and 0028.
3. **Switch memory pressure mode on for a while** — no new API, but it changes every collector's interval and links nothing.
4. **The link as the spool's sequence number of the event** — exact, but the spool (#38) does not exist and the collector cannot know
   the number; the kill's own identity (boot ID, kernel time, victim PID) is stable across a second reading as well (chosen).
5. **Backup runs as a new record kind `backup_run`** (chosen) — a minor-version addition while 1.1 is unreleased (0043).
   **As `kernel_event` types** — no new kind, but the kernel reports no backup. **As `log_line` events** — not a log line.
6. **One state file in `spool.directory`, written by a `state` job** (chosen) — one atomic write at most every 10 seconds; **one file
   per collector** or **writes from each collector run** — more writes and concurrent writers.
7. **The previous uptime from the uptime the agent saves** (chosen) — independent of the journal; **from the last journal entry of the
   previous boot** — exact to the last entry, but a search per boot in the journal for one number.
8. **Commit the journal position at the next run when the previous one was kept** (chosen) — a run whose result was dropped
   (timeout, error, sink error) is read again (same identities); **commit when the run returns** — simpler, but moves past kills whose
   records were lost without a gap (rejected by the Product Manager as option 3 of the B6 escalation).
   A record the scheduler would refuse must not hold the position back, or one such record makes the same span resend forever, since
   storage deduplicates by sequence number, not by kill identity. **The collector checks its own records and caps the re-reads**
   (chosen by the Product Manager): every record passes the scheduler's own check before it is returned, a failing one becomes a gap
   that is valid by construction, so `Run.PreviousLost` can only mean a dropped result, and a cap of three re-reads of the same position
   ends any other loop with a gap; the scheduler contract stays as it is. **A scheduler signal telling a refused record from a dropped
   result** (a new `Run` field) — the same effect, but a change to the contract `processes` and `connections` also rely on.
9. **Default backup rules `pzstd` only** (chosen by the Product Manager) — the one backup program seen in real data; **Plesk's
   documented tool names** — never captured, a wrong rule fails silently; **a capture first** — not available.

## Decision

A live OOM kill makes the kernel collector request one extra run of `processes` and `connections` through the scheduler, at most one
per collector per 15 seconds, and the snapshots name the kills by their identity. A boot is recognized by comparing the boot ID with
the one in the agent's state file, and a backup run is the span in which a process matches a `backups.processes` rule, recorded as a
`backup_run` start and end; the default rule is `command: pzstd` only. The rules are in the [Agent](../areas/agent.md) area (*Kernel
events*, *Boots*, *Backup runs*) and the [wire format](../areas/wire-format.md).

## Consequences

- Until #45 captures Plesk's own backup processes, a backup is recognized only while `pzstd` runs: the database dump before the
  compression is not part of a run unless the operator adds a rule.
- An extra snapshot is a sample like any other (predecessor, `Run.PreviousLost`, gaps); without pressure mode it costs at most one more
  snapshot per collector per 15 seconds.
- A span whose result is dropped more than three times in a row is given up with a gap; its kills are then lost although the journal
  may still hold them, for example during a sink error lasting more than four runs (later a full spool, #38) even when the sink
  recovers soon after.
- The state file is the agent's first file write; the spool (#38) must leave it out of what it counts and deletes.
- Without a usable state every start records a `boot` event of the current boot (same boot ID), and the journal is read from its end.
