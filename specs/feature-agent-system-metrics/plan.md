# Plan: System metrics collector including PSI

Source: Issue #31 | [spec.md](spec.md)
Status: Draft
Tier: security — the change adds an agent configuration option and new parsers of `/proc` and `/sys` content (security area 10,
*Parsing of external input*: configuration and the agent's `/proc` readers), and it constrains the system-call filter of the
future unit (security area 6, *Agent privileges*).

## Problem / root cause

Feature: see [spec.md](spec.md). The frame of #30 (`internal/scheduler`, record 0110) runs only the self-metrics collector
(`internal/agent/agent.go:77-87`, `newScheduler` builds one job). This change adds the package `internal/sysstats` with three
collectors (`system`, `filesystem`, `clock`), the option `filesystem.mount_points`, and wires the collectors into the agent.

Claims of the issue, checked against the code and the session's kernel (Linux 6.18):

| Claim | Result |
| ----- | ------ |
| Depends on #30 | **Confirmed**: `internal/scheduler`, `internal/selfstats`, `internal/sdnotify` are on `main` (38d42fe). |
| CPU per state, load, `/proc/meminfo` with `MemAvailable`, swap | **Confirmed** readable from `/proc/stat`, `/proc/loadavg`, `/proc/meminfo` (captured in the session). |
| PSI in `/proc/pressure/{memory,cpu,io}` with `some`/`full`, avg10/60/300, total | **Confirmed** (session capture). Refinement: the `cpu` file has a `full` line only from kernel 5.13 on (kernel documentation, not measured); Ubuntu 22.04's 5.15 has it, the parser accepts its absence. |
| `/proc/vmstat` `pswpin`, `pswpout`, `pgmajfault`, `oom_kill` | **Confirmed** present (session capture). |
| Disk usage and inodes per configured mount point, "read from `/proc` and `/sys`" | **Refuted** for the source: neither holds file system usage; it comes from `statfs(2)`, a read-only system call. No mount point is configurable today (`internal/config/agent.go:18-24`). |
| I/O throughput | **Confirmed** from `/sys/block/<dev>/stat` (session capture: 17 fields). |
| Clock synchronization status and offset (timesyncd or chrony), "from `/proc` and `/sys`" | **Refuted** for the source: the kernel's view comes from `adjtimex(2)`. Status: confirmed for both daemons by the rule `timedatectl` uses (maximum error < 16 s; systemd v249 `src/timedate/timedated.c` `ntp_synced`, read). Offset: **partly refuted** — only the kernel PLL's remaining offset is readable without a daemon protocol; systemd-timesyncd feeds the PLL (v249 `timesyncd-manager.c` `manager_adjust_clock`, read), chrony 4.2 switches it off and resets the offset to 0 (`sys_timex.c` `initialise_timex`, read), and chrony keeps `STA_UNSYNC` set unless `rtcsync` (`set_sync_status`, read). |
| No external programs | **Confirmed achievable**: every value has a file or read-only system call. |
| Tests against frozen `/proc` sample trees in `testdata/` | `testdata/proc/` holds only `self/status` today; this change adds the trees. |
| Missing PSI handled gracefully and reported once | New behavior; the frame logs a failing collector only at the first failure (`internal/scheduler/runner.go:251-262`), but missing PSI must not fail the sample at all, so the collector logs it itself. |

Related points found on the way (no defect in the code):

- Record 0110 says "the thresholds that switch the mode stay with #31 and #32". Issue #31 names none; #32 does ("Adaptive mode: shorter
  interval and full snapshot when PSI or `MemAvailable` crosses a threshold"). This plan gives the `system` collector a pressure interval
  but leaves the switch to #32 (out of scope below); 0110 needs no edit.
- The unit of #42 would break the clock collector if it used `ProtectClock=yes` or a `@system-service` allow-list without `adjtimex`
  (systemd v249 `src/shared/seccomp-util.c` `@clock`, `src/core/execute.c` `apply_protect_clock`, read). Recorded in 0111 and the Agent
  area (*Rights*) so #42 finds it.

## Acceptance criteria

Test files are in `internal/sysstats/` (external test package `sysstats_test`, black-box through the collectors, as
`internal/selfstats`), `internal/config/agent_test.go` and `internal/agent/agent_test.go`. Error texts are compared exactly where a
criterion names them. "Fixture tree" means `os.DirFS("../../testdata/proc")` and `os.DirFS("../../testdata/sys")`; variants are built
in memory (`fstest.MapFS`) from the fixture's text.

- [ ] **AC1 — first `system` sample from the fixture tree.** `NewSystem` over the fixture tree, run start `T`: the records are exactly,
  in order, `load1/5/15`, `mem_total/free/used/buff_cache/available_bytes`, `swap_total/free/used_bytes`, one `meminfo_bytes` (unit
  `bytes`, value kB × 1024) or `meminfo` (no unit) per line of `testdata/proc/meminfo` in file order with label `field`, the 24 pressure
  records (memory, cpu, io × some, full × avg10, avg60, avg300 in `percent`, `pressure_stall_microseconds_total` in `microseconds`;
  labels `resource`, `kind`), four `vmstat_total` (label `counter`: `pswpin`, `pswpout`, `pgmajfault`, `oom_kill`), and the five disk
  counters of `vda` only (label `device`; `loop0` has no `device` entry); no `cpu_percent`, no rate; every record has source `system`,
  captured at `T`, and passes `wire.CheckRecord` once stamped with origin `agent` and `seq` 1. Values equal the fixture text (the test
  writes them as constants). Tests: `TestSystem_RecordsFixtureTree`, `TestSystem_RecordsPassTheWireCheck`.
- [ ] **AC2 — rates between two samples.** A second sample 30 s later from a variant with raised counters gives `cpu_percent` for the 8
  modes in the order user, system, nice, idle, iowait, irq, softirq, steal (unit `percent`, label `mode`) = Δmode / Δsum × 100 before
  the load records, `vmstat_per_second` (unit `per_second`) after the four totals, and per disk `disk_reads_per_second`,
  `disk_read_bytes_per_second` (`bytes_per_second`), `disk_writes_per_second`, `disk_written_bytes_per_second`, `disk_busy_percent`
  (Δio ms / (Δt s × 1000) × 100) after its counters. No rate when: the counter fell (that counter only; a fallen CPU mode drops all 8
  `cpu_percent`), Δsum of the CPU counters is 0, the second start is not after the first, a disk is new; a failed sample (malformed
  `meminfo`) between two good ones is not a predecessor (the third sample's rates span from the first). Tests:
  `TestSystem_RatesBetweenSamples`, `TestSystem_NoRateWithoutPredecessor`.
- [ ] **AC3 — memory as `top` prints it.** Variants: `mem_used_bytes` = (MemTotal − MemFree − Buffers − Cached − SReclaimable) × 1024;
  when that is negative, (MemTotal − MemFree) × 1024; `mem_buff_cache_bytes` = (Buffers + Cached + SReclaimable) × 1024;
  `swap_used_bytes` = (SwapTotal − SwapFree) × 1024 and left out (the other two swap records kept) when SwapFree > SwapTotal; a required
  field (MemTotal, MemFree, MemAvailable, Buffers, Cached, SReclaimable, SwapTotal, SwapFree) missing or without ` kB` fails with
  `sysstats: meminfo: no <Field> value in kB`. Test: `TestSystem_MemoryAsTopPrintsIt`.
- [ ] **AC4 — missing PSI is graceful and reported once.** Cases: the `pressure` directory missing; only `pressure/io` missing; every
  read of `pressure/memory` answering an error that wraps `syscall.EOPNOTSUPP`. Each sample succeeds with every other record and
  without the unavailable resources' pressure records; over three samples exactly one warning `pressure stall information unavailable`
  with `collector=system` and `resources=memory,cpu,io` (or `resources=io`, `resources=memory`) is logged; a change of the set (io,
  then memory and io) logs one more warning with the new set; a sample with all files back logs one info line `pressure stall
  information available`, the next nothing. A permission error on a pressure file fails the sample (not "unavailable"). A `cpu` file
  with only a `some` line gives the four `some` records of `cpu` and no warning. Tests: `TestSystem_PressureUnavailable`,
  `TestSystem_PressureAvailabilityLoggedOnChange`, `TestSystem_PressureWithoutFullLine`.
- [ ] **AC5 — malformed input fails the sample with a fixed text.** Table test, each case one in-memory variant, `Collect` returns no
  records and exactly this error text (`<n>` the 1-based line):
  `sysstats: stat: no cpu line` (empty file; first line `intr 1`; `cpu0 …` first), `sysstats: stat: cpu line: not 8 or more counters`
  (7 counters; a counter `-1`, `1x`, 21 digits, 18446744073709551616), `sysstats: stat: first line longer than 4096 bytes`,
  `sysstats: loadavg: not a load average line` (`1.0 2.00 3.00 1/2 3`, `1,00 …`, `+1.00 …`, two fields, 11 integer digits),
  `sysstats: meminfo: line <n>: not a meminfo line` (no colon, name of 65 characters, a name with `-` or a non-ASCII letter, no value,
  `12 MB`, ` kB` without space, a tab before the value only, 21 digits, a value whose × 1024 overflows 64 bits, an empty line in the
  middle), `sysstats: meminfo: line <n>: repeated field`, `sysstats: meminfo: more than 128 lines`,
  `sysstats: vmstat: line <n>: not a counter line` (`pswpin -1`, `pswpin`, `pswpin  1`, `pswpin 1 2`),
  `sysstats: vmstat: line <n>: repeated counter`, `sysstats: vmstat: no oom_kill line`,
  `sysstats: pressure/memory: line <n>: not a pressure line` (`full` before `some`, a third line, `some avg10=0.0 …`,
  `avg10=100.01`, `avg10=1000.00`, keys in another order, two spaces, `total=-1`, a line `n/a`),
  `sysstats: pressure/memory: no some line` (empty file),
  `sysstats: block/vda/stat: not a block device stat line` (10 fields, a field `x`);
  and the size bounds `sysstats: <file>: larger than <limit> bytes` for `loadavg` 256, `meminfo` 16384, `vmstat` 65536,
  `pressure/cpu` 256, `block/vda/stat` 1024 (a file of exactly the limit is read). Accepted variants: `cpu` line with exactly 8 and with
  10 counters; block `stat` with 11, 15 and 17 fields and leading spaces; vmstat lines of other names in any form (`nr_foo -3`); a
  `meminfo` line without ` kB` (`HugePages_Total:       0`) as `meminfo`. Tests: `TestSystem_RejectsMalformedInput`,
  `TestSystem_AcceptsKernelVariants`, `TestSystem_ReadLimits`.
- [ ] **AC6 — disk selection.** Only `/sys/block` entries with a `device` entry are disks; of 20 such entries the 16 first in name order
  are recorded and the rest ignored without error; an entry with `device` but without `stat` fails with
  `sysstats: block/<dev>/stat: …` wrapping the not-exist error. Test: `TestSystem_DiskSelection`.
- [ ] **AC7 — file system usage per configured mount point.** `NewFilesystem` over the fixture `self/mountinfo` with a fake `StatFS`
  (scripted per path) and mount points `["/", "/proc"]`: records in configuration order, per mount `filesystem_size_bytes`, `_used_`,
  `_free_`, `_available_bytes` (block counts × `BlockSize`, used = size − free) and `filesystem_inodes`, `_inodes_used`,
  `_inodes_free`, label `mount` the configured path, source `system`; inode records left out when `Inodes` is 0; `StatFS` called only
  for mounted paths. Escapes (the test asserts that each input still holds its backslash): mount points `/srv/a\040b` and `/srv/b\134x`
  in mountinfo match the configured `/srv/a b` and `/srv/b` + backslash + `x`; lines with `/srv/t\011x` and `/srv/n\012x` are read
  without error (a configured path cannot hold a tab or line feed, so they match nothing); a backslash
  followed by fewer than 3 octal digits or by `8`/`9` fails with `sysstats: self/mountinfo: line <n>: not a mountinfo line`, as does a
  line with fewer than 5 fields; a line of 65,537 bytes fails with `sysstats: self/mountinfo: line <n>: longer than 65536 bytes`, one of
  65,536 bytes is read. Not mounted: `/srv` configured but absent gives one warning `mount point not mounted` with `collector=filesystem`
  and `mount=/srv` over three samples, an info line `mount point mounted` when it appears, and a warning again when it disappears.
  Errors: `StatFS` failing with `syscall.EACCES` for mount 1 fails with exactly `sysstats: filesystem.mount_points[1]: statfs:
  permission denied` (no path in the text); free blocks above the total, available blocks above the total and free inodes above the
  total fail with `sysstats: filesystem.mount_points[<i>]: statfs: free above total`; `Blocks` × `BlockSize` above 2^64 − 1 (cases:
  `Blocks` 2^63 with `BlockSize` 2, and `Blocks` 2^64 − 1 with `BlockSize` 4096) fails with
  `sysstats: filesystem.mount_points[<i>]: statfs: size above 64 bits`, and `Blocks` 2^52 with `BlockSize` 4096 (exactly 2^64) fails
  the same way, while `Blocks` 2^52 − 1 with `BlockSize` 4096 is recorded as `filesystem_size_bytes` 2^64 − 4096 (a float64 record
  value; the test compares with `float64(uint64(1<<52-1) * 4096)`). Tests: `TestFilesystem_RecordsMountPoints`,
  `TestFilesystem_MountinfoEscapes`, `TestFilesystem_NotMountedLoggedOnChange`, `TestFilesystem_Errors`.
- [ ] **AC8 — clock.** With a fake clock source: `MaxError` 15.999999 s gives `clock_synchronized` 1, 16 s gives 0;
  `clock_max_error_seconds` and `clock_estimated_error_seconds` in seconds; `clock_pll_offset_seconds` (seconds, sign kept: −0.25 s
  stays −0.25) only when `PLL` is true; an error of the source is returned unchanged; a negative `MaxError` or `EstimatedError` fails with
  `sysstats: adjtimex: negative error estimate`. The real sources: `ReadClock()` returns no error and non-negative errors;
  `StatFS("/")` returns no error, `BlockSize` > 0 and `Blocks` ≥ `BlocksFree` ≥ `BlocksAvailable`; `StatFS` of a missing path returns an
  error whose text does not contain the path. Tests: `TestClock_Records`, `TestClock_Errors`, `TestReadClock_ReadsTheKernel`,
  `TestStatFS_ReadsTheRootFilesystem`.
- [ ] **AC9 — a hanging source costs only its own sample.** In a `testing/synctest` bubble, a scheduler with the collector under test and a
  scripted collector that always succeeds: the collector's source blocks (the blocking `fs.FS` wrapper on `meminfo` for `System`; a
  blocking `StatFS`; a blocking clock source), released in `t.Cleanup`. After three intervals: a `collector_timeout` gap for the first
  run and one per skipped run with `collector` set to the collector's name, the blocked source entered exactly once, and the scripted
  collector's records present for every interval. Tests: `TestSystem_HangingSourceIsAbandoned`,
  `TestFilesystem_HangingStatFSIsAbandoned`, `TestClock_HangingSourceIsAbandoned`.
- [ ] **AC10 — no program, D-Bus or socket (allow-list).** Checked with `go/parser` (`parser.ParseFile` with `parser.ParseComments` on
  every `*.go` file of the package directory that does not end in `_test.go`, regardless of build constraints):
  - every import path is one of `bufio`, `bytes`, `context`, `errors`, `fmt`, `io`, `io/fs`, `log/slog`, `math/bits`, `os`, `slices`,
    `strconv`, `strings`, `sync`, `syscall`, `time`, `github.com/LarsLaskowski/Vandox/internal/model`,
    `github.com/LarsLaskowski/Vandox/internal/scheduler` (so `C`, `unsafe`, `os/exec`, `net/...`, `plugin`, `golang.org/x/sys/...` and
    any D-Bus package fail);
  - no import carries a name (no renamed, dot or blank import), so a selector `syscall.X` or `os.X` always means that package;
  - of `syscall` only the selectors `Statfs`, `Statfs_t`, `Adjtimex`, `Timex`, `Errno`, `EOPNOTSUPP` are used, of `os` only `DirFS`
    (so `syscall.Syscall*`, `RawSyscall*`, `Socket`, `Connect`, `Sendto`, `Sendmsg`, `Bind`, `Dup2`, `Exec`, `ForkExec`,
    `os.StartProcess` fail);
  - no comment line starts with `//go:` other than `//go:build` (so `//go:linkname`, `//go:cgo_*` fail);
  - the directory holds no source file other than `.go` files (no `.s`, `.c`, `.h`, `.cc`, `.syso`);
  - non-vacuity: at least one non-test file is parsed and the selectors `syscall.Statfs` and `syscall.Adjtimex` are found.
  The test itself is checked once against a scratch copy (Tester, in step 5): a file importing `net`, one using `syscall.Syscall`, one
  with `//go:linkname`, one with `import sc "syscall"` each make it fail. The defaults read the host: `NewSystem(Sources{}, nil)`,
  `NewFilesystem(Sources{}, []string{"/"}, nil)` and `NewClock(Sources{})` collect without error on the test host and record `load1`,
  `filesystem_size_bytes` with `mount=/` and `clock_synchronized`. Tests: `TestSysstats_ImportsOnlyAllowedPackages`,
  `TestNew_DefaultsReadTheHost`.
- [ ] **AC11 — option `filesystem.mount_points`.** Accepted: absent (default `["/"]`), block list, flow list `[/, /var]`, quoted
  elements, an empty list `[]` (no mount point), 16 entries, an entry of 1,024 bytes; the decoded list replaces the default (`[/var]`
  gives exactly `["/var"]`). Refused with exactly `config: <file>:<line>: filesystem.mount_points: must be a list of at most 16
  absolute, clean paths of at most 1024 bytes each (no trailing slash, no . or .. elements), each listed once`: 17 entries, an entry of
  1,025 bytes, `var`, `/var/`, `/var/../etc`, `/./var`, `//var`, an empty string `""`, `- !!str` (an empty string), `- "~"`, `- 1`,
  `- true`, `- 2026-01-01`, `- 0x1F`, `- .inf`, the same path twice. Refused by the decoder before decoding, because go.yaml.in/yaml/v3
  drops a null element of a sequence silently (v3.0.5 `decode.go` 751-754 with `null()` 554-563): a null element fails with exactly
  `config: <file>:<line>: filesystem.mount_points: has no value`, `<line>` the line of the null element — cases (each under
  `filesystem:` with `mount_points:` on line L): `mount_points:` followed by `    - ~` (line L+1), by `    - !!null` (L+1), by an
  empty `    -` element (L+1), by `    - /a` and `    - null` (L+2); `mount_points: [/a, ~, /b]` (L); `mount_points: [/a, null, ""]`
  (L, the null is refused before the `""`). Refused by the existing decoder rules: a scalar `filesystem.mount_points: /`, `""` in place
  of the list and a nested list or mapping (`invalid value, want a list`), a null value of the key itself (`has no value`), the
  double-quoted YAML elements `"/a\\x00"`, `"/a\\u202e"`, `"/a\\ufeff"`, `"/a\\tb"` (written here and in the Go source with a doubled
  backslash, so the YAML text holds one backslash; were it lost, `/ax00` and the like would pass the rule and the test fail) and a
  literal block scalar `|` element (`must not contain control, format or line-separating characters`), an anchor or alias, a `!!binary` or `!!set` tag. Accepted besides: `!!seq [/var]`, `- !!str /var`, a flow list over
  several lines, comments between elements, a folded `>-` element whose value is `/a b`. No error text contains the value.
  `AgentKeys()` ends with `filesystem.mount_points`; the example file sets it to the default. The decoder rule is also tested on the
  white-box document of `decode_test.go` (field `tags`): `tags: [a, ~]`, `tags:\n  - ~`, `tags:\n  -` fail as `has no value` with the
  element's line; `tags: [a, "~"]` decodes to `["a", "~"]`. Tests: `TestLoadAgent_MountPoints`, `TestLoadAgent_MountPointsRejected`,
  `TestLoadAgent_MountPointsNullElement`, `TestDecodeStrict_ListElementWithoutValue`, `TestAgentKeys`, `TestLoadAgent_Example`.
- [ ] **AC12 — the agent runs the collectors.** `jobs` returns, in order, `self` (60 s, 10 s), `system` (30 s, timeout 5 s, pressure
  interval 10 s), `filesystem` (60 s, 10 s) and `clock` (60 s, 5 s); with `filesystem.mount_points: []` the `filesystem` job is left out;
  `scheduler.New` accepts the jobs. `Run` with fixture system sources (`Options.System`) delivers `load1`, `mem_available_bytes`,
  `pressure_avg10`, `filesystem_size_bytes` and `clock_synchronized` to the sink. Tests: `TestJobs_Schedules`,
  `TestRun_RecordsSystemMetrics`.

## Behavior coverage

| spec.md *Behavior* | Covered by |
| ------------------ | ---------- |
| B1 three collectors and their schedules | AC12 |
| B2 `cpu_percent` per mode from the second sample | AC1 (absent in the first), AC2 |
| B3 load averages | AC1, AC5 (grammar) |
| B4 memory and swap as `top`, every meminfo line | AC1, AC3, AC5 |
| B5 PSI values, `cpu` without `full` | AC1, AC4, AC5 |
| B6 PSI unavailable: graceful, logged on change | AC4 |
| B7 vmstat totals and rates | AC1, AC2, AC5 |
| B8 disk counters and rates, device selection | AC1, AC2, AC6 |
| B9 file system usage per mounted configured path, not-mounted logging | AC7 |
| B10 clock synchronized, errors, PLL offset | AC8 |
| B11 malformed or unreadable source fails the sample with a fixed text | AC5, AC6, AC7, AC8 |
| B12 a hanging source costs only its own sample | AC9 |
| B13 no program, D-Bus or socket | AC10 |
| B14 option `filesystem.mount_points` | AC11 |
| B15 absent is not zero | AC2 (no rate), AC3 (swap used), AC4 (PSI), AC7 (unmounted, inodes), AC8 (PLL) |
| B16 metric records, source `system`, wire check | AC1, AC7, AC12 |

## Approach

New package `internal/sysstats`, three collectors that implement `scheduler.Collector`; the behavior is specified in the Agent area,
*System metrics* (written with this plan, `docs/areas/agent.md`), the names in the wire format area (*Metric names*). Points for the Dev:

1. **Reading.** Every file is read through `Sources.Proc` / `Sources.Sys` (`fs.FS`, defaults `os.DirFS("/proc")`, `os.DirFS("/sys")`)
   with a bounded read (limit + 1 bytes, then "larger than"), except `stat` (first line within the first 4,096 bytes) and
   `self/mountinfo` (line by line with a `bufio.Reader`, each line at most 65,536 bytes, the escape decoding on bytes, not on UTF-8).
   Numbers are checked as ASCII digit strings of the allowed length before `strconv.ParseUint`; no `fmt.Sscanf`, no regular expression on
   file content.
2. **State.** `System` keeps the previous successful reading (CPU counters, vmstat counters, disk counters by device, and the run
   start); `System` and `Filesystem` keep the last logged availability (unavailable PSI resources; not-mounted paths). State is read at the
   start and replaced at the end of a successful `Collect`, under a `sync.Mutex` held only for that read and that write, never across
   I/O; availability is logged only from a successful sample. Rates use `run.Start` (seconds between the two starts), never the clock.
3. **PSI unavailable** = an `Open` or read error that matches `fs.ErrNotExist` or `syscall.EOPNOTSUPP` (`errors.Is`); every other error
   fails the sample.
4. **Disks**: `fs.ReadDir(Sys, "block")`, names in order, keep an entry when `fs.Stat(Sys, "block/<name>/device")` succeeds, at most
   `MaxDisks`; then read `block/<name>/stat`. A `Stat` error other than not-exist fails the sample.
5. **Mounted**: a configured path is mounted when it equals a decoded mount point (field 5) of `self/mountinfo`; the read stops early
   only at end of file (every line is checked).
6. **System calls**: `StatFS` wraps `syscall.Statfs` (fragment size, the block size when it is 0; a negative size is an error) and
   returns the bare `syscall.Errno` (its text is "permission denied" and the like, without the path); `Filesystem.Collect` wraps it as
   `sysstats: filesystem.mount_points[<i>]: statfs: %w`. `Filesystem.Collect` checks `BlocksFree` ≤ `Blocks`, `BlocksAvailable` ≤
   `Blocks` and `InodesFree` ≤ `Inodes` (else `statfs: free above total`) and computes `Blocks` × `BlockSize` with `bits.Mul64` (a
   non-zero high word is `statfs: size above 64 bits`); the free and available products then cannot overflow.
   `ReadClock` calls `syscall.Adjtimex` with a zero `Timex` (modes 0, changes nothing): `MaxError` = `Maxerror` µs, `EstimatedError` =
   `Esterror` µs, `PLLOffset` = `Offset` in ns when `Status & 0x2000` (`STA_NANO`) else µs, `PLL` = `Status & 0x0001` (`STA_PLL`); its
   error is `sysstats: adjtimex: %w`.
7. **Logging**: the collectors take a `*slog.Logger` (nil logs nothing, `slog.New(slog.DiscardHandler)`); attributes `collector`,
   `resources`, `mount` as named in the area; the configured path is only ever an attribute value.
8. **Agent**: `jobs` builds the four jobs; `newScheduler` takes the loaded configuration; `Options.System` passes test sources.
9. **Config**: `Agent.Filesystem` with `MountPoints []string` (`yaml:"mount_points"`), default `[]string{"/"}` (a new slice per
   `DefaultAgent` call), checked by `checkMountPoints` after `log.level`.
10. **Decoder** (`internal/config/decode.go`, `setOther`): after `checkTree` and before `v.Decode`, when `v` is a sequence node, an
    element `e` with `isNull(e)` fails with `&KeyError{File: d.file, Line: e.Line, Key: path, Reason: "has no value"}` — the element's
    line, as `checkTree` reports an element's anchor or tag. This holds for every list option, today only `filesystem.mount_points`.
    No count check after `Decode`: in yaml.v3 v3.0.5 the only silent drop is `null()` returning false for a non-pointer element
    (`decode.go` 554-563); every other false return of `unmarshal` records a type error that `Decode` returns, so the check would be
    unreachable code.

Settled by measurement (Security, plan review, `log.md` 2026-10-17 18:33):

- *U1, measured*: unprivileged, without capabilities, `syscall.Adjtimex(&syscall.Timex{})` succeeds and changes nothing, while modes
  `ADJ_MAXERROR` return `EPERM` without `CAP_SYS_TIME`; `syscall.Statfs` works unprivileged; `Statfs` of a missing path returns a bare
  `syscall.Errno` whose text does not contain the path. Record 0111 and the Agent area (*Rights*) cite this; the Dev runs no
  measurement.

Unverified claims:

- *U2, unverified*: the sign and meaning of the kernel's remaining PLL offset (the correction still to apply). Not measurable here (no
  time daemon in the session); the record and the area say only what adjtimex reports and do not repeat a sign interpretation as fact.
