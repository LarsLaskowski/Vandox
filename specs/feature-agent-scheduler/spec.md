# Spec: Agent skeleton and collector scheduler

Status: Approved by Lead

Source: Issue #30 (depends on #10 and #11, both closed and implemented in `internal/model`, `internal/wire` and
`internal/config`).

## Problem / motivation

`vandox-agent` exists only as a binary that prints its version (`cmd/vandox-agent`, `internal/cli`). The collectors of
v0.2.0 (#31 to #37), the spool (#38) and the sender (#39) need a runtime frame to plug into: a process that loads its
configuration, logs to the journal, runs every collector isolated under its own interval and deadline so that a hung
source never blocks the agent (guarantee of record 0029), records every missed sample as a gap (guarantee of record
0028), reports its own resource use and tells systemd that it is ready and alive.

## Behavior

1. Started without `-version`, the binary runs the agent: it reads the configuration file (`/etc/vandox/agent.yaml`, or
   the path given with `-config <path>`) and the agent token from the environment with the existing loader, and runs
   until it receives SIGTERM or SIGINT, then stops and exits with code 0.
2. A configuration that cannot be loaded stops the agent at start-up with exit code 1 and an error line that names the
   problem as the loader reports it (never a value or the secret).
3. `-version` keeps printing the version line; `-h` / `-help` print the usage (exit 0); an unknown flag, a positional
   argument or an empty `-config` is a usage error (exit 2).
4. The agent logs structured lines (key=value) to standard error at the configured `log.level`; under systemd (the
   journal stream systemd names is the agent's standard error) every line carries its syslog priority so the journal
   shows the level, and no time stamp of its own. Values and lines are cut short enough that the journal never splits an
   entry. The configuration and the secret are never logged.
5. Every collector has its own interval and deadline and runs in isolation; its first run starts when the agent starts,
   later runs at fixed intervals from the first.
6. A collector that does not finish by its deadline is abandoned: the other collectors keep running at their own times,
   the missed sample is recorded as a gap (`collector_timeout`), and the stuck collector is not started again while it
   is still stuck; every cycle skipped for that reason is a gap as well. When it finally returns, its late result is
   discarded and the collector runs again at its next time.
7. A collector that panics or returns an error loses only that sample: the panic is recovered, logged as an error with
   the collector's name, and the sample is recorded as a gap (`unknown`); the collector runs again at its next time.
8. A record a collector produces that the wire format would refuse is not passed on; its sample gets a gap (`unknown`)
   and the collector's other records are kept.
9. When a collector's run starts a full interval or more late (the agent was frozen or descheduled), the missed span is
   recorded as a gap (`unknown`) and the collector runs at once.
10. Memory pressure mode: the agent can be switched into it and out of it at run time; while it is on, collectors that
    declare a shorter pressure interval run at that interval (counted from their last due time, but never earlier than
    the switch), the others are unchanged, and every collector can see whether the mode was on when its run started. A
    switch itself never records a gap.
11. Self-metrics: the agent records its own resident memory, CPU time, number of goroutines and — once the spool and the
    sender exist — the spool size and the send lag, as ordinary metric records, through the same scheduler as every
    collector.
12. systemd: when started by systemd with a notification socket, the agent reports `READY=1` once it runs, `WATCHDOG=1`
    at half the watchdog period systemd set (at least 100 ms, a shorter period disables the watchdog) while its
    collector runners are on time, and `STOPPING=1` when it stops. Without a notification socket, or with one it does
    not support, it runs the same and sends nothing.
13. Until the spool exists (#38), the records the scheduler produces are dropped after the scheduler's checks; the agent
    is not released with this behavior (v0.2.0 waits for #38 and #39).

## Acceptance criteria

- [ ] AC1: A deliberately hanging test collector does not delay others (issue).
- [ ] AC2: Watchdog notifications sent (issue).
- [ ] AC3: Self-metrics recorded (issue).
- The detailed criteria are in [plan.md](plan.md), *Acceptance criteria*.

## Out of scope

- The collectors themselves (#31 to #37) and the decision when memory pressure is high (the thresholds belong to #31 and
  #32; this feature provides the switch).
- The spool, sequence numbers and the gap of a stopped agent (`agent_not_running`) (#38); the sender, backfill and send
  lag computation (#39).
- The systemd unit with `Type=notify` and `WatchdogSec=`, the user and the installation (#42).
- Per-collector configuration options: each collector issue decides whether its interval becomes an option.
- Shipping the agent's own log lines to the backend (#37).

## Open questions

None.
