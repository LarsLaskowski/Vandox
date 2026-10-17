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
  read can be refused with the capability too. That `CAP_SYS_PTRACE` alone suffices and `CAP_DAC_READ_SEARCH` does not help is
  **measured** by Security in the plan review (*Security considerations*); the Dev does not repeat it.
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
  The programs are summed over **every process read**, not only the listed ones, so the counts add up to `total` (less the
  processes AC8 and AC9 leave out). The sums are kept in a map keyed by the final program text (AC2, AC3), so no two programs
  share a name. Programs are ordered by one memory key for the whole run — the PSS sum when **every** program has one, else
  the RSS sum for all of them (Challenge 1) — descending, then by name (ordinal).
  Tests: `TestProcesses_ProgramSumsPSSAndRSS` (two real captures of one program: exact RSS and PSS sums, RSS sum above PSS sum),
  `TestProcesses_ProgramPSSOmittedWhenAMemberHasNone`, `TestProcesses_ProgramsCoverUnlistedProcesses` (more than 50 processes
  without pressure: the sum of `count` equals `total`), `TestProcesses_RanksByRSSWhenPSSPartlyMissing` (one process with
  `EACCES` on `smaps_rollup`: processes and programs ordered by RSS; the same snapshot with every PSS readable ordered by PSS).
- [ ] **AC2 Same output model as the legacy parser.** One `process_snapshot` record per run, `captured_at` the run's start.
  Per process: `pid`, `ppid` (from `stat`), `user` (the name of the **effective** UID from `/etc/passwd`, else the UID in
  decimal), `command`, `cmdline`, `truncated`, `state` (the `stat` state when it is one ASCII letter, else left out),
  `started_at` (boot time plus `starttime` / 100 s, UTC), `cpu_percent`, `rss_bytes`, `pss_bytes`, `swap_bytes` (`VmSwap`,
  left out when the line is absent); `oom_score_adj` is not read. `cmdline`: trailing NUL bytes removed, every other NUL a
  space. `command`: the legacy derivation — the text inside `[...]` when the whole command line is bracketed, else the part
  after the last `/` of the first space-separated word with one trailing `:` removed; when that is empty, the command line;
  when that is empty too, the fallback. The agent's fallback is the cleaned `comm`, or the fixed text `(unnamed)`
  (`UnnamedCommand`, no process text) when `comm` is empty, so `command` is **never empty** (Security B1: `perl -e '$0=""'`
  gives an empty command line and an empty `comm`). Default program: the first space-separated word of `command`, `command`
  itself when that word is empty — never empty either, because `command` is not. Both derivations are pinned by one shared
  fixture `testdata/processes/names.json` (cases: `cmdline`, `fallback`, `command`, `program`) read by both languages; the
  agent's choice of the fallback is `commandFor` (Go only).
  Tests: `TestProcesses_SampleFieldsFromFixtures` (every field above, from the real captures, exact values),
  `TestCommandOf_SharedCases` and `TestProgramOf_SharedCases` (Go), `TestCommandFor_EmptyCommUsesUnnamed` (Go: empty command
  line with empty `comm` gives `(unnamed)`, with `comm` `x` gives `x`), `TestProcesses_UnnamedProcessIsRecorded` (a fixture
  process whose `stat` holds `()` and whose `cmdline` is empty, pressure on: `Collect` returns no error, so the run is no gap,
  the process is listed with command and program `(unnamed)`, `complete` is true and the record passes `wire.CheckRecord`),
  `LegacyProcessNamesSharedCasesMatchAgent` (C#, in the existing `LegacyProcessNamesTests`: `CommandOf(cmdline, fallback)` and
  `ProgramOf(command)` give the fixture's values). The fixture holds at least: `/usr/sbin/apache2 -k start` →
  `apache2`/`apache2`; `php-fpm: pool example.com` → `php-fpm`; `spamd child` → `spamd`; `amavisd (master)` → `amavisd`;
  `[kthreadd]` → `kthreadd`; empty with fallback `kworker/u16:0-kvfree_rcu_reclaim` → that fallback; **empty with fallback
  `(unnamed)`** (empty command line and empty `comm`, as the agent passes it) → `(unnamed)`/`(unnamed)`; `/` → `/` (derived
  name empty); `/usr/bin/` → `/usr/bin/`; `a:` → `a`; a command line of spaces only `   ` → `   `/`   `; `[]` → `[]`; a name
  with a space inside brackets `[a b]` → command `a b`, program `a`.
- [ ] **AC3 Grouping rules.** Rules are tried in configuration order; the first match gives the program. A rule matches by
  exactly one of `command` (equal to the derived command, ordinal) or `cmdline_prefix` (a literal, ordinal prefix of the
  command line). With `append_next_word`, the program is `<program>/<word>`, where word is the text after the prefix up to
  the next space, cut so the program is at most 1,024 bytes at a character boundary; an empty word gives `<program>`. The word
  is taken from the command line **after** its cleaning (AC7), so it is valid UTF-8 before it is cut and stays so. No match:
  the default program (AC2). Default rule list: empty.
  Tests: `TestProgramFor_Rules` (command rule, prefix rule, appended word, empty word, first rule wins, no match → default,
  a word cut at a character boundary, a prefix that equals the whole command line),
  `TestProgramFor_AppendedProgramPassesModel` (Challenge 4: a 128-byte rule program with a long multi-byte word gives a program
  of at most 1,024 bytes of valid UTF-8 that passes `checkProgramEntry`, AC9).
- [ ] **AC4 Top processes and full snapshot.** Without pressure mode the list holds the union of the top 25 by memory key and
  the top 25 by `cpu_percent`, ties by PID ascending, ordered by memory key descending then PID. The memory key is one for the
  whole run: PSS when **every** process read has one, else RSS for all (Challenge 1; test in AC1);
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
  - `started_at`: `btime` plus `starttime` / 100 s must be a time before the year 10000 (Go's JSON encoding of a time fails
    beyond it); otherwise the process fails to parse (AC8: left out, `complete` false). The arithmetic is checked for overflow
    before any conversion to a signed type.
  - PID directories (Security N1): `/proc` is listed in chunks (`fs.ReadDirFile.ReadDir(256)`, never `fs.ReadDir`, which reads
    and sorts every name at once). A PID directory's name is the **canonical** decimal of a 32-bit PID > 0: 1 to 10 ASCII digits
    without a leading zero; any other name is not a process and not counted. A bounded max-heap keeps the 4,096 numerically
    lowest PIDs while the listing streams; every other PID directory is counted in `total` and makes `complete` false. The
    listing holds at most one chunk and 4,096 PIDs. Canonical names also make the PIDs of one run distinct.
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
  `EACCES`, `EIO`, listing fails, `btime` missing, no process, a `starttime` that puts `started_at` in the year 10000: that
  process left out and `complete` false), `TestProcesses_LogsTransitionsOnce`,
  `TestProcesses_ErrorsAndLogsHoldNoProcessText` (a `comm`, command line and user name holding a sentinel, made malformed:
  neither the error nor any log line contains it).
