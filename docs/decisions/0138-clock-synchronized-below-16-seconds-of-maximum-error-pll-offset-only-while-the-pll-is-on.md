# 0138: The clock counts as synchronized below 16 seconds of maximum error; the offset is the kernel PLL's, only while the PLL is on

- **Status:** Accepted
- **Date:** 2026-10-17
- **Area:** Agent system metrics
- **Source:** Issue #31
- **Supersedes:** —

## Context

Issue #31 asks for the clock synchronization of the server. The `clock` collector reads it with `adjtimex` without changing
anything ([0111](0111-system-metrics-three-collectors-from-proc-sys-statfs-and-adjtimex.md)), so it sees the kernel's clock
discipline, not the time daemon's own state. The server runs Ubuntu 22.04, whose default daemon is systemd-timesyncd; chrony
is common too, and the two drive the kernel differently. Reaching a daemon's own figures would need D-Bus or chrony's command
port, both ruled out in 0111.

## Options considered

1. **"Synchronized" from the kernel's `STA_UNSYNC` flag** — chrony 4.2 on Linux keeps it set unless `rtcsync` is configured
   (read in chrony's `sys_timex.c`), so a synchronized chrony host would read as unsynchronized.
2. **"Synchronized" as the maximum error below 16 seconds** (chosen) — the rule `timedatectl` itself uses (systemd v249
   `timedated.c`, `ntp_synced`), which both systemd-timesyncd and chrony keep up to date.
3. **The daemon's measured offset** — only through D-Bus or chrony's port, ruled out in 0111.
4. **The kernel's PLL offset always** — chrony switches the PLL off and resets its offset to 0 (chrony 4.2
   `initialise_timex`), so the value would be a false zero.
5. **The kernel's PLL offset only while the PLL is on** (chosen) — systemd-timesyncd hands every slewed offset to the PLL
   (v249 `manager_adjust_clock`); under chrony the offset is omitted and the error estimates remain.

## Decision

The agent records the clock as synchronized when the kernel's maximum error is below 16 seconds, and records the kernel PLL's
offset only while the PLL is on, never as a zero. The rules are in [Agent system metrics](../areas/agent-system-metrics.md)
(*System metrics*, the `clock` sample).

## Consequences

- Under chrony there is no offset value, only the error estimates.
- The PLL offset is recorded with the sign the kernel reports for the correction still to be applied; what that sign means
  relative to the reference clock is not measured (no time daemon on the development host) and is not interpreted here. The
  batch header's `clock_offset_ns` (0046) stays the spool's and the sender's.
- Revisit if the clock offset under chrony is needed (then a reader of chrony's protocol needs its own record, against 0006).