- *U3, unverified*: the 5.15 kernel of the server prints the same grammars as the captured 6.18 (the field set of `meminfo` differs,
  which the parser does not depend on). Check: the parallel run of #45; named in record 0111.

Verified by reading (no measurement needed): `ntp_synced` in systemd v249 `timedated.c`; `manager_adjust_clock` in v249
`timesyncd-manager.c`; `set_sync_status` and `initialise_timex` in chrony 4.2 `sys_timex.c`; `@clock`, `@system-service` and
`apply_protect_clock` in systemd v249.

**Real samples** the parsers are built from (captured from the session's kernel, Linux 6.18, by the Tester as task 1; the plan was built
from the same capture made during planning):

| Fixture | Content | Variants it covers |
| ------- | ------- | ------------------ |
| `testdata/proc/stat` | `cp /proc/stat` | `cpu` line with 10 counters, per-CPU lines, long `intr` line after it |
| `testdata/proc/loadavg` | `cp /proc/loadavg` | three loads and the task fields |
| `testdata/proc/meminfo` | `cp /proc/meminfo` | 54 lines of 6.18, `kB` lines and lines without unit (`HugePages_*`) |
| `testdata/proc/vmstat` | `cp /proc/vmstat` | the four counters among about 190 others |
| `testdata/proc/pressure/{memory,cpu,io}` | `cp /proc/pressure/*` | `some` and `full` in each file, `cpu` with `full` |
| `testdata/proc/self/mountinfo` | the lines of `/proc/self/mountinfo` whose mount point is `/proc`, `/sys`, `/dev`, `/` or `/sys/fs/cgroup`, unchanged | real line shape, no escapes |
| `testdata/sys/block/vda/stat`, `testdata/sys/block/vda/device/uevent` | `cp` of the session's files | a disk with a `device` entry, 17 fields |
| `testdata/sys/block/loop0/stat` | `cp /sys/block/loop0/stat` | a block device without `device` entry |

