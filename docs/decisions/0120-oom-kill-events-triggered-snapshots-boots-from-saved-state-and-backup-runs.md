# 0120: OOM kill events with triggered snapshots, boots from saved state, and backup runs

- **Status:** Accepted
- **Date:** 2026-10-22
- **Area:** Agent
- **Source:** Issue #36
- **Supersedes:** —

## Context

#36 wants an OOM kill to be recorded within seconds together with a process and connection snapshot linked to it, reboots with the
previous uptime, and backup runs from configurable process patterns, so that a chain such as 2 Oct 2026 (a backup with `pzstd`,
the kill of `pzstd`, then of `mariadbd`) reads as one sequence. The scheduler (0110) runs every collector at a fixed rate and never
starts one twice; the agent had no state that outlives a restart. The Product Manager set the default backup rule to `pzstd` only until Plesk's own processes were captured (#287).

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
   ends any other loop with a gap; the scheduler contract stays as it is. What a run reached (the position, the drift window, the
   files of other seqnum IDs already read) is committed as one unit, so a re-read repeats the run exactly; only files with a head
   time and the online file set the position, so a file of another seqnum ID can neither move it back in time nor hide the online
   file, and a file that keeps failing is given up with a gap after three runs instead of holding the position. **A scheduler signal telling a refused record from a dropped
   result** (a new `Run` field) — the same effect, but a change to the contract `processes` and `connections` also rely on.
9. **Default backup rules `pzstd` only** (chosen by the Product Manager) — the one backup program seen in real data when the record was written; **Plesk's
   documented tool names** — never captured, a wrong rule fails silently; **a capture first** — not available then. #287 added the
   rules taken from a capture: `command` matches would also count a restore, so the two prefixes name the modes `--pack` and `--create`.
10. **A backup run whose records were dropped is decided again: the collector restores its copy of the open run on
    `Run.PreviousLost`** (chosen) — a lost start or end is written again by the next check, and the state stays current for a
    restart; **commit the open run only at the next kept run**, as for the journal position — the stored run then lags one more
    check, so a restart gives a second start within a wider window (S11); **accept the loss** — a dropped end leaves a run without
    an end for ever.

## Decision

A live OOM kill makes the kernel collector request one extra run of `processes` and `connections` through the scheduler, at most one
per collector per 15 seconds, and the snapshots name the kills by their identity. A boot is recognized by comparing the boot ID with
the one in the agent's state file, and a backup run is the span in which a process matches a `backups.processes` rule, recorded as a
`backup_run` start and end; the default rules are `command: pzstd` and the prefixes of `backup-archiver --pack` and `sw-tar --create`. The rules are in the [Agent](../areas/agent.md) area (*Kernel
events*, *Boots*, *Backup runs*) and the [wire format](../areas/wire-format.md).

## Consequences

- The default rules are `pzstd` plus `backup-archiver --pack` and `sw-tar --create`, chosen from the processes captured during a
  Plesk backup (#287); the database dumps run in `backup-archiver`, so they are part of the run. The creating modes keep a restore out.
- An extra snapshot is a sample like any other (predecessor, `Run.PreviousLost`, gaps); without pressure mode it costs at most one more
  snapshot per collector per 15 seconds.
- A span whose result is dropped more than three times in a row is given up with a gap; its kills are then lost although the journal
  may still hold them, for example during a sink error lasting more than four runs (a spool write that keeps failing, #38; a full spool drops its oldest data instead of refusing) even when the sink
  recovers soon after.
- Whether a kill is live is judged with `/proc/uptime`, which counts time in suspend, against the journal's monotonic clock, which
  does not; they agree on a server that never suspends, which could not be measured before the first run on the server (#42 checks
  it). After a suspend every kill reads as late and gets no snapshots: the error is towards fewer snapshots, never more.
- The state file is the agent's first file write; the spool (#38) must leave it out of what it counts and deletes.
- Without a usable state every start records a `boot` event of the current boot (same boot ID), and the journal is read from its end.
- A backup run's state that the `state` job saved between a dropped run and the next one survives a restart before that next run:
  the dropped start or end is then not written again. An unreadable process is skipped like an ended one, without a log line, so a
  backup process the agent cannot read is not recognized.
