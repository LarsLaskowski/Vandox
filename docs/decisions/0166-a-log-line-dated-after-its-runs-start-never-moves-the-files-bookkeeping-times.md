# 0166: A log line dated after its run's start never moves the file's bookkeeping times

- **Status:** Accepted
- **Date:** 2026-10-31
- **Area:** Agent log shipping
- **Source:** Issue #289
- **Supersedes:** —

## Context

The `logs` collector keeps a last time per configured file, which starts the gap of a truncation, a lost rotation or a given-up span.
The error log formats of #289, and RFC 3339 syslog lines, carry a year the writer controls, so a writer can date a line in 2262. A gap
that starts after lines it loses hides them, against the guarantee that a loss is always recorded.

## Options considered

1. **The line's time as the file's bookkeeping time** — as #37 did; a forged year moves the start of a gap past the lost lines.
2. **The run's start plus the 24-hour tolerance of year-less times** — still lets a gap start a day ahead.
3. **The earlier of the line's time and the run's start** — moves a gap start to that run's start, past honest lines written before
   it that a run stopped on its budget right after the forged line left unread.
4. **A line dated after the start of the run that reads it does not move the file's last time** (chosen) — the last time keeps its
   previous value, an earlier line's time or the start of the run that took the position, which lies at or before the forged line's
   real write time, so a gap never starts after a lost line.

## Decision

Option 4 for the file's last time; the run's latest time and the ends of a given-up span's and a pending lost rotation's gap take
the earlier of the line's time and the run's start, so a forged line and the raw lines after it still end such a gap at the run's start.
The record keeps its own time. The rules are in [Agent log shipping](../areas/agent-log-shipping.md).

## Consequences

- The start of a truncation's, a lost rotation's or a given-up span's gap never passes a lost line.
- Positions saved before this change with a future time (RFC 3339 syslog lines only) stay unbounded until the next line dated before
  its run is read: accepted.
- The live classification (record 0167) expires a group by the same rule.