In-memory variants (`fstest.MapFS`, built from the fixture text): PSI directory or one file missing, a read error wrapping `EOPNOTSUPP`
(a small `fs.FS` wrapper), `cpu` pressure without `full` (before 5.13), block `stat` of 11 fields (before 4.18) and 15 (before 5.5),
`cpu` line of exactly 8 counters, mountinfo lines with the four escapes, the malformed cases of AC5–AC7, 20 disks, raised counters for
the second sample.

## Options considered

Weighed in record 0111 (*Options considered*), in short:

1. One collector / one per file / **three by source** (chosen: a hung `statfs` loses only its own sample).
2. External programs, D-Bus, chrony's command port / **read-only `statfs` and `adjtimex`** (chosen: no program, no destination).
3. `STA_UNSYNC` / **maximum error < 16 s** (chosen: chrony keeps the flag set without `rtcsync`).
4. Daemon offset / PLL offset always / **PLL offset only while the PLL is on** (chosen: no false zero under chrony).
5. `meminfo` subset / **every field, 128 lines** (chosen: parity with the legacy script for #45).
6. `statfs` alone, device comparison, pid 1's namespace / **own `self/mountinfo`** (chosen: detects unmounted paths, no capability).
7. Configured devices, all of `diskstats` / **`/sys/block` entries with `device`, at most 16** (chosen).
8. Rates in the backend / **rates in the agent, raw counters kept** (chosen: as the legacy parser).
9. Partial `system` samples / **a broken file fails the sample** (chosen: the frame cannot record a partial loss).
10. Missing PSI as error or gap / **omitted, logged on change** (chosen).
11. Null list elements: dropped as yaml.v3 does / a count check after decoding / **refused per element before decoding** (chosen: names
    the element's line; the count check is unreachable with the pinned library, see *Approach* 10).
12. Import check: a deny-list of program and network packages / **an allow-list of packages and `syscall`/`os` selectors** (chosen: a
    deny-list misses raw system calls, cgo, `unsafe`, `//go:linkname` and indirect modules such as `golang.org/x/sys/unix`).

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Go agent | `internal/sysstats/sysstats.go` (new) | package doc, `Source`, collector names, `MaxDisks`, `Sources`, defaults, bounded read and digit helpers, metric record helper |
| Go agent | `internal/sysstats/system.go` (new) | `System`, `NewSystem`, `Name`, `Collect`, sample state, record order, rates |
| Go agent | `internal/sysstats/cpu.go` (new) | `/proc/stat` first line, `/proc/loadavg` |
| Go agent | `internal/sysstats/memory.go` (new) | `/proc/meminfo` with the derived values, `/proc/vmstat` |
| Go agent | `internal/sysstats/pressure.go` (new) | `/proc/pressure/*`, availability |
| Go agent | `internal/sysstats/disk.go` (new) | `/sys/block` selection and `stat` |
| Go agent | `internal/sysstats/filesystem.go` (new) | `Filesystem`, `NewFilesystem`, `FSStats`, `StatFS` |
| Go agent | `internal/sysstats/mountinfo.go` (new) | line reader and escape decoding of `self/mountinfo` |
| Go agent | `internal/sysstats/clock.go` (new) | `Clock`, `NewClock`, `ClockState`, `ReadClock` |
| Go agent | `internal/config/agent.go` | `Filesystem` section, default, `AgentKeys`, `checkMountPoints` (read and found to need only these additions) |
| Go agent | `internal/config/decode.go` | `setOther`: a null element of a sequence fails with `has no value` before `Decode` (*Approach* 10); nothing else changes |
| Go agent | `internal/agent/agent.go` | schedule constants, `Options.System`, `jobs`, `newScheduler(opts, cfg, log)` |
| Deploy | `deploy/agent/agent.yaml` | `filesystem.mount_points` at its default, commented |
| Test data | `testdata/proc/…`, `testdata/sys/…` (new) | real captures, see *Real samples* |

`internal/scheduler`, `internal/model`, `internal/wire` and the C# side are unchanged: metric is an existing kind, the names are a
convention of the wire format area, the golden batch does not change.

## Signatures (for the Dev's skeleton)

```go
// Package sysstats collects the system metrics of the monitored server from /proc, /sys and the read-only system calls statfs
// and adjtimex (record 0111).
package sysstats

// Source is the source of every record of the system collectors.
const Source = "system"

// Collector names.
const (
	SystemName     = "system"
	FilesystemName = "filesystem"
	ClockName      = "clock"
)

// MaxDisks is the most disks a system sample records.
const MaxDisks = 16

// Sources are what the collectors read; a nil field takes its default.
type Sources struct {
	Proc   fs.FS                              // default os.DirFS("/proc")
	Sys    fs.FS                              // default os.DirFS("/sys")
	StatFS func(path string) (FSStats, error) // default StatFS
	Clock  func() (ClockState, error)         // default ReadClock
}

// FSStats is what statfs reports about a file system; the counts are in units of BlockSize.
type FSStats struct {
	BlockSize       uint64
	Blocks          uint64
	BlocksFree      uint64
	BlocksAvailable uint64
	Inodes          uint64
	InodesFree      uint64
}

// StatFS returns the statistics of the file system that holds path; its error is the bare errno, without the path.
func StatFS(path string) (FSStats, error)

// ClockState is the kernel's clock discipline as adjtimex reports it.
type ClockState struct {
	MaxError       time.Duration // maximum error
	EstimatedError time.Duration // estimated error
	PLLOffset      time.Duration // the correction the phase-locked loop still has to apply
	PLL            bool          // whether the phase-locked loop is on
}

// ReadClock reads the kernel's clock discipline with adjtimex without changing it.
func ReadClock() (ClockState, error)

// System collects CPU, load, memory, pressure, vmstat and disk metrics; it implements scheduler.Collector.
type System struct { /* src, log, mu, previous reading, logged availability */ }
func NewSystem(src Sources, log *slog.Logger) *System
func (c *System) Name() string
func (c *System) Collect(ctx context.Context, run scheduler.Run) ([]model.Record, error)

// Filesystem collects the use of the configured mount points; it implements scheduler.Collector.
type Filesystem struct { /* src, mount points, log, mu, logged not-mounted paths */ }
func NewFilesystem(src Sources, mountPoints []string, log *slog.Logger) *Filesystem
func (c *Filesystem) Name() string
func (c *Filesystem) Collect(ctx context.Context, run scheduler.Run) ([]model.Record, error)

// Clock collects the clock synchronization; it implements scheduler.Collector.
type Clock struct { /* src */ }
func NewClock(src Sources) *Clock
func (c *Clock) Name() string
func (c *Clock) Collect(ctx context.Context, run scheduler.Run) ([]model.Record, error)
```

`NewFilesystem` copies `mountPoints`. Unexported helpers are the Dev's choice.

```go
// internal/config
type Agent struct {
	AgentID    string        `yaml:"agent_id"`
	Backend    BackendTarget `yaml:"backend"`
	Spool      Spool         `yaml:"spool"`
	Log        Log           `yaml:"log"`
	Filesystem Filesystem    `yaml:"filesystem"`
	Secrets    AgentSecrets  `yaml:"-"`
}

// Filesystem configures the file system metrics.
type Filesystem struct {
	MountPoints []string `yaml:"mount_points"`
}

// Bounds of filesystem.mount_points.
const (
	MaxMountPoints     = 16
	MaxMountPointBytes = 1024
)

func AgentKeys() []string // agent_id, backend.url, spool.directory, log.level, filesystem.mount_points
func checkMountPoints(file string, lines map[string]int, key string, values []string) error
```

```go
// internal/agent
const (
	SystemInterval         = 30 * time.Second
	SystemPressureInterval = 10 * time.Second
	SystemTimeout          = 5 * time.Second
	FilesystemInterval     = 60 * time.Second
	FilesystemTimeout      = 10 * time.Second
	ClockInterval          = 60 * time.Second
	ClockTimeout           = 5 * time.Second
)

type Options struct {
	ConfigPath string
	Environ    []string
	Stderr     io.Writer
	Sink       scheduler.Putter
	Self       selfstats.Sources
	System     sysstats.Sources // the system metrics sources; zero fields take their defaults
}

func jobs(opts Options, cfg *config.Agent, log *slog.Logger) []scheduler.Job
func newScheduler(opts Options, cfg *config.Agent, log *slog.Logger) (*scheduler.Scheduler, error) // was (opts, log)
```

## Test files

- `internal/sysstats/sysstats_test.go` — shared helpers and doubles (fixture tree, `fstest.MapFS` variants, the blocking `fs.FS` wrapper,
  the `EOPNOTSUPP` wrapper, a recording `slog.Handler`, fake `StatFS` and clock sources incl. blocking ones), AC10
  (`TestSysstats_ImportsOnlyAllowedPackages`, `TestNew_DefaultsReadTheHost`).
- `internal/sysstats/system_test.go` — AC1, AC2, AC5 (its three tests span every source of the `system` sample), AC9 (`System`).
- `internal/sysstats/memory_test.go` — AC3.
- `internal/sysstats/pressure_test.go` — AC4.
- `internal/sysstats/disk_test.go` — AC6.
- `internal/sysstats/filesystem_test.go` — AC7 (records, not mounted, errors), AC9 (`Filesystem`), `TestStatFS_ReadsTheRootFilesystem`.
- `internal/sysstats/mountinfo_test.go` — `TestFilesystem_MountinfoEscapes`.
- `internal/sysstats/clock_test.go` — AC8, AC9 (`Clock`).
- `internal/config/agent_test.go` — AC11 (`TestLoadAgent_MountPoints`, `TestLoadAgent_MountPointsRejected`,
  `TestLoadAgent_MountPointsNullElement`, `TestAgentKeys`, `TestLoadAgent_Example`).
- `internal/config/decode_test.go` — AC11 (`TestDecodeStrict_ListElementWithoutValue`, on the existing `testDoc.Tags`).
- `internal/agent/agent_test.go` — AC12.

`cpu.go` has no test file of its own: its grammar is exercised through `System.Collect` in `system_test.go` (AC1, AC2, AC5).

Existing test code affected:

- `newScheduler` changes signature: its only caller is `Run` (`internal/agent/agent.go:49`); no test calls it. Dev.
- `Options` gains `System`: the existing `startAgent(t, Options{…})` calls in `internal/agent/agent_test.go` still compile; the Tester
  adds `System: fixtureSystem()` to each of them in step 5 so no agent test reads the host (no assertion touched).
- `TestAgentKeys` (`internal/config/agent_test.go:83`) gets the new key in `want`; `TestLoadAgent_Example` compares the new section with
  `slices.Equal` (a struct with a slice is not comparable with `!=`). Tester.

Files of the change no unit test can cover: none (`StatFS` and `ReadClock` are exercised against the real kernel by AC8).

## Areas

- **Agent** (`docs/areas/agent.md`): status line, new section *System metrics*, *Rights* (no right needed; the unit must allow
  `adjtimex`), *Related decisions* (0111), *Implementation*. Written by the Lead with this plan; the Dev reports any deviation of the
  code from it instead of editing the text.
- **Wire format** (`docs/areas/wire-format.md`): *Metric names* rows `disk_*`, `filesystem_*`, `clock_*` and the source `system`. Written
  by the Lead with this plan.
- **Configuration and secrets** (`docs/areas/configuration-and-secrets.md`): the option row and its rule paragraph with the exact error
  text of AC11. Dev.

## Documentation updates

| File | Change | Owner |
| ---- | ------ | ----- |
| `docs/areas/agent.md` | as *Areas* | Lead (done) |
| `docs/areas/wire-format.md` | as *Areas* | Lead (done) |
| `docs/areas/configuration-and-secrets.md` | row `filesystem.mount_points` (default `/`, rule) and a paragraph: list of at most 16 absolute, clean, distinct paths of at most 1,024 bytes; the error text of AC11; the values must be mount points of the agent's namespace to be recorded (link to the Agent area); in *Configuration file*, the error bullet "a leaf option with no value" becomes "a leaf option or an element of a list with no value (the agent; a null element is refused, never dropped)" | Dev |
| `README.md` | *Agent options* row `filesystem.mount_points` | Dev |
| `deploy/agent/agent.yaml` | a `filesystem:` section with `mount_points: [/]`, commented like the others | Dev |
| `docs/ARCHITECTURE.md` | line 13-14 "(so far only its self-metrics)" becomes "(its self-metrics and the system metrics)"; the `internal/` list names "the system metrics (`internal/sysstats`)"; the agent bullet of *Components* links 0111 after 0110 | Dev |
| `.squad/project.md` | area 6: the unit's filter allows `adjtimex` (0111); area 10: the readers of `internal/sysstats` and their bounds (stat first line 4 KiB, loadavg 256 B, meminfo 16 KiB / 128 lines, vmstat 64 KiB, pressure 256 B, block stat 1 KiB, mountinfo lines 64 KiB streamed, at most 16 disks and 16 mount points, digits checked before conversion, statfs products checked for 64-bit overflow, no file content in errors), the allow-list import test of `internal/sysstats` (AC10), and the option `filesystem.mount_points` (`checkMountPoints`, null list elements refused in `decode.go`), records list + 0111; *Test doubles*: `/proc` row — the blocking `fs.FS` wrapper and the `EOPNOTSUPP` wrapper implemented in `internal/sysstats`; `/sys` row implemented (`testdata/sys/`); a new row *statfs and adjtimex*: fake `StatFS` and clock functions with scripted results and blocking variants (implemented, `internal/sysstats`) | Dev |
| `.squad/stack.md` | *Layout*: `internal/` list gains `sysstats` | Dev |

## Architecture check

- **No data gaps unless explicitly recorded** (0028): every failed or abandoned sample is a gap through the frame; what the host does not
  offer is left out and documented as unknown (Agent area, *Absent is not zero*), not a lost sample. Preserved.
- **A hanging collector never blocks the agent** (0029): the three collectors run as their own jobs; `statfs` (which can hang on a stale
  network mount) and `adjtimex` sit in collectors of their own; AC9 tests each. Preserved.
- **Outbound only** (0006) and security area 11: no socket, no D-Bus, no program (AC10). Preserved.
- **Agent rights** (0030): no new group or capability; the constraint on the unit's filter (`adjtimex`) is written down for #42. The
  2 GB budget (0025): bounded reads, about 120 small records every 30 s.
- `docs/ARCHITECTURE.md` changes only in its status sentence and component list (Dev, above); no guarantee or flow changes.

## Security considerations

- [x] Limits: every byte limit applies to the raw bytes read from the file, before any decoding; the only decoding is the mountinfo
  escape, which shrinks a line (4 bytes to 1), so the 64 KiB line limit before decoding bounds the decoded line too. The 1,024-byte
  bound of a mount point applies to the UTF-8 bytes of the value after YAML decoding (it becomes a label value, a short text of at most
  1,024 bytes).
- [x] Errors that reach a user-visible text: the configuration error of AC11 (exact text, no value); the collector errors reach the log
  through the scheduler's `collector failed` line (`err` attribute) with the fixed texts of AC5–AC8, which carry no file content and no
  configured path (a mount point is named by its index); the fs error texts wrapped for I/O failures name only the relative file name the
  collector opened.
- [x] Accepted forms of the guard on `filesystem.mount_points` (YAML forms the go.yaml.in/yaml/v3 decoder turns into `[]string`, and the
  outcome for each; probed by Security with the real `decodeStrict`): block and flow sequences (also over several lines, with comments,
  with `!!seq`), plain, single- and double-quoted scalars, `!!str` elements and folded `>-` scalars (accepted when the value passes the
  rule); scalars that would resolve to another core tag (`1`, `true`, `2026-01-01`, `0x1F`, `.inf`) decode to their text and fail the
  rule as not absolute; a **null element** (`~`, `null`, `!!null`, an empty `-`), which yaml.v3 would drop silently, is refused by
  `setOther` before decoding as `has no value` (*Approach* 10), so the decoded list always has one entry per element and an all-null
  list can never turn into `[]` and silently drop the `filesystem` job; a scalar (also `""`) or mapping in place of the list, a nested
  sequence or mapping as an element fail as `invalid value, want a list`; a null value of the key fails as `has no value`; anchors,
  aliases, merge keys and non-core tags (`!!binary`, `!!set`) are refused by `checkTree` before decoding; Cc/Cf/Zl/Zp characters
  (also a literal `|` element's line feed) are refused per element after decoding; escapes in double-quoted YAML (`"/a\\x00"`,
  `"/a\\u202e"`, `"/a\\ufeff"`) are decoded by YAML first and then refused by the character rule. The rule itself: `filepath.IsAbs`,
  `filepath.Clean(v) == v`, 1 to 1,024 bytes, at most 16 entries, no two equal.
- [x] No program, D-Bus or socket: an allow-list of imports and of `syscall`/`os` selectors, no named imports, no `//go:` directive
  but `//go:build`, no non-Go source file (AC10), so cgo, `unsafe`, `//go:linkname`, raw system calls and `golang.org/x/sys/unix` fail
  the test; a deny-list was rejected (*Options considered* 12).
- [x] `statfs` values from FUSE, NFS or CIFS are untrusted numbers: free and available above the total and a size above 64 bits fail
  the sample with fixed texts (AC7), never a wrapped-around value.
- [x] Mount table text is kernel output that local users influence (FUSE mount names): it is never logged, never put into a record (the
  label is the configured path) and never echoed in an error; the decoding works on bytes, so invalid UTF-8 or raw control characters in a
  mount point simply never match a configured path. A line above 64 KiB fails every `filesystem` sample while it exists (a recorded
  gap, fail closed; Agent area).
- [x] Disk names from `/sys/block` are kernel device names (at most 32 bytes) and become label values; the scheduler's wire check drops a
  record whose label the wire format refuses (gap with cause `unknown`).

## Decision records

- `docs/decisions/0111-system-metrics-three-collectors-from-proc-sys-statfs-and-adjtimex.md` (Proposed), index row added.

## Challenge

- **Devil's Advocate, minor: disk selection drops `dm-*`, `md*`, `loop*`; state the limit in the Agent area so #45's legacy-parity
  check does not read the missing device-mapper or RAID I/O as a defect.** Accepted. The Agent area is the Lead's document in this
  change, so the Lead wrote the sentence now (*System metrics*, *Disks*: the I/O of a device-mapper, RAID or loop volume appears only
  as that of its member disks, and #45 compares per disk). No code or criterion changes (AC6 already asserts the selection).

## Security review

- **B1 (blocking): null elements of `filesystem.mount_points` pass the guard.** Accepted. Root cause confirmed by reading:
  go.yaml.in/yaml/v3 v3.0.5 `sequence` (`decode.go` 751-754) keeps an element only when `unmarshal` returns true, and `null()`
  (554-563) returns false for a string element, so `internal/config/decode.go` `setOther` (219-238) sees the shortened slice.
  Revision: `setOther` refuses a null element of a sequence before `Decode` with `has no value` and the element's line (*Approach* 10,
  `decode.go` added to *Affected projects and types*); AC11 lists `- ~`, `- !!null`, an empty `-`, `[/a, ~, /b]`, `- /a` + `- null`
  and `[/a, null, ""]` with exact texts and lines, plus a white-box `TestDecodeStrict_ListElementWithoutValue`; the *Security
  considerations* bullet no longer claims `~` decodes to text. The whole list of accepted forms was re-checked against Security's probe:
  every accepted and refused form it reports is now named in AC11 or the bullet (`!!seq`, `!!str`, multi-line flow, comments, `>-`
  accepted; `|`, `\x00`, U+202E, U+FEFF, `!!set`, anchors, scalar or `""` in place of the list, nested list or mapping refused; `1`,
  `true`, `2026-01-01`, `0x1F`, `.inf` fail as not absolute). A count check after decoding was weighed and left out as unreachable
  (*Options considered* 11).
- **B2 (blocking): the import check is a deny-list.** Accepted. AC10 is now an allow-list (`TestSysstats_ImportsOnlyAllowedPackages`):
  Security's package set plus `math/bits` (pure arithmetic, needed for the overflow check of N1); `syscall` limited to `Statfs`,
  `Statfs_t`, `Adjtimex`, `Timex`, `Errno`, `EOPNOTSUPP`; additionally `os` limited to `DirFS` (since `os.StartProcess` is in an
  allowed package), no named imports (a renamed `syscall` would escape the selector check), no `//go:` directive but `//go:build`, no
  non-Go source file (assembly or cgo objects), and a non-vacuity check. *Options considered* 12 records the rejected deny-list.
- **N1 (non-blocking): `statfs` size overflow.** Accepted: `size above 64 bits` with `bits.Mul64`, available above the total joins
  `free above total`, cases in AC7, *Approach* 6.
- **N2 (non-blocking): a mountinfo line over 64 KiB.** Accepted: the Agent area states that such a line fails every `filesystem`
  sample while the mount exists (a gap each time, fail closed), so a local user able to create such a mount can suppress the file
  system values only visibly.
- **N3 (non-blocking): record 0111 states the PLL offset's sign as fact.** Accepted: the Consequences bullet now says the sign is
  reported as the kernel gives it and its interpretation is unmeasured (U2), as the plan does.
- **U1**: settled by Security's measurement and cited (*Approach*, record 0111, Agent area *Rights*); the Dev's measurement task is
  removed from `tasks.md`.

## Out of scope / follow-ups

- Switching memory pressure mode on PSI or `MemAvailable` thresholds: #32 (its requirement); the `system` job already has a pressure
  interval.
- The batch header's `clock_offset_ns` from the clock values: #38/#39 (0046).
- Allowing `adjtimex` in the unit's filter and not using `ProtectClock=`: #42 (recorded in 0111 and the Agent area).
- The clock offset under chrony, `/proc/pressure/irq`, per-CPU and network interface counters: not requested.
