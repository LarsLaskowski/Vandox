# 0136: systemd notification by hand, with a watchdog fed only while every collector runner is on time

- **Status:** Accepted
- **Date:** 2026-10-17
- **Area:** Agent
- **Source:** Issue #30
- **Supersedes:** —

## Context

The agent runs as a systemd service; the issue asks for systemd's `READY=1` and `WATCHDOG=1`. A stuck collector must not
block the agent (0029), and the frame of [0110](0110-agent-runtime-frame-runner-per-collector-gaps-for-every-missed-sample.md)
abandons a stuck run, so a stuck collector alone is no reason for a restart; a runner blocked in the sink or a frozen
process is. The 2 GB server is under memory pressure at exactly the moments that matter (0025), so a briefly descheduled
agent must not be restarted for it. The notification protocol is one datagram per message to a local socket.

## Options considered

1. **Notification library.** `github.com/coreos/go-systemd` — a dependency for about sixty lines; **the protocol with the
   standard library's unix datagram socket and a write deadline** (chosen).
2. **What the watchdog proves.** Ping unconditionally — proves only that one goroutine runs; gate on the scheduler
   goroutine — misses a runner blocked in the sink; **ping only while every runner is within its run's deadline plus a
   slack** (chosen): a stuck collector does not withhold it (its runner moves on), a blocked runner or a frozen process does,
   and the slack keeps a briefly descheduled agent from being restarted under memory pressure.
3. **Lower bound of the watchdog period.** None — a period of one microsecond makes a busy loop; one second — the
   end-to-end test would wait half a second on a real timer, against 0062; **100 milliseconds** (chosen), which already
   rules out the busy loop.

## Decision

The agent speaks systemd's notification protocol itself with the standard library and sends `WATCHDOG=1` only while every
collector runner is on time. The rules are in the [Agent](../areas/agent.md) area (*Service manager*, *Scheduling*).

## Consequences

- No new dependency.
- A runner blocked in the sink for longer than its deadline plus the slack withholds the watchdog, and systemd restarts the
  agent; the unit must set `Type=notify`, `WatchdogSec=` and `NotifyAccess=main`.
- A library behavior the design rests on was measured (Go 1.27, Linux 6.18) and holds: a datagram write to a socket whose
  queue is full returns at the write deadline instead of blocking.
