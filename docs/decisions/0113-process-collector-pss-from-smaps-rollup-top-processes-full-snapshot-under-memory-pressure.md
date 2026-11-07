# 0113: Process collector: PSS from smaps_rollup, top processes by memory and CPU with every program summed, a full snapshot under memory pressure

- **Status:** Accepted
- **Date:** 2026-10-17
- **Area:** Agent system metrics
- **Source:** Issue #32
- **Supersedes:** —

## Context

Issue #32 asks the agent to replace the legacy `top` script for processes: read `/proc/<pid>/stat`, `status`, `cmdline` and
`smaps_rollup`, keep the top processes by memory and by CPU with command line, user and start time, sum every program
(PSS, RSS, count), take a full snapshot under memory pressure, and document the capability that reading `smaps_rollup` of other
users' processes needs. The legacy log showed that summed RSS overstates a program such as Apache, whose children share
pages; PSS divides every shared page among its sharers. The output must be the model the legacy log parser already writes
(`process_snapshot`), so that the incident and report code reads both alike. The server has about 2 GB of RAM (0025), so the
collector must stay small under exactly the condition it is meant to record, and process names and command lines are text any
local user chooses. Who switches the memory pressure mode is decided in
[0141](0141-memory-pressure-mode-switched-by-the-system-collector-with-fixed-hysteretic-thresholds.md), how processes are named
in [0142](0142-program-names-follow-the-legacy-derivation-after-literal-grouping-rules.md).

## Options considered

1. **Memory source.** `status` (`VmRSS`, `RssAnon`, `RssFile`) — cheap, but no PSS; `smaps` — PSS per mapping, many
   kilobytes per process to read and parse; **`smaps_rollup`** (chosen, as the issue asks) — one summed block per process, so
   RSS and PSS come from the same block and are comparable.
2. **What a snapshot holds.** Every process every minute — about five times the volume for the spool and the record line
   limit; only the top processes — the totals per program would be lost; **the top processes by memory and by CPU, with every
   program summed over every process read, and every process while memory pressure mode is on** (chosen).
3. **Ranking key when PSS is partly missing.** PSS where present, else RSS — compares PSS of some processes with RSS of
   others, and RSS is the larger for shared pages; **one key per run, PSS only when every process has one** (chosen), as
   `top` ranks without the capability.
4. **User names.** `os/user` — behaves differently with and without cgo (NSS, possibly a network lookup) and cannot be
   replaced in tests; the numeric ID only — the legacy model holds names; **a bounded reader of `/etc/passwd` through the same
   replaceable file system as `/proc`** (chosen).
5. **CPU of a process without a previous sample.** Leave it out — the model requires `cpu_percent`; 0 — a false value;
   making the field optional — a wire format change on both sides for one case; **the average since the process started, as
   `ps` shows it** (chosen), which for a process that started within the interval is the exact value.
6. **Size of the record.** Encode the whole snapshot and shorten it until it fits — a hostile snapshot can encode to tens of
   megabytes first; **fill the record entry by entry within a byte budget, programs before processes** (chosen), then check
   the whole record once as the producer contract (0044) requires.
7. **A read that blocks** (a process whose memory map lock is held can block reads of its `cmdline` and `smaps_rollup`). One
   goroutine and deadline per process — more goroutines and memory than the collector itself; **the frame abandons the run at
   its timeout and records a gap** (chosen, 0029), accepting that a process stuck that way costs process samples until it
   moves. Any local user can cause it on purpose by holding the memory map lock of a process of their own; this residual is
   deliberate.
8. **Text a user chooses that fails a check.** Fail the sample — any local user could suppress process data; **every field
   the model checks is valid by construction, and an entry that still fails its check alone is left out with the snapshot
   marked incomplete** (chosen).

## Decision

A collector reads every process from `/proc` (within a fixed cap), sums every program and records the top processes by memory
and CPU, or every process while memory pressure mode is on, with PSS from `smaps_rollup`; reading `smaps_rollup` of other
users' processes needs `CAP_SYS_PTRACE` and nothing else (measured on the development host: `CAP_DAC_READ_SEARCH` does not
replace it; `stat`, `status` and `cmdline` need no capability). The rules are in
[Agent system metrics](../areas/agent-system-metrics.md) (*Processes*) and [Agent rights](../areas/agent-rights.md).

## Consequences

- PSS appears beside RSS for every program; the backend still ranks programs by RSS; the processes view is where PSS is shown.
- A PSS that cannot be read (no capability, another user namespace, an LSM) is left out, never 0; RSS stays.
- Under memory pressure the process snapshot lists every process at the shorter interval, so the spool grows faster for the
  length of the episode; the growth is bounded by the record budget per snapshot and by the spool's size bound, which then
  evicts older data with a recorded gap.
- A local user who holds a process's memory map lock can make a run hang; the run becomes a gap and process samples are lost
  until the process moves (option 7).
- The speed bound for 250 processes is not measured on the server (Ubuntu 22.04, kernel 5.15); the fixtures are captures of
  the development host's kernel 6.18. The parallel run of #45 and the footprint test of #44 check it.