- [ ] **AC9 The record always fits and is valid on both sides; no text a user chooses can fail the sample (Security B1).**
  - *By construction.* Every rule the model checks per entry (Go `internal/model/process.go` `validate`, C#
    `ProcessSample.Validate` and `ProgramAggregate.Validate`, both counting UTF-8 bytes) and every way the encoder can fail
    holds by construction, re-checked against the whole list:
    - `pid` > 0 and distinct: canonical PID directory names (AC7), equal to the `stat` PID;
    - `ppid` ≥ 0: parsed as 32-bit and checked (AC7);
    - `user` ≤ 1,024 bytes: a `passwd` name of at most 256 bytes of valid UTF-8, or a UID of at most 10 digits;
    - `command` non-empty (AC2, `(unnamed)` as the last fallback), ≤ 1,024 bytes, valid UTF-8 (AC7);
    - `cmdline` ≤ 1,024 bytes (the model allows 16,384), valid UTF-8;
    - `state` one ASCII letter or left out (AC2);
    - `started_at` in UTC, after 1970 and before the year 10000 (AC7), so neither the zero time nor an encoding error;
    - `cpu_percent` finite and ≥ 0: tick deltas only when the counters did not fall, divided by a positive duration, else 0
      (AC5); every product and sum of at most 4,096 such values stays finite;
    - `oom_score_adj` never set;
    - `program` non-empty (from `command` or a validated rule `program`), ≤ 1,024 bytes, valid UTF-8 (AC3), distinct (map key,
      AC1); `count` ≥ 1; program `cpu_percent` a sum of finite non-negative values;
    - at least one entry (a run without a process read fails, AC8); at most 4,096 processes and programs (`MaxItems`, AC7).
  - *Checked per entry anyway.* Each entry is checked alone before it goes into the record: `checkProcessEntry` /
    `checkProgramEntry` validate it as a one-entry `model.ProcessSnapshot` and encode it with `json.Marshal`, giving its size.
    An entry that fails either is **left out** and makes `complete` false (counted with the processes not read of AC8, the
    error text fixed: `processes: entry refused`, never the entry's text); it never fails the sample.
  - *Budget.* The collector fills the record entry by entry within a payload budget of 1 MiB − 8 KiB of JSON (each entry's
    size from its check): programs first, at most half of the budget, in their order, stopping at the first that does not fit;
    then processes in their order, stopping at the first that does not fit. A left-out entry makes `complete` false. The
    finished record is checked with `wire.CheckRecord`; on a size error the processes and then the programs are halved until it
    passes (at most 13 rounds each, a safety net the budget makes unused); any other error — by the list above only an
    implementation fault — fails the sample.
  Tests: `TestProcesses_LargeSnapshotFitsTheLineLimit` (4,096 processes in an `fstest.MapFS`, each with a distinct command line
  of more than 4 KiB of control characters, pressure on: `wire.CheckRecord` passes, `complete` false, every program kept before
  any process; and the allocation bound of AC10), `TestProcesses_InvalidUTF8StaysWithinLimits` (1,024 invalid bytes: the record
  encoded by `wire.EncodeBatch` and decoded by `wire.NewDecoder` passes the model's validation, `command` and `cmdline` at most
  1,024 bytes each), `TestProcesses_ReadsAtMostMaxProcesses` (PIDs 1 to 4,100 plus a directory `0123` and `self`, listed by
  `fstest.MapFS` in lexical order: exactly PIDs 1 to 4,096 read, `total` 4,100, `complete` false), `TestCheckEntry_Refuses`
  (table: empty `command`, empty `program`, NaN and negative `cpu_percent`, `started_at` in the year 10000 and not in UTC, a
  1,025-byte `command`: each refused; a valid entry accepted with its encoded size), `TestFillSnapshot_LeavesOutRefusedEntry`
  (one refused process between two valid ones: the two are kept in order, `complete` false), `TestProcesses_UnnamedProcessIsRecorded`
  (AC2).
- [ ] **AC10 Hanging source and bounded memory (0029, area 10).** A read that blocks is abandoned by the scheduler at the
  timeout with a gap, and the collector is not started again while it hangs (a deliberate residual, Security N2: a local user
  who holds the memory map lock of a process can make its `cmdline` or `smaps_rollup` read block, which costs process
  samples, as gaps, until the process moves). Reading a command line holds at most 4 KiB per process. The package imports no
  program, socket, cgo or `unsafe` and makes no raw system call.
  **Allocation bound (Security B2).** One `Collect` over 4,096 processes with maximal texts allocates at most **48 MiB in
  total** (`runtime.MemStats.TotalAlloc` before and after, garbage included, so the heap it holds at any moment is less); this
  asserted figure is the only memory figure the area document states. Design that keeps it: one read buffer per file kind
  reused for every process of a run, the `cmdline` text cut while it is cleaned (never a 4 KiB string per process kept), the
  listing in chunks (AC7), the budget fill stopping at the first entry that does not fit (AC9). The Dev records the measured
  value in `log.md`; a value above the bound is a plan deviation for the Lead (optimize, or raise the bound and the documented
  figure together — never one without the other).
  Tests: `TestProcesses_BlockingReadIsAbandonedAsGap` (scheduler with the collector over a blocking file system in a
  `testing/synctest` bubble), `TestProcesses_ReadsBoundedCmdline` (64 processes with 1 MiB command lines: bytes allocated by
  `Collect` below 64 × 64 KiB, measured with `runtime.MemStats`), `TestProcesses_LargeSnapshotFitsTheLineLimit` (AC9's
  fixture, built before the measurement; the bytes allocated by one `Collect` at most 48 MiB, measured like `allocatedBy` in
  `internal/wire/decode_test.go`; the test is not parallel), `TestProcstats_ImportsOnlyAllowedPackages` (an allow-list test like
  `TestSysstats_ImportsOnlyAllowedPackages`; `os/user` is not allowed).
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
  and leaves PSS out. Verified by Security's measurement in the plan review (*Security considerations*, measured) and the
  Reviewer's reading.
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
   `os.DirFS("/etc")`), the collector `Processes` with `Collect`. One run: read `btime`, read `passwd`, list `/proc` in chunks
   keeping the lowest 4,096 canonical PIDs (`listPIDs`, AC7), read each process (`stat`, `status`, `cmdline`, `smaps_rollup`,
   in this order; one reused buffer per file kind), derive command (`commandFor`) and program, keep per process only its
   numbers and its three texts (each ≤ 1,024 bytes); sum programs in a map; compute CPU against the previous run's map (PID →
   `starttime`, ticks), which replaces the old map after a successful run; select (AC4); check and fill the entries within the
   budget (`checkProcessEntry`, `checkProgramEntry`, `fillSnapshot`, AC9); compare the warning states with the last run and
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
syntax (string grammar versus **list of mappings in the strict decoder**); a process without any name (leave it out versus
**a fixed placeholder `(unnamed)`** — leaving it out would let a local user hide a process's memory from the program sums by
blanking its name); the ranking key when PSS is partly missing (mixed PSS and RSS versus **RSS for the whole run**).

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Go | `internal/procstats/procstats.go` | new: package doc, constants, `Sources`, `Rule`, `Processes`, `New`, `Name`, `Collect`, `listPIDs`, warning states |
| Go | `internal/procstats/stat.go` | new: `stat` reader (AC7) |
| Go | `internal/procstats/status.go` | new: `status` reader |
| Go | `internal/procstats/smaps.go` | new: `smaps_rollup` reader |
| Go | `internal/procstats/cmdline.go` | new: `cmdline` reader and text cleaning (UTF-8, cut) |
| Go | `internal/procstats/boottime.go` | new: `btime` reader of `/proc/stat` |
| Go | `internal/procstats/passwd.go` | new: `/etc/passwd` reader |
| Go | `internal/procstats/names.go` | new: `commandOf`, `commandFor`, `programOf`, `programFor` (rules) |
| Go | `internal/procstats/snapshot.go` | new: CPU, aggregation, selection, `checkProcessEntry`, `checkProgramEntry`, `fillSnapshot` |
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
	UnnamedCommand = "(unnamed)" // command and program of a process with an empty command line and an empty comm
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
func commandOf(cmdline, fallback string) string          // the legacy derivation, pinned by names.json
func commandFor(cmdline, comm string) string             // commandOf with comm, or UnnamedCommand when comm is empty
func programOf(command string) string
func programFor(rules []Rule, cmdline, command string) string
func listPIDs(proc fs.FS) (pids []int32, seen int, err error) // lowest MaxProcesses canonical PIDs, ascending; seen counts all
func checkProcessEntry(p *model.ProcessSample) (size int, err error)
func checkProgramEntry(a *model.ProgramAggregate) (size int, err error)
// fillSnapshot keeps entries within budget bytes (programs first, at most budget/2); complete is false when one was left out.
func fillSnapshot(procs []model.ProcessSample, progs []model.ProgramAggregate, budget int) (snap model.ProcessSnapshot, complete bool)

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
  (AC2 shared cases, `TestCommandFor_EmptyCommUsesUnnamed`, AC3 with `TestProgramFor_AppendedProgramPassesModel`),
  `snapshot_test.go` (AC5 `TestProcesses_CPUPercent`, AC9 `TestCheckEntry_Refuses`, `TestFillSnapshot_LeavesOutRefusedEntry`).
  `listPIDs` lives in `procstats.go` and is tested through `TestProcesses_ReadsAtMostMaxProcesses`.
