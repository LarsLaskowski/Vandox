# Plan: Process collector (replaces top)

Source: Issue #32
Status: Draft
Tier: security — the change adds readers of `/proc` files whose content any local user chooses (process names and command
lines: *Security areas* 10 and 12 of `.squad/project.md`), extends the strict configuration decoder (area 10) and documents the
capability the agent needs for it (area 6).

## Problem / root cause

Feature. The agent has no process collector yet; the backend reads process snapshots only from the imported legacy `top`
log. Issue #32 asks for a collector that reads `/proc/<pid>/stat`, `status`, `cmdline` and `smaps_rollup`, keeps the top
processes by memory and by CPU with full command line, user and start time, sums every program (PSS, RSS, count) under
configurable grouping rules, switches to a shorter interval and a full snapshot when PSI or `MemAvailable` crosses a threshold,
and documents the minimal capability for `smaps_rollup` of other users' processes.

Claims of the issue, checked:

- *RES sums from `top` overstate program totals because shared pages are counted per process; PSS from `smaps_rollup` gives
  correct totals* — **confirmed** as a definition (RSS counts a shared page fully in every process that maps it, PSS divides it
  among them); the legacy parser already marks every program with more than one process `rss_overcounted`
  (`src/Vandox.Core/LogParsing/LegacyProcessTable.cs:144`). "Correct" holds for the memory processes map; kernel memory and
  unmapped page cache are in neither sum.
- *Same output model as the legacy log parser* — the model exists on both sides: `internal/model/process.go` (`ProcessSnapshot`,
  `ProcessSample`, `ProgramAggregate`, with `pss_bytes` optional on process and program) and the C# twin; the legacy parser's
  naming rules are in `src/Vandox.Core/LogParsing/LegacyProcessNames.cs` (`CommandOf`, `ProgramOf`, `DeriveName`). No wire
  change is needed. **Confirmed.**
- *Adaptive mode* — the scheduler offers it (`internal/scheduler/scheduler.go:123` `SetPressure`, `Run.Pressure`), but **nothing
  calls `SetPressure`** (grep over `internal/` and `cmd/`): record 0110 left the thresholds "with #31 and #32", and #31 shipped
  none. This plan adds them.
- *Minimal capability for `smaps_rollup`* — record 0030 already names `CAP_SYS_PTRACE` for it (the procfs ptrace access check
  on open). Measured on the development host (kernel 6.18): reading `smaps_rollup` of a kernel thread or a zombie answers
  `ESRCH`, and even the sandbox's root, which holds `CAP_SYS_PTRACE`, gets `EACCES` for pid 1 (another user namespace), so the
  read can be refused with the capability too. Whether `CAP_SYS_PTRACE` alone suffices and `CAP_DAC_READ_SEARCH` does not help
  is *unverified* until the Dev's measurement (*Security considerations*).
- *Dependency #30* — merged (`log.md`).

Formats checked on real samples of the development host (kernel 6.18, see *Real samples*): `stat` carries `comm` raw, so a
name may hold `)`, `(`, spaces and a **line feed** (measured: `prctl(PR_SET_NAME, "a\nUid:\t9\t9")` gives
`1654 (a\nUid:\t9\t9) R …` with a real line feed), which rules out reading only the first line; `status` escapes the line feed
of `Name:` (`Name:\ta\\nUid:…`), so no name can start a line there; a `setproctitle` process (`perl` `$0`, as `spamd` and
`amavisd` do) has a `cmdline` of one string ending in one NUL; a workqueue kernel thread's `comm` is longer than 15 bytes
(`kworker/u16:0-kvfree_rcu_reclaim`) and its state can be `I`.

