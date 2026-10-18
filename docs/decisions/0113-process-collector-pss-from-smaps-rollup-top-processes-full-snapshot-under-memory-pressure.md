# 0113: Process collector: PSS from smaps_rollup, top processes by memory and CPU with every program summed, a full snapshot under memory pressure that the system collector switches

- **Status:** Accepted
- **Date:** 2026-10-17
- **Area:** Agent
- **Source:** Issue #32
- **Supersedes:** —

## Context

Issue #32 asks the agent to replace the legacy `top` script for processes: read `/proc/<pid>/stat`, `status`, `cmdline` and
`smaps_rollup`, keep the top processes by memory and by CPU with command line, user and start time, sum every program
(PSS, RSS, count) under configurable grouping rules, switch to a shorter interval and a full snapshot when pressure stall
information or `MemAvailable` crosses a threshold, and document the capability that reading `smaps_rollup` of other
users' processes needs. The legacy log showed that summed RSS overstates a program such as Apache, whose children share
pages; PSS divides every shared page among its sharers. The output must be the model the legacy log parser already writes
(`process_snapshot`, [log import](../areas/log-import.md), *Legacy monitoring log*), so that the incident and report code reads
both alike. The runtime frame (0110) offers a memory pressure mode but leaves its thresholds to #31 and #32; #31 shipped none,
so nothing switches the mode yet. The server has about 2 GB of RAM (0025), so the collector must stay small under exactly the
condition it is meant to record, and process names and command lines are text any local user chooses.

## Options considered

1. **Memory source.** `status` (`VmRSS`, `RssAnon`, `RssFile`) — cheap, but no PSS; `smaps` — PSS per mapping, many
   kilobytes per process to read and parse; **`smaps_rollup`** (chosen, as the issue asks) — one summed block per process.
   RSS comes from the same block as PSS when it is readable, so the two are comparable, and from `status` otherwise.
2. **What a snapshot holds.** Every process every minute — about five times the volume for the spool and the record line
   limit of 1 MiB; only the top processes — the totals per program would be lost; **the top 25 by memory and the top 25 by
   CPU, with every program summed over every process read, and every process while memory pressure mode is on** (chosen).
3. **Where the pressure thresholds live.** A separate pressure collector — a second reader of the same files and another
   runner; the process collector — it runs once a minute, too late; **the `system` collector, after each successful sample
   it already takes of `meminfo` and the pressure files** (chosen), which reports a change to the scheduler.
4. **Thresholds.** Configurable options — more configuration surface before any value has been tried on the server;
   **fixed values with hysteresis** (chosen): on when `MemAvailable` is below 10 % of `MemTotal` or the memory `some avg10`
   reaches 10 %, off only when `MemAvailable` is at least 15 % and `some avg10` is below 5 %. They become options once the
   parallel run of #45 shows a reason.
5. **Program names.** A new naming scheme — the agent's and the legacy parser's names for the same process would differ;
   **the legacy parser's derivation (name from the command line, program from its first word), pinned by a fixture both
   languages read, with configurable rules applied first** (chosen). The default rule list is empty, so the names match the
   legacy log until an operator adds a rule (for example one program per PHP-FPM pool).
6. **Rule language.** Regular expressions — more surface in the configuration and in review for the examples of the issue,
   which all fit a literal match; **an exact command or a literal command-line prefix, optionally with the next word appended**
   (chosen).
7. **User names.** `os/user` — behaves differently with and without cgo (NSS, possibly a network lookup) and cannot be
   replaced in tests; the numeric ID only — the legacy model holds names; **a bounded reader of `/etc/passwd` through the same
   replaceable file system as `/proc`** (chosen), the number where no entry exists.
8. **CPU of a process without a previous sample.** Leave it out — the model requires `cpu_percent`; 0 — a false value;
   making the field optional — a wire format change on both sides for one case; **the average since the process started, as
   `ps` shows it** (chosen), which for a process that started within the interval is the exact value.
9. **Size of the record.** Encode the whole snapshot and shorten it until it fits — a hostile snapshot can encode to tens of
   megabytes first; **fill the record entry by entry within a byte budget, programs before processes** (chosen), then check
   the whole record once as the producer contract (0044) requires.
10. **A read that blocks** (a process whose memory map lock is held can block reads of its `cmdline` and `smaps_rollup`). One
    goroutine and deadline per process — more goroutines and memory than the collector itself; **the frame abandons the run at
    its timeout and records a gap** (chosen, 0029), accepting that a process stuck that way costs process samples until it moves.
    Any local user can cause it on purpose by holding the memory map lock of a process of their own; this residual is
    deliberate.
11. **Configuration list of rules.** A compact string syntax per rule — a second grammar to parse and to document; **a list of
    mappings checked by the strict decoder like every section** (chosen, 0049): unknown and duplicate keys in an element are
    refused with the element's index in the key path.
12. **A process without any name** (empty command line and empty `comm`, which any local user can produce). Leave it out —
    the model requires a non-empty command and program, but leaving the process out would let a local user hide its memory
    from the program sums by blanking its name; **a fixed placeholder `(unnamed)` as the last fallback** (chosen), which holds
    no process text. More generally, no text a user chooses may fail the whole sample: every field the model checks is valid
    by construction, and an entry that still fails its check alone is left out with the snapshot marked incomplete.
13. **Ranking key when PSS is partly missing.** PSS where present, else RSS — compares PSS of some processes with RSS of
    others, and RSS is the larger for shared pages; **one key per run: PSS when every process (program) has one, else RSS for
    all** (chosen), as `top` ranks without the capability.

## Decision

A new collector reads every process (at most 4,096 per run) from `/proc`, sums every program and records the top processes by
memory and CPU, or every process while memory pressure mode is on; the `system` collector switches that mode with fixed,
hysteretic thresholds on `MemAvailable` and memory PSI. Names follow the legacy parser's rules after the configured grouping rules,
and reading `smaps_rollup` of other users' processes needs `CAP_SYS_PTRACE` and nothing else (measured on the development host:
`CAP_DAC_READ_SEARCH` does not replace it; `stat`, `status` and `cmdline` need no capability). The rules are in the
[Agent](../areas/agent.md) area (*Processes*, *Memory pressure mode*, *Rights*) and the option in
[Configuration and secrets](../areas/configuration-and-secrets.md).

## Consequences

- PSS appears beside RSS for every program; the backend still ranks programs by RSS (`MemoryAnalysis`); the processes view
  (#50) is where PSS is shown.
- A PSS that cannot be read (no capability, another user namespace, an LSM) leaves that process's PSS and its program's PSS sum
  out, never 0; RSS stays.
- Under memory pressure the process snapshot is taken every 15 seconds and lists every process, so the spool grows faster for
  the length of the episode (#38, #44 measure it). Any local user can switch the mode on by allocating memory; the growth is
  bounded by the record budget per snapshot and by the spool's size bound, which then evicts older data with a recorded gap.
- A local user who holds a process's memory map lock can make a run hang; the run becomes a gap and process samples are lost
  until the process moves (option 10).
- The thresholds and the 100 ms bound for 250 processes are not measured on the server (Ubuntu 22.04, kernel 5.15); the
  fixtures are captures of the development host's kernel 6.18. The parallel run of #45 and the footprint test of #44 check them.
- A grouping rule with `append_next_word` takes the word from text any local user controls, so it can create programs; their
  number is bounded by the record budget like every other program name.