- `internal/sysstats/pressuremode_test.go` (`TestNextPressure`), `internal/sysstats/system_test.go`
  (`TestSystem_SwitchesPressureOnlyOnChange`).
- `internal/config/agent_test.go`, `internal/config/decode_test.go`.
- `internal/agent/agent_test.go`.
- `tests/Vandox.Core.Tests/LegacyProcessNamesTests.cs` (existing class, new method).

Fixtures (Tester): `testdata/proc/<pid>/{stat,status,cmdline,smaps_rollup}` captured from the development host for a
multi-argument process, a `setproctitle` process (`perl -e '$0="php-fpm: pool example.com"; sleep 60'`), a second process of the
same program sharing pages (for AC1), a `comm` with `) (` and a line feed (set with `prctl(PR_SET_NAME)`), a process without
any name (`perl -e '$0=""; sleep 60'`: empty `cmdline`, `stat` with `()`, for `TestProcesses_UnnamedProcessIsRecorded`); `stat`, `status` and
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

- `docs/areas/agent.md` (Dev): new section *Processes* (collector, schedule, readers and bounds of AC7 including the chunked
  listing and the PID grammar, outcomes and logs of AC8, selection with the one-key-per-run rule, CPU, names and rules with the
  `(unnamed)` fallback, the per-entry guarantees and the refusal of AC9, budget; the blocking-read residual of AC10 stated as
  deliberate, naming a local user holding a process's memory map lock as a cause; the allocation bound of AC10 — 48 MiB in
  total for one run over 4,096 processes with maximal texts, the asserted figure and no other memory figure; the cost of
  reading `smaps_rollup` of every process every 15 s under pressure as an open point for #44 and #45 (Challenge 2); the 100 ms
  value with where it was measured; the sentence that the PHP-FPM, `spamd` and `amavisd` titles are not Plesk captures and #45
  checks them, Challenge 5); new section *Memory pressure mode* (thresholds and logs of AC6, the effect on `system` and
  `processes`, the detection latency of up to one `system` interval, Challenge 3, and that any local user can switch it on by
  allocating memory, Security N3); *Rights* (AC12, as measured); the *Status* paragraph, the job table, *Implementation* and
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
- Memory budget (0025): typical snapshot state is tens of kilobytes; the worst case is bounded (AC7, AC9) and its allocation
  asserted (AC10, 48 MiB in total for 4,096 maximal processes).

