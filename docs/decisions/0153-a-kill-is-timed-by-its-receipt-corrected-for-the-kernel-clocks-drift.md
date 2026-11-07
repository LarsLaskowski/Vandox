# 0153: An OOM kill is timed by its receipt in the journal, corrected for the kernel clock's drift

- **Status:** Accepted
- **Date:** 2026-10-22
- **Area:** Agent kernel events and boots
- **Source:** Issue #36
- **Supersedes:** —

## Context

The kernel collector reads the kill lines from the journal files
([0119](0119-kernel-messages-from-the-binary-journal-files-read-in-pure-go-without-decompression.md)). Each kernel entry carries the
kernel's own monotonic stamp of the message and journald's real-time and monotonic stamps of its receipt. On the server the kernel's
printk clock runs slow against the monotonic and real-time clocks, about 33 µs per second, which adds up to a minute after three weeks
of uptime. A kill must be placed correctly against the other records of an outage, such as the 2 Oct 2026 chain of a backup, the kill
of `pzstd` and then of `mariadbd`.

## Options considered

1. **Kill time as the boot time plus the kernel's stamp** — exact in principle, but the kernel's clock runs slow, so the time comes
   out early, by up to a minute on a long-running server.
2. **The receipt time minus the delay, corrected by the drift seen on recent lines** (chosen); liveness is judged on the journal's
   monotonic clock. "Recent" is a span of receipt time, not a count of lines: the drift only grows, so on a quiet boot the smallest
   lag of the last lines by count is that of a line days old and overstates the delay (by 5.8 s for the 2 Oct `pzstd` kill).

## Decision

A kill's capture time is its receipt time minus its delay, where the delay is its lag behind the kernel's stamp less the drift seen
on the recent kernel lines of the same boot. The rules are in [Agent kernel events and boots](../areas/agent-kernel-events.md)
(*Kernel events*, **Capture time**).

## Consequences

- The capture time is never after the receipt; a kill without an earlier line in the window gets no correction.
- The drift window is lost at a restart, so the first kill after a start is timed by its receipt alone.
