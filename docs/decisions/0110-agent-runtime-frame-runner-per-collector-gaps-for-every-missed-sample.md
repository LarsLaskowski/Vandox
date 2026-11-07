# 0110: The agent's runtime frame: one runner per collector at a fixed rate, a gap for every missed sample

- **Status:** Accepted
- **Date:** 2026-10-17
- **Area:** Agent
- **Source:** Issue #30
- **Supersedes:** —

## Context

The collectors of v0.2.0 (#31 to #37), the spool (#38) and the sender (#39) need one frame to run in. Two accepted
guarantees shape it: a hanging collector never blocks the agent (0029) and no data gap goes unrecorded (0028). The agent
runs as a systemd service on a 2 GB server under memory pressure (0025), where a stuck `/proc` read, a descheduled process
and a restart by the service manager are all real. The spool and the sender did not exist yet when the frame was built, so
the records first went to a sink that dropped them and the spool size and send lag came through provider interfaces that
#38 and #39 implemented; the data model has no event record kind. The notification of systemd is decided in
[0136](0136-systemd-notification-by-hand-and-a-watchdog-gated-on-the-collector-runners.md), the log format in
[0137](0137-agent-log-as-text-lines-on-standard-error-with-the-journals-priority-prefix.md).

## Options considered

1. **Scheduling.** A single ticker loop that starts due collectors — one timer and one in-flight table for all of them;
   a worker pool — a stuck worker takes a slot from the others, against 0029; **one runner goroutine per collector, each
   run in a worker goroutine abandoned at its deadline** (chosen).
2. **Rate.** Fixed delay after each run — drifts by the run time; **fixed rate from the first run** (chosen), so a missed
   slot is detectable and recorded.
3. **Gap span.** Up to the deadline only — leaves the rest of the slot looking normal; **the slot the sample stands for**
   (chosen), so consecutive gaps tile.
4. **A panic "reported as event".** A new record kind or gap cause — a wire change in both languages (0043, 0075) for what
   the log already says; **an error line in the log and a gap naming the collector** (chosen), the rule the wire format
   already gives for a lost sample.
5. **Where records are checked against the wire format.** In the spool, which assigns sequence numbers — it would have to
   record the producer's gap for collectors it does not know; **in the scheduler, once for every collector, with the largest
   storable sequence number assumed** (chosen), so the size check is an upper bound.
6. **A sink that refuses records.** The scheduler records a gap for them — it cannot know what a failed sink kept, and the
   gap would go into the same failing sink; **the sink owns recording the loss, the scheduler logs the error** (chosen).
7. **Memory pressure switch.** Count the next run from the last due time with the new interval even when that moment is
   long past — the late check cannot tell the switch from a frozen agent and records a gap for a span in which nothing was
   missed, against 0028; restart the schedule at the switch in every case — a switch just after a run samples twice within
   the pressure interval; **the last due time plus the new interval, but never earlier than the switch** (chosen): on the
   old grid where possible, never late because of the switch.
8. **Telling a collector that its previous sample was lost** (#34: a collector that records only changes must record
   everything again after a dropped result). The collector checks its context before returning — a deadline can pass
   between the check and the scheduler's receipt, and a failed run, a refused record or a refused sink stays unknown to
   it; a callback that confirms each kept result — a second method on every collector; **a flag in each run, set after a
   run whose result was not kept** (chosen); collectors without such state ignore it.

## Decision

The agent runs one runner per collector at a fixed rate, abandons a run at its deadline, never starts a stuck collector
again until it returns, records a gap for every sample it misses (but never for a memory pressure switch) and tells the next
run when the previous run's result was not kept. The rules are in the [Agent](../areas/agent.md) area (*Scheduling*,
*Self-metrics*).

## Consequences

- Every collector issue (#31 to #37) plugs in as a job with an interval and a timeout and gets isolation, gaps, logging and
  the memory pressure flag from the frame; the thresholds that switch the mode stay with the collectors that measure
  pressure ([0141](0141-memory-pressure-mode-switched-by-the-system-collector-with-fixed-hysteretic-thresholds.md)).
- The spool must record a gap for records it refuses to take; the scheduler only logs a sink failure.
- Revisit if a collector needs a run overlapping its previous one (then the "never started again while stuck" rule of 0029
  needs a superseding record).