## Security considerations

- [ ] Byte limits: every read limit counts raw bytes of the file before any decoding; the text limits (1,024 bytes for `comm`
  derivations, command line, command and program; 256 for user names) count bytes **after** the replacement of invalid UTF-8
  by U+FFFD, so the encoded value never exceeds them (AC7, AC9).
- [ ] Exceptions reaching a user-visible text: none — the agent has no user interface; error texts go to the scheduler's log
  and hold only file names relative to `/proc` or `/etc` and fixed reasons (AC8). Configuration errors are the fixed texts of
  AC11.
- [x] *Measured by Security in the plan review (the Dev does not repeat it)*: on the development host, as UID 65534 through
  `setpriv` against a process owned by root in the same namespace: `smaps_rollup` answers `EACCES` without capabilities and with
  ambient `CAP_DAC_READ_SEARCH` only, and is readable with ambient `CAP_SYS_PTRACE`; `stat`, `status` and `cmdline` are
  readable in every case. The host had neither Yama restrictions nor `hidepid`; the area document says the rule holds for
  `/proc` mounted without `hidepid` and that a Yama or other LSM policy was not measured.
- [ ] No per-entry field can fail the sample on text a user chooses (AC9, Security B1): every field rule of both models is met by
  construction, and each entry is checked alone and left out when it fails.