**Related defect found on the way** (not fixed here, follow-up issue below): the Go model measures text fields in bytes of the
Go string (`internal/model/model.go:218-220`, `checkShort`/`checkText` call `checkLen` on `len(s)`), while the encoder
(`json.Marshal` in `internal/wire/encode.go:25`) replaces every invalid UTF-8 byte with U+FFFD (3 bytes) and the backend
validates the decoded length. A `command` of 1,024 invalid bytes passes `wire.CheckRecord` and is 3,072 bytes in the backend,
which refuses it — and a batch is valid only as a whole (0044). This collector guards itself (AC9); every other producer of
external text (log shipping #37) needs the model fixed.

## Acceptance criteria

Names: the collector is `processes` (`internal/procstats`), records carry the source `processes`.

- [ ] **AC1 PSS per program beside RSS.** Every program has `count`, `cpu_percent` (sum), `rss_bytes` (saturating sum),
  `pss_bytes` (saturating sum, present only when **every** member has a PSS) and `rss_overcounted` (exactly when `count` > 1).
  The programs are summed over **every process read**, not only the listed ones, so the counts add up to `total`. Programs are
  ordered by their memory key (PSS sum when present, else RSS sum) descending, then by name (ordinal).
  Tests: `TestProcesses_ProgramSumsPSSAndRSS` (two real captures of one program: exact RSS and PSS sums, RSS sum above PSS sum),
  `TestProcesses_ProgramPSSOmittedWhenAMemberHasNone`, `TestProcesses_ProgramsCoverUnlistedProcesses` (more than 50 processes
  without pressure: the sum of `count` equals `total`).
- [ ] **AC2 Same output model as the legacy parser.** One `process_snapshot` record per run, `captured_at` the run's start.
  Per process: `pid`, `ppid` (from `stat`), `user` (the name of the **effective** UID from `/etc/passwd`, else the UID in
  decimal), `command`, `cmdline`, `truncated`, `state` (the `stat` state when it is one ASCII letter, else left out),
  `started_at` (boot time plus `starttime` / 100 s, UTC), `cpu_percent`, `rss_bytes`, `pss_bytes`, `swap_bytes` (`VmSwap`,
  left out when the line is absent); `oom_score_adj` is not read. `cmdline`: trailing NUL bytes removed, every other NUL a
  space. `command`: the legacy derivation — the text inside `[...]` when the whole command line is bracketed, else the part
  after the last `/` of the first space-separated word with one trailing `:` removed; when that is empty, the command line;
  when that is empty too, `comm`. Default program: the first space-separated word of `command`, `command` itself when that word
  is empty. Both derivations are pinned by one shared fixture `testdata/processes/names.json` (cases: `cmdline`, `fallback`,
  `command`, `program`) read by both languages.
  Tests: `TestProcesses_SampleFieldsFromFixtures` (every field above, from the real captures, exact values),
  `TestCommandOf_SharedCases` and `TestProgramOf_SharedCases` (Go), `LegacyProcessNamesSharedCasesMatchAgent` (C#, in the
  existing `LegacyProcessNamesTests`: `CommandOf(cmdline, fallback)` and `ProgramOf(command)` give the fixture's values). The
  fixture holds at least: `/usr/sbin/apache2 -k start` → `apache2`/`apache2`; `php-fpm: pool example.com` → `php-fpm`;
  `spamd child` → `spamd`; `amavisd (master)` → `amavisd`; `[kthreadd]` → `kthreadd`; empty with fallback
  `kworker/u16:0-kvfree_rcu_reclaim` → that fallback; `/` → `/` (derived name empty); `/usr/bin/` → `/usr/bin/`; `a:` → `a`;
  a name with a space inside brackets `[a b]` → command `a b`, program `a`.
- [ ] **AC3 Grouping rules.** Rules are tried in configuration order; the first match gives the program. A rule matches by
  exactly one of `command` (equal to the derived command, ordinal) or `cmdline_prefix` (a literal, ordinal prefix of the
  command line). With `append_next_word`, the program is `<program>/<word>`, where word is the text after the prefix up to
  the next space, cut so the program is at most 1,024 bytes at a character boundary; an empty word gives `<program>`. No match:
  the default program (AC2). Default rule list: empty.
  Tests: `TestProgramFor_Rules` (command rule, prefix rule, appended word, empty word, first rule wins, no match → default,
  a word cut at a character boundary, a prefix that equals the whole command line).
- [ ] **AC4 Top processes and full snapshot.** Without pressure mode the list holds the union of the top 25 by memory key (PSS
  when present, else RSS) and the top 25 by `cpu_percent`, ties by PID ascending, ordered by memory key descending then PID;
  `complete` is false whenever a process was left out. With `run.Pressure` true every process read is listed (same order);
  `complete` is true only when every process was read and listed and no reduction for size happened. `total` is the number of
  processes seen (PID directories listed, minus those that ended during the read).
  Tests: `TestProcesses_TopProcessesWithoutPressure`, `TestProcesses_FullSnapshotUnderPressure`.
- [ ] **AC5 CPU.** `cpu_percent` of a process with a predecessor (same PID and same `starttime` in the previous successful run
  of the collector, start times moving forward) = (utime + stime − previous) / 100 / seconds between the two runs' starts × 100;
  without one (first run, new PID, reused PID, counters that fell, start time not later) the average since the process
  started: (utime + stime) / 100 / seconds from `started_at` to the run's start × 100, and 0 when that age is not positive.
  Clock ticks are the fixed 100 per second (`USER_HZ` of the Linux ABI on x86-64 and arm64). A failed run is no predecessor.
  Test: `TestProcesses_CPUPercent` (table: first run, second run, reused PID, counters fell, clock set back).
- [ ] **AC6 Memory pressure mode.** After each successful `system` sample the `system` collector computes the mode:
  **on** when 10 × `MemAvailable` < `MemTotal` or the memory `some avg10` ≥ 10.00; **off** only when 20 × `MemAvailable` ≥
  3 × `MemTotal` (15 %) and (`some avg10` < 5.00 or the memory pressure file is unavailable); otherwise it keeps the mode. The
  products are exact (no overflow, no float). It calls the switch only when the mode changes (it starts off), and logs one info
  line `memory pressure mode on` / `memory pressure mode off` with `collector=system`, `mem_available_bytes` and, when present,
  `memory_some_avg10`. A failed sample changes nothing. The agent connects the switch to `Scheduler.SetPressure`. The
  `processes` job: interval 60 s, pressure interval 15 s, timeout 10 s, placed after `system` in the job list.
  Tests: `TestNextPressure` (table at the exact boundaries: 10 % on and not on, `avg10` 10.00 and 9.99, the band between 10 % and
  15 % keeps either mode, `avg10` 5.00 keeps on, PSI unavailable), `TestSystem_SwitchesPressureOnlyOnChange` (scripted meminfo
  and pressure files across samples: calls and log lines), `TestJobs_Schedules` (expected list gains `processes`),
  `TestNewScheduler_LowMemoryMakesFullProcessSnapshots` (agent: `newScheduler` with fixture sources whose `meminfo` is below
  10 %, run in a `testing/synctest` bubble: a later `processes` record lists every fixture process with `complete` true).
- [ ] **AC7 Readers of `/proc` and `/etc` (security area 10).** Every read is bounded before parsing; every number is 1 to 20
  ASCII digits (UIDs and PIDs fewer, below) parsed into its target width; nothing else is accepted.
  - `stat`: at most 4 KiB, read whole (never only the first line). The PID, a space, `(`, then `comm` up to the **last** `)` of
    the file, a space, then at least 20 single-space-separated fields; state is field 3, `ppid` 4, `flags` 9, `utime` 14,
    `stime` 15, `starttime` 22. The PID must equal the directory name; `ppid` and PID are parsed as 32-bit signed and must be
    ≥ 0 and > 0; `flags` as 32-bit unsigned. `comm` may be empty and may hold any byte but NUL.
  - `status`: at most 16 KiB; only lines starting with `Uid:`, `VmRSS:` and `VmSwap:` are read, each at most once (a second one
    fails the process). `Uid:` is four tab-separated decimal values within 32 bits, the second the effective UID. `VmRSS:` and
    `VmSwap:` are spaces or tabs, 1 to 20 digits, ` kB`, and the value times 1,024 within 64 bits. `VmRSS` absent is allowed only
    for a process without address space (below).
  - `smaps_rollup`: at most 4 KiB; the first line (the `[rollup]` range) is not read; of the rest only `Rss:` and `Pss:` are
    read, each exactly once, as name, colon, spaces, 1 to 20 digits, ` kB`, times 1,024 within 64 bits; other lines (`Pss_Dirty`
    of 6.x is absent in 5.15) are ignored.
  - `cmdline`: at most 4 KiB read; then trailing NULs removed, NULs to spaces, every byte that does not start a valid UTF-8
    sequence replaced by U+FFFD, then cut to 1,024 bytes at a character boundary; `truncated` when more than 4 KiB were there
    or the text was cut. `comm` is made valid UTF-8 the same way. `command` and the program are at most 1,024 bytes after this.
  - `/proc/stat`: `btime` read line by line, at most 1 MiB in total; a line longer than 64 KiB (the `intr` line of a large host)
    is skipped to its end without being held; the line `btime`, one space, 1 to 20 digits must occur exactly once.
  - `/etc/passwd`: at most 1 MiB, lines at most 4 KiB; a line is `name:password:uid:…`; a line with fewer than 3 fields, an
    empty name or a UID that is not 1 to 10 digits within 32 bits is skipped; the first line of a UID wins; the name is made
    valid UTF-8 and cut to 256 bytes. Read once per run.
  - At most 4,096 PIDs are read per run, the lowest numbers first (directory names of 1 to 10 digits, parsed as 32-bit, > 0);
    PIDs beyond that are counted in `total` and make `complete` false.
  Tests: `TestParseStat`, `TestParseStatus`, `TestParseSmapsRollup`, `TestParseCmdline`, `TestReadBootTime`, `TestReadPasswd`
  (each a table that starts from the real captures and then covers: for `stat` a `comm` holding `)`, `) (`, a line feed, fake
  fields such as `x) R 1 1 1`, the empty `comm` `()`, 19 fields, a non-digit and a 21-digit field, a PID that differs from the
  directory, 4 KiB + 1; for `status` the captured line-feed name (`Name:\ta\\nUid:\t9\t9` must not be read as `Uid:`), a second
  `Uid:`, a `VmRSS` whose times 1,024 overflows, a kernel thread without `VmRSS`; for `smaps_rollup` the 6.18 capture, the 5.15
  shape without `Pss_Dirty`, missing `Pss:`, a second `Rss:`, an overflow; for `cmdline` the NUL-separated capture, the
  `setproctitle` capture, trailing NULs only, empty, 4 KiB + 1, 1,024 invalid bytes, a multi-byte character across byte 1,024, an
  embedded line feed kept; for `btime` missing, twice, a 70 KiB line before it; for `passwd` the capture, a duplicate UID, a
  short line, a UID of 11 digits, a 4 KiB + 1 line, 1 MiB + 1).
- [ ] **AC8 Per-process outcomes and errors.**
  - A process whose files disappear (`ENOENT`) or whose read answers `ESRCH` has ended: left out, not counted, `complete`
    unchanged. Exception: `ESRCH` on `smaps_rollup` of a **kernel thread** (`flags` has `PF_KTHREAD`, 0x00200000) or a **zombie**
    (state `Z`) means no address space: `rss_bytes` 0, `pss_bytes` 0.
  - `EACCES` or `EPERM` on `smaps_rollup`: `pss_bytes` left out, `rss_bytes` from `VmRSS`.
  - Any other read or parse error of one process: that process is left out and `complete` is false; the sample goes on.
  - The sample fails (a gap, scheduler) only when `/proc` cannot be listed, `btime` cannot be read, or no process was read.
  - `passwd` that cannot be read: every user is the decimal UID; the sample goes on.
  - Error texts name the file relative to `/proc` or `/etc` (`1234/stat`, `stat`, `passwd`) and a fixed reason, never file
    content, a `comm`, a command line or a user name.
  - Log lines (through the collector's logger, attribute `collector=processes`), each once per change: warning
    `process memory partly unknown` (`processes`: the number without PSS) when that number becomes positive, info
    `process memory known again` when it returns to 0; warning `processes not read` (`processes`: the count, `err`: the first
    error) when it becomes positive, info `processes read again`; warning `user names unavailable` (`err`) and info
    `user names available`. No log line holds process text.
  Tests: `TestProcesses_ReadOutcomes` (table over a wrapper that answers a given error per file: ended, kernel thread, zombie,
  `EACCES`, `EIO`, listing fails, `btime` missing, no process), `TestProcesses_LogsTransitionsOnce`,
  `TestProcesses_ErrorsAndLogsHoldNoProcessText` (a `comm`, command line and user name holding a sentinel, made malformed:
  neither the error nor any log line contains it).
- [ ] **AC9 The record always fits and is valid on both sides.** The collector fills the record entry by entry within a payload
  budget of 1 MiB − 8 KiB of JSON (each entry measured by encoding it alone): programs first, at most half of the budget, in
  their order; then processes in their order until the budget is used. A left-out entry makes `complete` false. The finished
  record is checked with `wire.CheckRecord`; on a size error the processes and then the programs are halved until it passes
  (at most 13 rounds each); any other error fails the sample. Every text is valid UTF-8 before it is cut (AC7), so the encoder
  replaces nothing.
  Tests: `TestProcesses_LargeSnapshotFitsTheLineLimit` (4,096 processes in an `fstest.MapFS`, each with a distinct 1,024-byte
  command line of control characters, pressure on: `wire.CheckRecord` passes, `complete` false, every program kept before any
  process), `TestProcesses_InvalidUTF8StaysWithinLimits` (1,024 invalid bytes: the record encoded by `wire.EncodeBatch` and
  decoded by `wire.NewDecoder` passes the model's validation, `command` and `cmdline` at most 1,024 bytes each),
  `TestProcesses_ReadsAtMostMaxProcesses` (4,100 PID directories: 4,096 read, `total` 4,100, `complete` false).
- [ ] **AC10 Hanging source and bounded memory (0029, area 10).** A read that blocks is abandoned by the scheduler at the
  timeout with a gap, and the collector is not started again while it hangs. Reading a command line holds at most 4 KiB per
  process. The package imports no program, socket, cgo or `unsafe` and makes no raw system call.
  Tests: `TestProcesses_BlockingReadIsAbandonedAsGap` (scheduler with the collector over a blocking file system in a
  `testing/synctest` bubble), `TestProcesses_ReadsBoundedCmdline` (64 processes with 1 MiB command lines: bytes allocated by
  `Collect` below 64 × 64 KiB, measured with `runtime.MemStats`), `TestProcstats_ImportsOnlyAllowedPackages` (an allow-list
  test like `TestSysstats_ImportsOnlyAllowedPackages`; `os/user` is not allowed).
- [ ] **AC11 Configuration `processes.programs`.** A list of at most 64 rules, each a mapping with `program` (required, the wire
  name pattern, 1 to 128 bytes), exactly one of `command` and `cmdline_prefix` (1 to 256 bytes, no Cc, Cf, Zl, Zp), and
  `append_next_word` (boolean, default false, only with `cmdline_prefix`). Default `[]`. The strict decoder walks a list of
  mappings like a section: an unknown or duplicate key, an anchor, alias or custom tag inside an element, a null or non-mapping
  element are refused with the element's line and the key path `processes.programs[<index>]` (and `.<key>`). Exact texts
  (`config: <file>:<line>: <key>: <reason>`):
  - more than 64: key `processes.programs`, reason `must be a list of at most 64 rules`;
  - a bad `program`: key `processes.programs[<i>].program`, reason
    `must be 1 to 128 characters of [A-Za-z0-9._:/@+-], starting with a letter or digit`;
  - neither or both of `command` and `cmdline_prefix`: key `processes.programs[<i>]`, reason
    `must set exactly one of command and cmdline_prefix`;
  - a `command` or `cmdline_prefix` of more than 256 bytes: key `processes.programs[<i>].command` (or `.cmdline_prefix`),
    reason `must be 1 to 256 bytes`;
  - `append_next_word` without `cmdline_prefix`: key `processes.programs[<i>].append_next_word`, reason `needs cmdline_prefix`;
  - a null element: key `processes.programs[<i>]`, reason `has no value`; an element that is not a mapping: key
    `processes.programs[<i>]`, reason `must be a section (a mapping of keys)`.
  No error text contains a rule's value.
  Tests: `TestLoadAgent_ProcessesPrograms` (valid rules, default empty), `TestLoadAgent_ProcessesProgramsErrors` (every text
  above, compared exactly), `TestDecodeStrict_ListOfSections` (unknown key in an element, duplicate key, null and scalar
  element, anchor inside an element), `TestLoadAgent_Example` and the `AgentKeys` test (existing, expectations extended).
- [ ] **AC12 Capability documented.** The Agent area (*Rights*) and `deploy/agent/README.md` state: `stat`, `status` and
  `cmdline` of every process are readable by any user (unless `/proc` is mounted with `hidepid`), `smaps_rollup` of another
  user's process needs `CAP_SYS_PTRACE` and `CAP_DAC_READ_SEARCH` does not replace it; without it the collector records RSS
  and leaves PSS out. Verified by the Dev's measurement (*Security considerations*) and the Reviewer's reading.
- [ ] **AC13 Speed.** A snapshot of 250 processes takes under 100 ms. Verified by a benchmark, not a unit test (*Verification
  of AC13 speed* below).

## Verification of AC13 speed

Unit tests do not measure absolute time (`docs/UNIT_TESTS.md`). The Tester writes `BenchmarkProcesses_Collect` in
`internal/procstats/procstats_test.go` (the default sources, i.e. the host's `/proc`, one `Collect` per iteration with
pressure on). The **Dev** runs it once on the development host with at least 250 processes (the procedure starts sleeping
processes in a scratch shell until `/proc` lists 250, runs `go test ./internal/procstats -run '^$' -bench Collect -count 10`,
then stops them) and records host, kernel, process count and the median in a new section *Process snapshot* of
`docs/BENCHMARKS.md`, before the review. A median of 100 ms or more is a plan deviation for the Lead. The value on the server
(kernel 5.15, 2 GB) is measured by #44; the area document says so.

## Behavior coverage

No `spec.md` (issue work). Requirements of the issue mapped:

| Requirement | Criterion |
| ----------- | --------- |
| Read `stat`, `status`, `cmdline`, `smaps_rollup` (RSS and PSS) | AC2, AC7, AC8 |
| Top N by memory and by CPU with command line, user, start time | AC2, AC4 |
| Aggregate per program with configurable rules: PSS sum, RSS sum, count | AC1, AC3, AC11 |
| Adaptive mode: shorter interval and full snapshot on PSI or `MemAvailable` | AC4, AC6 |
| Document the minimal capability | AC12 |
| AC "PSS sums per program, difference to RSS visible" | AC1 (both sums in the record; shown by the processes view #50) |
| AC "Same output model as the legacy log parser" | AC2 |
| AC "Snapshot of 250 processes under 100 ms" | AC13 |
| AC "Adaptive mode tested" | AC6, AC4 |

## Approach

1. **`internal/procstats`** (new package, Go): `Sources` (`Proc`, `Etc` as `fs.FS`, defaults `os.DirFS("/proc")` and
   `os.DirFS("/etc")`), the collector `Processes` with `Collect`. One run: read `btime`, read `passwd`, list `/proc`, take the
   lowest 4,096 PIDs, read each process (`stat`, `status`, `cmdline`, `smaps_rollup`, in this order; a 4 KiB buffer for
   `cmdline`), derive command and program, keep per process only its numbers and its three texts (each ≤ 1,024 bytes); sum
   programs in a map; compute CPU against the previous run's map (PID → `starttime`, ticks), which replaces the old map after a
   successful run; select (AC4); fill the record within the budget (AC9); compare the warning states with the last run and
   log changes (AC8). The file layout below keeps each reader small and testable.
2. **`internal/sysstats`**: `NextPressure` (pure, AC6) in a new file; `System` gets the switch through `SetPressureSwitch` and
   evaluates after a successful sample, keeping its own `high` under its mutex; it reads `MemAvailable`, `MemTotal` from the
   parsed meminfo and the memory `some avg10` from the parsed pressure files.
3. **`internal/config`**: section `Processes` with `Programs []ProgramRule`; `decode.go` gets a walker for a slice of structs
   (each element through `walkMapping` with the path `<key>[<i>]`, lines recorded per element and key); `validate` checks the
   rules (AC11); `AgentKeys` adds `processes.programs` last.
4. **`internal/agent`**: `Options.Processes`; `jobs` adds the `processes` job after `system` with the rules converted from the
   configuration and returns the `system` collector as well; `newScheduler` calls `system.SetPressureSwitch(sched.SetPressure)`
   before returning.
5. Fixtures (Tester), docs (Dev), shared names fixture and its C# test (Tester).

## Options considered

See record 0113: memory source; what a snapshot holds (all, top only, **top plus full under pressure**); where the thresholds
live (separate collector, process collector, **system collector**); configurable thresholds versus **fixed with hysteresis**;
naming (**legacy derivation plus rules**, default rules empty versus a built-in PHP-FPM pool rule — rejected because the
default names would then differ from the legacy log, which AC "same output model" asks to match; the example file shows the
pool rule commented out); rule language (regular expressions versus **literal**); user names (`os/user`, numbers, **own
`passwd` reader**); CPU without predecessor (leave out, 0, optional field, **average since start**); record size (encode and
halve versus **budgeted fill**); a blocking read (per-process goroutine versus **frame abandonment**); the configuration
syntax (string grammar versus **list of mappings in the strict decoder**).

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Go | `internal/procstats/procstats.go` | new: package doc, constants, `Sources`, `Rule`, `Processes`, `New`, `Name`, `Collect`, warning states |
| Go | `internal/procstats/stat.go` | new: `stat` reader (AC7) |
| Go | `internal/procstats/status.go` | new: `status` reader |
| Go | `internal/procstats/smaps.go` | new: `smaps_rollup` reader |
| Go | `internal/procstats/cmdline.go` | new: `cmdline` reader and text cleaning (UTF-8, cut) |
| Go | `internal/procstats/boottime.go` | new: `btime` reader of `/proc/stat` |
| Go | `internal/procstats/passwd.go` | new: `/etc/passwd` reader |
| Go | `internal/procstats/names.go` | new: `commandOf`, `programOf`, `programFor` (rules) |
| Go | `internal/procstats/snapshot.go` | new: CPU, aggregation, selection, budgeted record |
| Go | `internal/sysstats/pressuremode.go` | new: thresholds, `PressureReading`, `NextPressure` |
| Go | `internal/sysstats/system.go` | `SetPressureSwitch`, evaluation after a successful sample, log lines |
| Go | `internal/config/agent.go` | `Processes`, `ProgramRule`, `MaxProgramRules`, `MaxRuleMatchBytes`, defaults, `AgentKeys`, `validate`, `checkProgramRules` |
| Go | `internal/config/decode.go` | list of mappings in `walkField` (new `walkList`) |
| Go | `internal/agent/agent.go` | `ProcessesInterval`, `ProcessesPressureInterval`, `ProcessesTimeout`, `Options.Processes`, `jobs`, `newScheduler`, `programRules` |
| C# | none in `src/` | — (the C# side only gets the shared-fixture test) |

`cmd/vandox-agent/main.go` is final as it is (it passes no sources; I read it).

## Signatures (for the Dev's skeleton)

```go
// package procstats
const (
	Name         = "processes" // collector name
	Source       = "processes" // source of every record
	TopProcesses = 25          // processes listed per ranking without memory pressure
	MaxProcesses = 4096        // processes read per run
	ClockTicks   = 100         // USER_HZ
)
type Sources struct {
	Proc fs.FS // default os.DirFS("/proc")
	Etc  fs.FS // default os.DirFS("/etc")
}
type Rule struct {
	Program        string
	Command        string
	CmdlinePrefix  string
	AppendNextWord bool
}
type Processes struct{ /* unexported */ }
func New(src Sources, rules []Rule, log *slog.Logger) *Processes
func (c *Processes) Name() string
func (c *Processes) Collect(ctx context.Context, run scheduler.Run) ([]model.Record, error)

// unexported, named for the tests of the same package (tests are package procstats)
type statFields struct {
	pid, ppid                int32
	comm                     string
	state                    byte
	flags                    uint32
	utime, stime, starttime  uint64
}
func parseStat(pid int32, data string) (statFields, error)
type statusFields struct {
	euid              uint32
	rss, swap         uint64 // bytes
	hasRSS, hasSwap   bool
}
func parseStatus(data string) (statusFields, error)
func parseSmapsRollup(data string) (rss, pss uint64, err error)
func parseCmdline(raw []byte, more bool) (text string, truncated bool)
func cleanText(s string, limit int) (text string, cut bool)
func readBootTime(proc fs.FS) (time.Time, error)
func readPasswd(etc fs.FS) (map[uint32]string, error)
func commandOf(cmdline, fallback string) string
func programOf(command string) string
func programFor(rules []Rule, cmdline, command string) string

// package sysstats
const (
	PressureOnAvg10  = 10.0
	PressureOffAvg10 = 5.0
)
type PressureReading struct {
	MemAvailable, MemTotal uint64  // bytes
	Avg10                  float64 // memory some avg10
	HasAvg10               bool
}
func NextPressure(high bool, r PressureReading) bool
func (c *System) SetPressureSwitch(f func(high bool))

// package config
type Processes struct {
	Programs []ProgramRule `yaml:"programs"`
}
type ProgramRule struct {
	Program        string `yaml:"program"`
	Command        string `yaml:"command"`
	CmdlinePrefix  string `yaml:"cmdline_prefix"`
	AppendNextWord bool   `yaml:"append_next_word"`
}
const (
	MaxProgramRules   = 64
	MaxRuleMatchBytes = 256
)
// Agent gains: Processes Processes `yaml:"processes"` (after Filesystem)
func (d *decoder) walkList(path string, k, v *yaml.Node, field reflect.Value) error
func checkProgramRules(file string, lines map[string]int, rules []ProgramRule) error

// package agent
const (
	ProcessesInterval         = 60 * time.Second
	ProcessesPressureInterval = 15 * time.Second
	ProcessesTimeout          = 10 * time.Second
)
// Options gains: Processes procstats.Sources
func jobs(opts Options, cfg *config.Agent, log *slog.Logger) ([]scheduler.Job, *sysstats.System) // was: []scheduler.Job
func programRules(rules []config.ProgramRule) []procstats.Rule
```

## Test files

Named by *Layout* (`foo.go` → `foo_test.go`, colocated):

- `internal/procstats/procstats_test.go` (collector tests of AC1, AC2 fields, AC4, AC8, AC9, AC10, `BenchmarkProcesses_Collect`),
  `stat_test.go`, `status_test.go`, `smaps_test.go`, `cmdline_test.go`, `boottime_test.go`, `passwd_test.go`, `names_test.go`
  (AC2 shared cases, AC3), `snapshot_test.go` (AC5 `TestProcesses_CPUPercent`).
- `internal/sysstats/pressuremode_test.go` (`TestNextPressure`), `internal/sysstats/system_test.go`
  (`TestSystem_SwitchesPressureOnlyOnChange`).
- `internal/config/agent_test.go`, `internal/config/decode_test.go`.
- `internal/agent/agent_test.go`.
- `tests/Vandox.Core.Tests/LegacyProcessNamesTests.cs` (existing class, new method).

Fixtures (Tester): `testdata/proc/<pid>/{stat,status,cmdline,smaps_rollup}` captured from the development host for a
multi-argument process, a `setproctitle` process (`perl -e '$0="php-fpm: pool example.com"; sleep 60'`), a second process of the
same program sharing pages (for AC1), a `comm` with `) (` and a line feed (set with `prctl(PR_SET_NAME)`); `stat`, `status` and
`cmdline` of a kernel thread and of a zombie (their `smaps_rollup` is `ESRCH`, answered by the error wrapper); `testdata/etc/passwd`
(the host's file); `testdata/processes/names.json`. The existing `testdata/proc/stat` holds `btime`. The PID directories sit
beside the existing fixtures, which list no PID directory today.

Existing test call sites of changed signatures:

- `jobs` (`internal/agent/agent_test.go:464`, `TestJobs_Schedules`): the old signature goes away, so the **Dev** adapts the call
  to the two results in step 4, mechanically; the **Tester** extends the expected job list in step 5.
- `agent.Options` gains a field (old literals compile): the **Tester** adds `Processes: fixtureProcesses()` to every `Options`
  literal of `agent_test.go` in step 5, so no agent test reads the host's `/proc` or `/etc`.
- `config.AgentKeys`, `DefaultAgent` (pinned in `internal/config/agent_test.go`): the **Tester** extends the expectations.

Files no unit test can cover: none.

## Real samples

The formats are the kernel's, captured on the development host (kernel 6.18), as for record 0111. Variants: see *Fixtures*. Not
captured: kernel 5.15 of the server — its `smaps_rollup` lacks `Pss_Dirty` (the parser ignores the line either way) and its
`status` has no `Kthread:` line (not read; kernel threads are told by `PF_KTHREAD`). The `5.15` variants in the tests are the 6.18
captures with those lines removed, stated as derived. The `php-fpm` process title is PHP-FPM's documented form, captured through
`perl`, not from a Plesk server; the parallel run of #45 is the check on the target.

## Areas

- `docs/areas/agent.md` (Dev): new section *Processes* (collector, schedule, readers and bounds of AC7, outcomes and logs of AC8,
  selection, CPU, names and rules, budget, the blocking-read limitation, worst-case memory about 9 MB for 4,096 processes with
  maximal texts, the 100 ms value with where it was measured); new section *Memory pressure mode* (thresholds and logs of AC6,
  and the effect on `system` and `processes`); *Rights* (AC12); the *Status* paragraph, the job table, *Implementation* and
  *Related decisions* (0113).
- `docs/areas/configuration-and-secrets.md` (Dev): row `processes.programs` and a rule paragraph with the exact error texts of
  AC11 and the list-of-mappings rule.
- `docs/areas/wire-format.md`: none (no change of the format).

## Documentation updates

- `README.md` (Dev): *Agent options* row `processes.programs`.
- `deploy/agent/agent.yaml` (Dev): `processes:` with `programs: []` and a commented example rule
  (`program: php-fpm`, `cmdline_prefix: "php-fpm: pool "`, `append_next_word: true`).
- `deploy/agent/README.md` (Dev, new): the agent's rights per collector, first entry `CAP_SYS_PTRACE` for `smaps_rollup` (the
  rule of the Agent area, *Rights*, that rights are listed in `deploy/agent/`).
- `docs/BENCHMARKS.md` (Dev): section *Process snapshot* (AC13).
- `.squad/project.md` (Dev): security area 10 (the `procstats` readers and their bounds, the list-of-mappings decoder, records
  0113), area 12 (process names and command lines are never logged by the agent), area 6 (the process collector is the user of
  `CAP_SYS_PTRACE`); *Test doubles* `/proc` row (implemented for `internal/procstats`, the per-file error wrapper,
  `testdata/etc/passwd`).
- `.squad/stack.md` (Dev): *Layout* lists `procstats`.
- `docs/ARCHITECTURE.md` (Lead, done in this plan commit): the component paragraph names the switch and the collector and links
  0113.

## Architecture check

- *A hanging collector never blocks the agent*: the collector runs in the frame like every other; a blocked read is abandoned
  with a gap (AC10). Kept.
- *No data gaps unless recorded*: a failed sample is a gap through the scheduler; a partial sample is marked `complete` false;
  absent PSS is left out, never 0 (except for processes without address space, where 0 is the measurement). Kept.
- *Backfill never alerts*: not touched.
- The pressure switch never records a gap and never makes a run late (scheduler, 0110). The agent stays read-only on `/proc`
  and `/etc`, starts no program and opens no socket (0019, 0006).
- Memory budget (0025): typical snapshot state is tens of kilobytes; the worst case is bounded (AC7, AC9, AC10).

## Security considerations

- [ ] Byte limits: every read limit counts raw bytes of the file before any decoding; the text limits (1,024 bytes for `comm`
  derivations, command line, command and program; 256 for user names) count bytes **after** the replacement of invalid UTF-8
  by U+FFFD, so the encoded value never exceeds them (AC7, AC9).
- [ ] Exceptions reaching a user-visible text: none — the agent has no user interface; error texts go to the scheduler's log
  and hold only file names relative to `/proc` or `/etc` and fixed reasons (AC8). Configuration errors are the fixed texts of
  AC11.
- [ ] *Unverified, measured once by the Dev before the area document and `deploy/agent/README.md` state it*: on the development
  host, as an unprivileged user (`setpriv --reuid=65534 --regid=65534 --clear-groups`), open `smaps_rollup`, `stat`, `status`
  and `cmdline` of a process of another user (a `sleep` started by root in the same namespace, not PID 1): (a) without
  capabilities, (b) with ambient `CAP_SYS_PTRACE` only (`--inh-caps=+sys_ptrace --ambient-caps=+sys_ptrace`), (c) with ambient
  `CAP_DAC_READ_SEARCH` only. Expected: `smaps_rollup` `EACCES` in (a) and (c), readable in (b); the other three readable in all.
  The result goes into `log.md`; a different result is a plan deviation for the Lead. Security checks it in its review.
- [ ] Untrusted text: `comm`, command line and the appended rule word are chosen by any local user. They are parsed by position
  (the last `)`), never interpreted, stored as text, never logged, and escaped by whoever displays them (area 12). A forged
  name can take program places; their number is bounded by the record budget.
- [ ] The grouping rules are literal comparisons; no pattern language reaches user input.
- [ ] Integer conversions follow *Writing code* (parse into the target width or check both bounds right before the conversion);
  kB values times 1,024 are checked for overflow; sums saturate.

## Decision records

- `docs/decisions/0113-process-collector-pss-from-smaps-rollup-top-processes-full-snapshot-under-memory-pressure.md` (Proposed,
  indexed).

## Out of scope / follow-ups

- **Follow-up issue (orchestrator):** title `[Agent] Model text limits must hold after the encoder's UTF-8 replacement`; body:
  "`internal/model` checks text fields by the bytes of the Go string (`checkShort`/`checkText` in `model.go`), while the wire
  encoder replaces every invalid UTF-8 byte with U+FFFD (3 bytes) and the backend validates the decoded length. A field of
  1,024 invalid bytes therefore passes `wire.CheckRecord` but is refused by the backend, and with it the whole batch (0044).
  The process collector (#32) cleans its texts itself; the model should refuse invalid UTF-8 or measure the replaced length,
  so every producer (log shipping #37) is covered. Found while planning #32."
- `oom_score_adj` per process (one more file per process) — not asked by #32.
- Showing PSS in the web UI and using it in `MemoryAnalysis` — #50.
- Thresholds as options — after #45.

## Challenge notes

Minor objections of the Devil's Advocate (0 major), verbatim in substance:

1. **Mixed ranking key (AC1/AC4).** The key is "PSS when present, else RSS". When only some processes have PSS (partial `EACCES`), PSS and RSS values are compared, and RSS is larger for shared pages. The Dev states this as a known limitation in `docs/areas/agent.md` under *Processes* (or ranks by RSS whenever any process lacks PSS); the tests include one mixed case so the order is deliberate.
2. **Cost under pressure.** In pressure mode `smaps_rollup` of up to 4,096 processes is read every 15 s on a host already short of memory (page-table walk, mmap lock). The Dev records this in `docs/areas/agent.md` *Processes* as an open point for #44/#45 and states the process count in the benchmark note.
3. **Detection latency.** The `system` collector samples every 30 s (10 s under pressure), so the switch to the full snapshot can take up to 30 s. The area document's *Memory pressure mode* section says so.
4. **Config rule limit.** AC11 caps `program` at 128 bytes, while `append_next_word` can reach 1,024 bytes (AC3). Add a test that a 1,024-byte appended program passes the model's `checkRequiredShort`, and pin that the appended word is UTF-8-clean before the cut.
5. **Sample wording.** The plan's real captures are from kernel 6.18, not a Plesk server's `php-fpm`, `spamd` or `amavisd`. Keep the sentence deferring that to #45 in the area document so the `perl`-derived title is not read as a Plesk sample.
