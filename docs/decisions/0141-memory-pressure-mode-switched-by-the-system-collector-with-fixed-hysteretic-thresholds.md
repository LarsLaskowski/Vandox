# 0141: The memory pressure mode is switched by the system collector with fixed, hysteretic thresholds

- **Status:** Accepted
- **Date:** 2026-10-17
- **Area:** Agent
- **Source:** Issue #32
- **Supersedes:** —

## Context

The runtime frame (0110) offers a memory pressure mode in which collectors run at shorter intervals and take fuller samples,
but leaves its thresholds to the collectors that measure pressure (#31 and #32); #31 shipped none, so nothing switched the
mode. Issue #32 asks to switch it when pressure stall information or `MemAvailable` crosses a threshold. The values come from
`/proc/meminfo` and `/proc/pressure/memory`, which the `system` collector already reads every 30 seconds
([0111](0111-system-metrics-three-collectors-from-proc-sys-statfs-and-adjtimex.md)). No threshold had been tried on the server
yet.

## Options considered

1. **Where the thresholds live: a separate pressure collector** — a second reader of the same files and another runner.
2. **The process collector** — it runs once a minute, too late.
3. **The `system` collector, after each successful sample it already takes of `meminfo` and the pressure files** (chosen),
   which reports a change to the scheduler.
4. **Thresholds as configurable options** — more configuration surface before any value has been tried on the server.
5. **Fixed values with hysteresis** (chosen): one threshold on `MemAvailable` and one on the memory `some avg10` to switch on,
   and stricter ones on both to switch off, so the mode does not flap at the border.

## Decision

The `system` collector switches the memory pressure mode after each successful sample from `MemAvailable` and the memory
`some avg10`, with fixed thresholds that switch off only below stricter ones than switch on. The values and effects are in the
[Agent](../areas/agent.md) area (*Memory pressure mode*).

## Consequences

- The switch can lag the pressure by up to one `system` interval.
- Any local user can switch the mode on by allocating memory; the cost is bounded by the record budgets and the spool's size
  bound.
- The thresholds become options once the parallel run of #45 shows a reason; they are not measured on the server yet.