- [ ] A local user can (a) switch memory pressure mode on by allocating memory, making the snapshot run every 15 s with every
  process and the spool grow faster until its size bound evicts older data with a recorded gap (record 0113, Security N3), and
  (b) make a run hang by holding a process's memory map lock, which costs process samples as gaps (AC10, Security N2). Both are
  stated residuals, not defects.
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

## Challenge

The Devil's Advocate raised 0 major and 5 minor objections (the former *Challenge notes*, folded in here). All five are accepted.

1. **Mixed ranking key (AC1/AC4).** *Accepted, plan revised.* Rather than documenting a mixed comparison, the key is now one per
   run: PSS when every process read (for programs: every program) has one, else RSS for all. Without `CAP_SYS_PTRACE` that is
   RSS, as `top` ranks. Test `TestProcesses_RanksByRSSWhenPSSPartlyMissing` (AC1); option recorded in 0113.
2. **Cost under pressure.** *Accepted.* The Agent area's *Processes* section names the `smaps_rollup` cost every 15 s under
   pressure as an open point for #44 and #45 (*Areas*); the benchmark note in `docs/BENCHMARKS.md` states the process count
   (already in *Verification of AC13 speed*).
3. **Detection latency.** *Accepted.* The *Memory pressure mode* section states that the switch follows a `system` sample, so
   it can take up to one `system` interval (*Areas*).
