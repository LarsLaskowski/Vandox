# 0110: The agent's runtime frame: one runner per collector at a fixed rate, a gap for every missed sample, sd_notify by hand with a watchdog gated on the runners, journal logging on stderr

- **Status:** Proposed
- **Date:** 2026-10-17
- **Area:** Agent
- **Source:** Issue #30
- **Supersedes:** —

## Context

The collectors of v0.2.0 (#31 to #37), the spool (#38) and the sender (#39) need one frame to run in. Two accepted
guarantees shape it: a hanging collector never blocks the agent (0029) and no data gap goes unrecorded (0028). The agent
runs as a systemd service (#42 writes the unit) on a 2 GB server under memory pressure (0025), where a stuck `/proc`
read, a descheduled process and a restart by the service manager are all real. The issue asks for journal logging, a
memory pressure hook, self-metrics (RSS, CPU, goroutines, spool size, send lag) and systemd's `READY=1` and
`WATCHDOG=1`. The spool and the sender do not exist yet, and the data model has no event record kind.

## Options considered

1. **Scheduling.** A single ticker loop that starts due collectors — one timer and one in-flight table for all of them;
   a worker pool — a stuck worker takes a slot from the others, against 0029; **one runner goroutine per collector, each
   run in a worker goroutine abandoned at its deadline** (chosen).
2. **Rate.** Fixed delay after each run — drifts by the run time; **fixed rate from the first run** (chosen), so a missed
   slot is detectable and recorded.
3. **Where records are checked against the wire format.** In the spool, which assigns sequence numbers — it would have to
   record the producer's gap for collectors it does not know; **in the scheduler, once for every collector, at the largest
   storable sequence number** (chosen), so the size check is an upper bound.
4. **A panic "reported as event".** A new record kind or gap cause — a wire change in both languages (0043, 0075) for what
   the log already says; **an error line in the log and a gap with cause `unknown` naming the collector** (chosen), the
   rule the wire format already gives for a lost sample.
5. **Gap span.** Up to the deadline only — leaves the rest of the slot looking normal; **the slot the sample stands for,
   from its due time to one interval later** (chosen), so consecutive gaps tile.
6. **systemd notification.** `github.com/coreos/go-systemd` — a dependency for about sixty lines; **the protocol with the
   standard library's unix datagram socket and a write deadline** (chosen).
7. **Watchdog.** Ping unconditionally — proves only that one goroutine runs; gate on the scheduler goroutine — misses a
   runner blocked in the sink; **ping only while every runner is within its run's deadline plus a slack of 30 seconds**
   (chosen): a stuck collector does not withhold it (its runner moves on), a blocked runner or a frozen process does, and
   the slack keeps a briefly descheduled agent from being restarted under memory pressure.
8. **Logging.** JSON lines — hard to read in `journalctl`; the native journal socket — a second socket for fields nobody
   queries; **text lines on standard error with the syslog priority prefix that systemd reads, when the journal stream is
   set** (chosen).
9. **Records before the spool exists.** Wait for #38 with the whole frame — blocks every collector issue; log the records —
   writes process names and log text into the journal; **a sink interface, with a sink that drops the records until #38**
   (chosen).
10. **Spool size and send lag.** Walk the spool directory now — a second definition next to #38's; **provider interfaces
    that #38 and #39 implement** (chosen).

## Decision

The agent runs one runner per collector at a fixed rate, abandons a run at its deadline, never starts a stuck collector
again until it returns, and records a gap for every sample it misses (timeout, skip, panic, error, refused record, late
start); it notifies systemd with the standard library and sends `WATCHDOG=1` only while every runner is on time. The
rules are in the [Agent](../areas/agent.md) area (*Scheduling*, *Self-metrics*, *Service manager*, *Logging*) and the
metric names in the [wire format](../areas/wire-format.md) (*Metric names*).

## Consequences

- Every collector issue (#31 to #37) plugs in as a job with an interval and a timeout and gets isolation, gaps, logging and
  the memory pressure flag from the frame; the thresholds that switch the mode stay with #31 and #32.
- Until the spool exists (#38) the agent drops what it collects. It is not released in that state: v0.2.0 (#45) waits for
  the spool and the sender. #38 replaces the sink, assigns the sequence numbers and adds the spool size provider; #39 adds
  the send lag provider.
- A runner blocked in the sink for more than its deadline plus 30 seconds withholds the watchdog, and systemd restarts the
  agent after `WatchdogSec=`; the unit (#42) must set `Type=notify`, `WatchdogSec=` and `NotifyAccess=main`.
- Unverified until measured (the plan names the measurement): that a datagram write to a socket whose buffer is full
  returns at the write deadline instead of blocking.
- Revisit if the agent needs structured journal fields (then the native journal protocol) or if a collector needs a run
  overlapping its previous one (then the "never started again while stuck" rule of 0029 needs a superseding record).
