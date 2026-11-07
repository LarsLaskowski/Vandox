# 0120: A live OOM kill triggers extra process and connection snapshots through the scheduler, linked by the kill's identity

- **Status:** Accepted
- **Date:** 2026-10-22
- **Area:** Agent kernel events and boots
- **Source:** Issue #36
- **Supersedes:** —

## Context

#36 wants an OOM kill to be recorded within seconds together with a process and connection snapshot linked to it, so that a chain such
as 2 Oct 2026 (a backup with `pzstd`, the kill of `pzstd`, then of `mariadbd`) reads as one sequence. The kernel collector reads the
kills from the journal ([0119](0119-kernel-messages-from-the-binary-journal-files-read-in-pure-go-without-decompression.md)); the
scheduler ([0110](0110-agent-runtime-frame-runner-per-collector-gaps-for-every-missed-sample.md)) runs every collector at a fixed rate
and never starts one twice. The spool, which assigns the sequence numbers, did not exist yet. The other decisions of #36 have records
of their own: the journal position
([0155](0155-the-kernel-journal-position-is-committed-only-after-a-kept-run-with-a-re-read-cap.md)), the agent's state file
([0156](0156-one-agent-state-file-in-the-spool-directory-written-by-a-state-job.md)), boots
([0157](0157-boots-recognized-from-the-saved-boot-id-previous-uptime-from-the-saved-uptime.md)) and backup runs
([0158](0158-backup-runs-as-a-record-kind-of-their-own-recognized-by-process-rules.md)).

## Options considered

1. **An extra run requested from the scheduler** (chosen) — the deadline, the gap rules and "never started twice" stay the
   scheduler's; under pressure mode the extra run replaces the next regular one.
2. **Collectors take the extra snapshot themselves, outside the scheduler** — no scheduler change, but a second run beside a stuck
   one and no gap for a lost snapshot, against 0029 and 0028.
3. **Switch memory pressure mode on for a while** — no new API, but it changes every collector's interval and links nothing.
4. **The link as the spool's sequence number of the event** — exact, but the spool did not exist and the collector cannot know the
   number; **the kill's own identity (boot ID, kernel time, victim PID)**, stable across a second reading as well (chosen).

## Decision

A live OOM kill makes the kernel collector request one extra run of `processes` and `connections` through the scheduler, rate-limited
per collector, and the snapshots name the kills by their identity. The rules are in
[Agent kernel events and boots](../areas/agent-kernel-events.md) (*Extra snapshots*), the [Agent](../areas/agent.md) area
(*Scheduling*, the extra run) and the trigger in [Wire format records](../areas/wire-format-records.md) (*Snapshot trigger*).

## Consequences

- An extra snapshot is a sample like any other (predecessor, the lost-sample flag, gaps); without pressure mode it costs at most one
  more snapshot per collector per rate-limit interval.
- Whether a kill is live is judged with `/proc/uptime`, which counts time in suspend, against the journal's monotonic clock, which
  does not; they agree on a server that never suspends, which the install script checks. After a suspend every kill reads as late and
  gets no snapshots: the error is towards fewer snapshots, never more.