4. **Config rule limit.** *Accepted, plan revised.* AC3 pins that the appended word comes from the cleaned command line and is
   cut at a character boundary; `TestProgramFor_AppendedProgramPassesModel` checks that a long appended program passes the
   model through `checkProgramEntry`. AC9 makes the per-entry check general.
5. **Sample wording.** *Accepted.* The Agent area keeps the sentence that the PHP-FPM, `spamd` and `amavisd` titles are not
   Plesk captures and #45 checks them (*Areas*, *Real samples*).

## Security review

Plan review: CHANGES_REQUIRED (2 blocking, 3 non-blocking). Every finding is accepted.

- **B1 (blocking): an empty command line with an empty `comm` failed the whole sample.** *Accepted, plan revised.* Confirmed
  against the code: `checkRequiredShort` refuses an empty `command` (`internal/model/process.go:94`) and an empty `program`
  (`:113`), C# `Check.RequiredShort` likewise (`ProcessSample.cs:121`, `ProgramAggregate.cs:61`). AC2 adds the fixed last
  fallback `(unnamed)` (`UnnamedCommand`, no process text) through `commandFor`, so `command` and the default program are never
  empty; `names.json` gains the case (empty command line, fallback `(unnamed)`, which is what the agent passes for an empty
  `comm`, so the C# derivation agrees without a change); `TestCommandFor_EmptyCommUsesUnnamed` and
  `TestProcesses_UnnamedProcessIsRecorded` (no gap, record passes `wire.CheckRecord`) cover it, with a captured fixture. As
  asked for a guard, the **whole list** of per-entry rules was re-checked, not only the reported one: AC9 now enumerates every
  model rule of both languages and every encoder failure with the construction that meets it. Two more were found on the way
  and closed: `started_at` beyond the year 9999 makes Go's JSON encoding of a time fail (now bounded in AC7, the process left
  out), and a PID directory with a leading zero (`0123`) would duplicate a PID (now not a PID directory, AC7). On top, each entry
  is validated and encoded alone (`checkProcessEntry`, `checkProgramEntry`) and left out with `complete` false when it fails
  (`TestCheckEntry_Refuses`, `TestFillSnapshot_LeavesOutRefusedEntry`), so no user-chosen text can fail the sample.
  Leaving the unnamed process out instead was rejected: a local user could then hide a process's memory from the program sums
  by blanking its name (0113, option 12).
- **B2 (blocking): the documented worst-case memory had no test.** *Accepted, plan revised.* The untested "about 9 MB" is
  dropped. AC10 asserts an allocation bound instead — one `Collect` over 4,096 processes with maximal texts allocates at most
  48 MiB in total (`TotalAlloc`, an upper bound for what the run holds at once) — in `TestProcesses_LargeSnapshotFitsTheLineLimit`,
  names the design that keeps it, and the area document states only that asserted figure. A measured value above it is a plan
  deviation; bound and documented figure only ever change together.
- **N1: numeric selection, chunked listing.** *Accepted.* AC7 specifies `ReadDir(256)` chunks, the canonical PID grammar and a
  bounded max-heap of the 4,096 lowest PIDs, the rest counted in `total`; `TestProcesses_ReadsAtMostMaxProcesses` lists PIDs 1
  to 4,100 in lexical order and asserts exactly 1 to 4,096 are read (`listPIDs`).
- **N2: hanging reads as a deliberate residual.** *Accepted.* AC10 and the Agent area state that a local user holding a
  process's memory map lock can make a read block, the run is abandoned as a gap and process samples are lost until the
  process moves; record 0113 names the local user as the cause (option 10, *Consequences*).
- **N3: local user as a cause of pressure mode.** *Accepted.* Record 0113 (*Consequences*) and the Agent area's *Memory pressure
  mode* section name a local user who allocates memory as a cause of the mode, of the 15-second full snapshots and of the faster
  spool growth, bounded by the record budget and the spool's size bound.
- **Measured claim.** The capability claim (AC12, *Security considerations*) is marked measured by Security; the Dev's
  measurement step is removed.
