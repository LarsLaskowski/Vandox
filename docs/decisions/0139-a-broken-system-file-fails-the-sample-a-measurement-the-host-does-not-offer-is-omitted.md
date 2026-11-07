# 0139: A system file that cannot be read fails the whole sample; a measurement the host does not offer is omitted and logged once

- **Status:** Accepted
- **Date:** 2026-10-17
- **Area:** Agent system metrics
- **Source:** Issue #31
- **Supersedes:** —

## Context

Issue #31 asks that missing pressure stall information (PSI) be "handled gracefully and reported once". The frame of 0110
records a missed sample as a gap, but it cannot record a partial loss: a sample either succeeds or becomes a gap (0028). Some
measurements are not missing by accident but because the host does not offer them: a kernel without PSI (or booted with
`psi=0`), a pressure file without a `full` line, a configured mount point that is not mounted, the PLL offset under chrony
([0138](0138-clock-synchronized-below-16-seconds-of-maximum-error-pll-offset-only-while-the-pll-is-on.md)). The backend reads
an omitted measurement as unknown.

## Options considered

1. **Failure inside the `system` sample: partial records with the failed part left out** — the frame cannot record a partial
   loss, so the missing part would look normal (0028).
2. **A file that cannot be read or breaks its grammar fails the whole sample, which becomes a gap** (chosen); the only
   exceptions are the absences the kernel defines.
3. **Missing PSI fails the sample** — memory data lost on every PSI-less kernel.
4. **Missing PSI as a gap per sample** — a gap means a missed sample, and the log would repeat.
5. **Missing PSI omitted, one warning when the set of unavailable resources changes** (chosen); the backend reads the absence
   as unknown, as it does for every omitted measurement.
6. **Whether a configured path is mounted: `statfs` on the path alone** — an unmounted mount point silently reports the file
   system below it; comparing the device of the path with its parent's — wrong for bind mounts; the host's namespace through
   `/proc/1/root` — would also see paths the unit hides, but needs `CAP_SYS_PTRACE` for a collector that has no other use for
   it; **the mount table of the agent's own mount namespace** (chosen): a path that is not a mount point there is reported once
   and its values are omitted.

## Decision

A file of the system collectors that cannot be read or breaks its grammar fails the whole sample as a gap, while a measurement
the host does not offer (PSI, a configured path that is not mounted, the PLL offset under chrony) is omitted, logged once when
its availability changes and never recorded as zero or as a gap. The rules are in
[Agent system metrics](../areas/agent-system-metrics.md) (*Absent values and errors*).

## Consequences

- One unreadable file costs the whole `system` sample, PSI included; that is the price of never letting a partial loss look
  normal.
- A mount point unmounted between the mount table read and `statfs` reports the file system below it for one sample.
- Readers of the metrics must treat a missing value as unknown, never as 0.
