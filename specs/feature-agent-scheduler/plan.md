# Plan: Agent skeleton and collector scheduler

Source: Issue #30 | [spec.md](spec.md)
Status: Draft
Tier: security — the change gives the agent its first running entry point, which parses a new command-line flag and
the systemd environment (security area 10), holds the agent token in a running process that now logs (areas 8 and 12)
and writes to a new local destination, the systemd notification socket (area 11).

## Problem / root cause

Summary of [spec.md](spec.md): the agent needs the runtime frame its collectors plug into — configuration, journal
logging, an isolating scheduler with gaps, a memory pressure switch, self-metrics and systemd notification.

Claims of the issue checked against the code (base `origin/main`, c24fc4a):

- "Configuration via the agent configuration package (`internal/config`)" — **confirmed**: `config.LoadAgent(path,
  environ)` (`internal/config/agent.go:61`) loads `agent_id`, `backend.url`, `spool.directory`, `log.level` and the token
  `VANDOX_AGENT_TOKEN`; nothing calls it yet (`cmd/vandox-agent/main.go:19` only calls `cli.Run`, which knows only
  `-version`, `internal/cli/cli.go:17`). `log.level` is validated to `debug|info|warn|error` (`internal/config/config.go:122`).
- "structured logging to the journal" — nothing exists; no logging in the agent today.
- "each collector has its own interval and timeout and runs isolated; a hanging or panicking collector never blocks the
  others (recover, report as event)" — nothing exists; it is the guarantee of record 0029 and the *Collecting* rules of
  `docs/areas/agent.md`. "Report as event": the data model has no agent event record kind (`internal/model/model.go:35`,
  kinds are metric … gap), and `docs/areas/wire-format.md` (*Producer size contract*) already prescribes for a lost
  sample a `gap` with cause `unknown` and the collector's name. The plan reads "event" as: an error line in the agent's log
  plus a gap record (cause `unknown`) — no new record kind, no wire change.
- "Adaptive mode hook … shorter interval when memory pressure is high (used by process/network collectors)" — confirmed
  as a dependency of #32 ("Adaptive mode: shorter interval and full snapshot …"); the hook is a switch on the scheduler
  plus a flag every run sees, the thresholds stay with #31/#32.
- "Self-metrics: own RSS, CPU, goroutines, spool size, send lag – stored like any other metric" — RSS, CPU and goroutines
  can be read now; **spool size and send lag cannot**: the spool (#38) and the sender (#39) do not exist (#38 itself lists
  "Spool size and age are self-metrics"). The plan defines the two metrics and the provider interfaces; they are emitted
  once #38/#39 wire a provider. "Stored like any other metric": there is no spool yet, so "stored" means handed to the
  record sink as `metric` records (see *Approach*, sink).
- "systemd notify: `READY=1` and periodic `WATCHDOG=1`" — nothing exists; no systemd unit exists either
  (`deploy/agent/` holds only `agent.yaml`); the unit with `Type=notify` and `WatchdogSec=` is #42.
- Dependencies #10, #11 — **confirmed** closed and implemented (`internal/model`, `internal/wire`, `internal/config`).

Related defects found on the way (fixed in this change, small and in files it touches anyway):

- `deploy/agent/agent.yaml:3` says "the binary does not read this file yet"; `docs/areas/configuration-and-secrets.md:14`,
  `docs/ARCHITECTURE.md:355` (Configuration) and `docs/CONTRIBUTING.md:31` say the same — all become untrue with this
  change.
- `internal/cli/cli_test.go` still tests the flag parser with the binary name `vandoxd`, although `vandoxd` is .NET and
  parses its own flags (the package comment says so). The rewritten tests use only `vandox-agent`.

Related defect found on the way, **not** fixed here (follow-up issue, see *Out of scope*): `internal/config/config.go:75`
checks the file type with `os.Stat` and then opens the path with `os.Open` (line 82); a FIFO put in place between the two
calls blocks the open. `docs/areas/configuration-and-secrets.md:15` says "a FIFO cannot block start-up", which claims more
than the code does; the sentence is reworded here to what holds ("the type is checked before the file is opened"). The
file lives in the root-owned `/etc/vandox`, so only root can race it.

## Acceptance criteria

All Go. Timeout and interval behavior is tested inside `testing/synctest` bubbles (record 0062; no network involved);
the tests that use a real unix datagram socket follow 0062's real-timer rules (a duration that must fire is about 50 ms,
waits are on channels or received datagrams, no `time.Sleep` to wait for a state, no elapsed-time assertion). In the
synctest tests the bubble's clock starts at 2000-01-01T00:00:00Z; times below are offsets from the moment
`Scheduler.Run` is called. `t.Run` cannot be called inside a bubble, so a table-driven synctest test calls
`synctest.Test` inside each `t.Run` subtest.

**Scheduler (`internal/scheduler`)**

- [ ] AC1 (issue: a hanging collector does not delay others): jobs `fast` (interval 10 s, timeout 2 s) and `hang`
  (interval 10 s, timeout 2 s, `Collect` blocks until `t.Cleanup` releases it, ignoring its context). Over 60 s,
  `fast` runs with `Run.Start` exactly 0, 10, 20, 30, 40, 50 and 60 s and each of its samples reaches the sink at its
  start time; `hang.Collect` is called exactly once. — `TestScheduler_HangingCollectorDoesNotDelayOthers`
- [ ] AC2 (timeout gap): in the setup of AC1 the sink receives, at 2 s, one record: origin `agent`, source `agent`,
  seq 0, `captured_at` 2 s, a `gap` with cause `collector_timeout`, collector `hang`, from 0 s, to 10 s. One warning
  line "collector timed out" with `collector=hang` is logged for the stuck episode, not one per cycle.
  — `TestScheduler_TimeoutRecordsGap`
- [ ] AC3 (stuck collector skipped, each skip a gap): while `hang` is stuck, at 10, 20 and 30 s the sink receives one
  `collector_timeout` gap each, from that due time to due + 10 s, `captured_at` equal to the due time, and `Collect` is
  not called again. — `TestScheduler_StuckCollectorSkipsCyclesWithGaps`
- [ ] AC4 (late result discarded, collector resumes): `hang` blocks on its first call until 25 s and returns one metric
  record, then returns normally. The late record never reaches the sink; the cycles at 10 and 20 s are skip gaps; the
  cycle at 30 s runs (`Collect` called the second time with `Run.Start` 30 s) and its record reaches the sink; one info
  line "collector returned after its deadline" with `collector=hang` and `skipped=2` is logged at 30 s.
  — `TestScheduler_LateResultDiscardedAndCollectorResumes`
- [ ] AC5 (panic recovered): a collector that panics with the value `"boom"` on its first run and succeeds afterwards:
  the sink receives a gap with cause `unknown`, collector set, from 0 s, to one interval; one error line "collector
  panicked" with the collector's name and `panic=boom` and a `stack` attribute is logged; a second, healthy job keeps its
  times; the panicking collector runs again at its next due time and its record arrives.
  — `TestScheduler_PanicRecoveredAsGap`; `PanicError.Error()` returns `scheduler: collector panicked: boom`, and a
  value whose `fmt.Sprint` is longer than 1,024 bytes is cut to its first 1,024 bytes — `TestPanicError_Error`
- [ ] AC6 (collector error): a collector that returns one metric record **and** an error: the record is dropped, a gap
  (cause `unknown`, collector set, from the due time to one interval later) is put; three failing runs in a row log one
  warning "collector failed" (at the first) and the next successful run logs one info line "collector recovered" with
  `failed_runs=3`. — `TestScheduler_CollectorErrorRecordsGap`
- [ ] AC7 (record metadata): a collector returns two records, one with zero `CapturedAt` and one with a set UTC
  `CapturedAt`, both with `Origin` `import` and `Seq` 7: the sink receives both with origin `agent` and seq 0, the first
  with `captured_at` equal to `Run.Start`, the second unchanged, `Source` as the collector set it.
  — `TestScheduler_RecordsGetAgentMetadata`
- [ ] AC8 (invalid record): a collector returns three records, the second a metric with a NaN value: the sink receives
  the first and third and one gap (cause `unknown`, collector set, from the due time to one interval later); one warning
  "record refused" names the collector. Table cases for the refusal: NaN value (validation error), a `log_line` with a
  non-UTC `captured_at`, and a `process_snapshot` whose encoded line exceeds `wire.DefaultLimits().MaxLineBytes`
  (`*wire.RecordSizeError`). — `TestScheduler_InvalidRecordReplacedByGap`
- [ ] AC9 (memory pressure, table of synctest subtests): job `adaptive` (interval 60 s, timeout 5 s, pressure interval
  10 s) and job `plain` (interval 60 s, timeout 5 s, no pressure interval).
  (a) `SetPressure(true)` at 15 s, `SetPressure(false)` at 40 s: `adaptive` starts at 0, 15, 25, 35, 95 s with
  `Run.Pressure` false, true, true, true, false; `plain` starts at 0 and 60 s with `Run.Pressure` false and false (60 s is
  after the switch back). A second `SetPressure(true)` while already on changes nothing.
  (b) `SetPressure(true)` at 45 s (the switch comes long after `0 + 10 s` has passed): `adaptive` starts at 0, 45, 55,
  65 s with `Run.Pressure` false, true, true, true, and the sink receives **no gap** (the switch re-anchors, it is never
  late).
  (c) `SetPressure(true)` at 3 s, no blocking: `adaptive`'s second run starts at 10 s (`0 + 10 s`, later than the switch),
  not at 3 s.
  (d) `adaptive` alone, the sink's `Put` of the 0 s run sleeps until 26 s, `SetPressure(true)` at 24 s: the second run
  starts at 26 s with `Run.Pressure` true, the third at 34 s (`24 + 10 s`), and the sink receives no gap.
  (e) `adaptive` alone, the sink's `Put` of the 0 s run sleeps until 50 s, `SetPressure(true)` at 24 s: the sink receives
  one gap (cause `unknown`, collector `adaptive`) from 24 s to 50 s, and the second run starts at 50 s.
  — `TestScheduler_PressureShortensInterval`
- [ ] AC10 (late wake-up): job interval 10 s, timeout 2 s; the sink's first `Put` blocks for 30 s (a `time.Sleep` inside
  the bubble). After it returns at 30 s the sink receives a gap with cause `unknown`, collector set, from 10 s to 30 s,
  and the next run starts at 30 s (not 10 or 20 s), then 40 s. A `Put` that returns at 15 s (inside the next slot) causes
  no such gap and the next run starts at once at 15 s, the one after at 20 s. — `TestScheduler_LateWakeUpRecordsGap`
- [ ] AC11 (liveness): job interval 10 s, timeout 2 s, the sink's first `Put` blocks until the test releases it.
  `Healthy` at 31 s is true, at 32 s true, at 32 s + 1 ns false (deadline = due 0 + timeout 2 s + `LivenessSlack` 30 s);
  after the release, at the next wait, `Healthy` is true again. `Healthy` on a scheduler whose `Run` has not started is
  true. — `TestScheduler_Healthy`. With the job `adaptive` of AC9 and `SetPressure(true)` at 45 s, `Healthy` is true at
  every whole second from 0 to 120 s (checked after `synctest.Wait`), so a switch while the runner waits never makes it
  unhealthy. — `TestScheduler_HealthyAcrossPressureSwitch`
- [ ] AC12 (stop): with `hang` stuck, cancelling the context makes `Run` return (its goroutine exits before the test
  releases `hang`); no record is put after `Run` returned. — `TestScheduler_RunStopsWithoutWaitingForStuckWorker`. With
  job interval 10 s, timeout 2 s and a sink whose `Put` blocks until its `ctx` is done: `Put` is still blocked at 60 s (its
  context is the one given to `Run`, not one bounded by the timeout); cancelling at 60 s makes `Put` return with an error
  that `errors.Is(err, context.Canceled)` and `Run` return. — `TestScheduler_RunStopsSinkBlockedOnContext`
- [ ] AC13 (job checks): `New` returns these exact errors (job index 0-based), checked in this order per job:
  `scheduler: a sink and a logger are required` (nil sink, nil logger);
  `scheduler: job 0: collector is nil`;
  `scheduler: job 0: collector name must match the wire format's name pattern` (empty name, `-x`, a name of 129 bytes,
  `a b`);
  `scheduler: job 1 (x): duplicate collector name`;
  `scheduler: job 0 (x): interval must be positive` (0, -1 s);
  `scheduler: job 0 (x): timeout must be positive and at most the interval` (0, interval + 1 ns);
  `scheduler: job 0 (x): pressure interval must be at least the timeout and at most the interval` (timeout - 1 ns,
  interval + 1 ns, negative). Valid: pressure interval equal to the timeout, equal to the interval, and 0; no jobs.
  — `TestNew_InvalidJobs`, `TestNew_ValidJobs`
- [ ] AC14 (`model.ValidName`): true for `agent`, `proc.meminfo`, `a`, 128 bytes of `a`; false for empty, 129 bytes,
  `-a`, `.a`, `a b`, `a\n`, `ä`. — `TestValidName`
- [ ] AC15: `DiscardSink.Put` returns nil for nil, empty and non-empty slices. — `TestDiscardSink_Put`

**Self-metrics (`internal/selfstats`)**

- [ ] AC16 (issue: self-metrics recorded): with `Proc` = `os.DirFS("../../testdata/proc")` (the real sample
  `testdata/proc/self/status`, see *Real sample*), `CPUTime` returning 1.5 s, `Goroutines` returning 12, a spool provider
  returning 4096 and a send provider returning 2.5 s, `Collect` returns exactly five `metric` records, each with source
  `agent`, `captured_at` = `Run.Start`, no labels: `agent_rss_bytes` = the sample's VmRSS kB × 1024 (unit `bytes`),
  `agent_cpu_seconds_total` = 1.5 (unit `seconds`), `agent_goroutines` = 12 (no unit), `agent_spool_bytes` = 4096 (unit
  `bytes`), `agent_send_lag_seconds` = 2.5 (unit `seconds`). Every record passes `wire.CheckRecord` once given origin
  `agent` and seq 1. — `TestCollector_RecordsSelfMetrics`
- [ ] AC17: without spool and send providers `Collect` returns exactly the first three metrics.
  — `TestCollector_WithoutSpoolAndSender`
- [ ] AC18 (VmRSS guard, every variant built from the real sample in the test): exact errors
  `selfstats: self/status: no VmRSS line` (line removed; `VmRSSx:` instead of `VmRSS:`);
  `selfstats: self/status: VmRSS: not a kB value of 1 to 12 digits` (value `-1`, `+1`, `1.5`, `abc`, 13 digits, unit
  `MB`, unit `kB ` with a trailing space, no unit, no digits, `１２` in full-width digits);
  `selfstats: self/status: larger than 65536 bytes` (65,537 bytes);
  a missing file returns an error that `errors.Is(err, fs.ErrNotExist)` and starts with `selfstats: self/status: `.
  Accepted: one or more tabs or spaces before the digits, 12 digits (the largest, 999,999,999,999 kB × 1024, is below
  2^53, so the byte value is an exact double as the wire format's metric names require), `0`. — `TestCollector_StatusErrors`
- [ ] AC19 (provider errors): `CPUTime` error → `selfstats: cpu time: <err>` (wrapped); spool error →
  `selfstats: spool size: <err>`; negative spool size → `selfstats: spool size: negative`; send error →
  `selfstats: send lag: <err>`; negative lag → `selfstats: send lag: negative`. On any error `Collect` returns no records.
  — `TestCollector_ProviderErrors`
- [ ] AC20: `New` with zero `Sources` uses `/proc`, `ProcessCPUTime` and `runtime.NumGoroutine` (checked through
  `Collect` on the real system returning three metrics with RSS > 0 and goroutines >= 1); `Name()` returns `self`;
  `ProcessCPUTime` returns no error and a value >= 0, and a second call after a busy loop is not smaller.
  — `TestNew_Defaults`, `TestProcessCPUTime`

**systemd notification (`internal/sdnotify`)**

- [ ] AC21 (`NOTIFY_SOCKET` guard, every form `sd_notify` knows, see *Guard forms*): `FromEnviron` returns a disabled
  notifier and nil for unset and empty; an enabled notifier for `/run/systemd/notify`, a 107-byte absolute path,
  `@abstract`, `@` + 107 bytes; a disabled notifier and the exact error
  `sdnotify: NOTIFY_SOCKET: unsupported address (an absolute path of at most 107 bytes or @ and an abstract name of 1 to 107 bytes)`
  for `run/notify`, `./notify`, `@`, a 108-byte absolute path, `@` + 108 bytes, `vsock:2:1234`, `vsock-stream:2:1234`,
  `vsock-dgram:2:1234`, ` /run/x` (leading space); `sdnotify: NOTIFY_SOCKET: set more than once` when the variable
  appears twice. — `TestFromEnviron`
- [ ] AC22 (issue: watchdog notifications sent, transport): `Notify("WATCHDOG=1")` on a notifier for a unixgram socket
  bound in `t.TempDir()` delivers exactly one datagram `WATCHDOG=1`; `READY=1` and `STOPPING=1` likewise; on a disabled
  notifier it returns nil and sends nothing; to a path where no socket listens it returns an error starting with
  `sdnotify: `. An abstract `@name` socket works as well. — `TestNotifier_Notify`, `TestNotifier_NotifyDisabled`,
  `TestNotifier_NotifyNoListener`
- [ ] AC23 (`WATCHDOG_USEC` / `WATCHDOG_PID` guard): `WatchdogInterval(environ, pid)` returns 0 and nil when
  `WATCHDOG_USEC` is unset or empty, or when `WATCHDOG_PID` is set to another PID; `100000` (the floor,
  `MinWatchdogPeriod`) → 50 ms; `9223372036854775` → 4611686018427387500 ns; with `WATCHDOG_PID` equal to `pid` the
  interval. Exact errors:
  `sdnotify: WATCHDOG_USEC: must be a number of microseconds from 100000 to 9223372036854775 in ASCII digits` for `0`,
  `1`, `99999`, `-1`, `+5`, ` 5`, `5 `, `1e6`, `0x10`, `abc`, `9223372036854776`, 20 digits, `５` (full-width);
  `sdnotify: WATCHDOG_PID: must be a process ID from 1 to 2147483647 in ASCII digits` for `0`, `-1`, `abc`, `2147483648`;
  `sdnotify: WATCHDOG_USEC: set more than once` / `sdnotify: WATCHDOG_PID: set more than once`. — `TestWatchdogInterval`

**Agent (`internal/agent`)**

- [ ] AC24 (issue: watchdog notifications sent, loop): in a synctest bubble with a recording notifier, interval 10 s and
  `healthy` always true, `runWatchdog` sends `WATCHDOG=1` at 10, 20 and 30 s and nothing else, and returns when the
  context is cancelled. — `TestWatchdog_PingsWhileHealthy`
- [ ] AC25: with `healthy` false from 15 s to 35 s, pings come at 10 and 40 s only (none at 20 and 30 s); one warning
  "watchdog withheld" is logged at 20 s and one info "watchdog resumed" at 40 s. — `TestWatchdog_SkipsWhileUnhealthy`
- [ ] AC26: a notifier whose `Notify` fails on every call logs one warning "watchdog notification failed" for three
  failing ticks, not three. — `TestWatchdog_NotifyErrorLoggedOnce`
- [ ] AC27 (logger): table over levels and modes. `NewLogger(w, "warn", false)` drops info and writes warn; journal mode
  writes `<7>` for debug, `<6>` info, `<4>` warn, `<3>` error at the start of each line and no `time=` attribute; plain
  mode writes no `<N>` prefix and a `time=` attribute; an unknown level string logs at info. A message or attribute value
  holding `\n` and `\r` produces exactly one output line in which they appear escaped. Caps: a message, a string
  attribute and an error attribute of 5,000 `a` bytes each appear as exactly 4,096 `a` (`MaxLogValueBytes`); 4,095 `a`
  followed by `ä` (2 bytes) is cut to the 4,095 `a` (rune boundary); a record with ten attributes of 4,096 bytes of
  `\x01` is written as one line of at most 32,768 bytes (`MaxLogLineBytes`, prefix and newline included) that ends in
  `\n`, holds no other `\n` and is valid UTF-8, in both modes. — `TestNewLogger`
- [ ] AC27b (journal mode detection): with stderr an `*os.File` created in `t.TempDir()`, `journalStream` is true for
  `JOURNAL_STREAM=<dev>:<ino>` of that file's `Stat` (`*syscall.Stat_t`); false for the same device with inode + 1, for
  unset and empty, for a `*bytes.Buffer` as stderr with any value, for the malformed values `8`, `8:`, `:8`, `8:1:2`,
  `a:1`, `-1:1`, `+1:1`, ` 8:1`, `8:1 `, a 21-digit device, `18446744073709551616:1`, and for the matching value set
  twice. — `TestJournalStream`
- [ ] AC28 (end to end, all three issue criteria): `Run` with a config file in `t.TempDir()`, the token in `Environ`,
  `NOTIFY_SOCKET` pointing at a unixgram socket bound in `t.TempDir()`, `WATCHDOG_USEC=100000`, `Stderr` a file created
  in `t.TempDir()` and `JOURNAL_STREAM` set to that file's `<dev>:<ino>`, `Self.Proc` = the fixture tree and a recording
  sink: the socket receives `READY=1` first, then at least one `WATCHDOG=1`; the sink receives the metrics
  `agent_rss_bytes`, `agent_cpu_seconds_total` and `agent_goroutines`; after the context is cancelled the socket receives
  `STOPPING=1`, `Run` returns 0, and the log file holds the info line "agent started" with `agent_id` set to the file's
  ID and a line "agent stopped", every line starting with `<N>`. — `TestRun_ReadyWatchdogAndSelfMetrics`
- [ ] AC29: a config file without `agent_id` → `Run` returns 1, logs one error line "agent cannot start" whose `err`
  attribute is `config: <path>: agent_id: required key is missing`, and sends nothing to the notification socket; a
  missing config file → 1. — `TestRun_ConfigErrorExitsOne`
- [ ] AC30 (secret never logged): with the token `leak-sentinel-0123456789abcdef0123456789` and log level `debug`, the
  whole log output of a run that starts, collects once and stops contains no part of the token (checked for the whole
  token and for its first 16 bytes). — `TestRun_DoesNotLogSecret`
- [ ] AC31: `NOTIFY_SOCKET=vsock:2:1` or `WATCHDOG_USEC=abc` → one warning line ("service manager notification
  disabled" or "watchdog disabled") with the sdnotify error, the agent still runs and returns 0 on cancel.
  — `TestRun_NotificationProblemsDoNotStopTheAgent`

**Command line (`internal/cli`, `cmd/vandox-agent`)**

- [ ] AC32: `Parse("vandox-agent", args, stdout, stderr)`: nil and empty args → `done` false, `ConfigPath`
  `/etc/vandox/agent.yaml`; `-config /x/a.yaml` and `--config=/x/a.yaml` → `/x/a.yaml`; `-version` and `--version` →
  version line on stdout, done, code 0; `-version=false` → runs (done false); `-h`, `-help`, `--help` → usage on stderr,
  done, code 0; `-bogus` → `flag provided but not defined: -bogus` and usage, code 2; positional `extra` → stderr
  `vandox-agent: unexpected argument` and usage, code 2; `-config ""` → stderr `vandox-agent: -config must not be empty`
  and usage, code 2. The usage names `-config` and `-version`. — `TestParse`
- [ ] AC33: `-version` with a failing stdout → done, code 1. — `TestParse_VersionWriteFails`; three calls in a row see no
  shared flag state — `TestParse_NoSharedFlagState`
- [ ] AC34: `cmd/vandox-agent` `run`: `-version` prints the version (exit 0), `-bogus` exits 2 with the usage (both
  adapted, unchanged assertions), `-config <missing file>` exits 1. — `TestRun_Version`, `TestRun_UndefinedFlag`,
  `TestRun_MissingConfigExitsOne`

## Behavior coverage

| Spec *Behavior* bullet | Covered by |
| ---------------------- | ---------- |
| 1 runs with config and token, stops on SIGTERM/SIGINT with 0 | AC28, AC32, AC34 (signal → context in `main`, accepted uncovered wiring) |
| 2 config error → exit 1, loader's error text | AC29, AC34 |
| 3 `-version`, `-h`, usage errors | AC32, AC33, AC34 |
| 4 structured log lines, journal priority, no time under systemd, no config/secret | AC27, AC27b, AC28, AC30 |
| 5 own interval and deadline, first run at start, fixed rate | AC1, AC13 |
| 6 timeout → abandon, gap, no restart while stuck, skip gaps, late result discarded | AC1, AC2, AC3, AC4, AC12 |
| 7 panic / error → gap `unknown`, error log, runs again | AC5, AC6 |
| 8 refused record → gap, others kept | AC7, AC8 |
| 9 late wake-up → gap, run at once | AC10, AC9 (e) |
| 10 memory pressure switch, pressure flag per run | AC9, AC11 (`TestScheduler_HealthyAcrossPressureSwitch`) |
| 11 self-metrics through the scheduler; spool/lag once providers exist | AC16, AC17, AC18, AC19, AC20, AC28 |
| 12 `READY=1`, gated `WATCHDOG=1`, `STOPPING=1`; no socket → nothing | AC11, AC21, AC22, AC23, AC24, AC25, AC26, AC28, AC31 |
| 13 records dropped until the spool exists | AC15 (`DiscardSink`), AC12 (`Put` gets the agent context), wiring in `agent.Run` (Reviewer reads it) |

## Approach

New packages, standard library only (no new dependency):

1. **`internal/scheduler`** — the frame of record 0029.
   - A `Job` is a `Collector` with a `Schedule` (interval, timeout ≤ interval, optional pressure interval between timeout
     and interval). `New` checks the jobs (AC13); `Run(ctx)` starts **one runner goroutine per job** and returns when all
     runners have stopped.
   - A runner's loop, per cycle with due time `d`. The pressure mode is held as one atomic value `{high, since}` (`since`
     = `time.Now()` of the last real change); `iv(m)` is the job's interval for mode `m` (the pressure interval when `m`
     is on and the job has one, else the interval).
     1. **Cycle start** (at `d` or later; the first `d` is the moment `Run` started): read the mode into `seen`, let
        `iv = iv(seen)`. If now ≥ `d + iv` (a whole slot passed since the due time, whatever kept the runner — a long
        `Put`, a frozen process, a wait that woke late), put a gap `unknown` `[d, now)` (log "collector late", warn,
        `missed=<now - d>`) and set `d = now`. The liveness deadline becomes `d + timeout + LivenessSlack`.
     2. If the previous run is still out (abandoned and not returned): put a `collector_timeout` gap `[d, d+iv)`
        (`captured_at` = now), count the skip, go to 5. If it has returned meanwhile, drop its late result, log
        "collector returned after its deadline" (info, `skipped=<n>`) once, and continue with 3.
     3. Start a **worker goroutine** with `context.WithTimeout(ctx, timeout)` that calls `Collect(ctx, Run{Start: now UTC,
        Pressure: seen})` under `recover` and sends its result into a channel buffered to 1 (so an abandoned worker can
        always finish). Wait for the result, the deadline or the end of `ctx`.
     4. Result: on a panic put a gap `unknown` `[d, d+iv)` and log "collector panicked" (error, `panic` = `fmt.Sprint`
        cut to 1,024 bytes, `stack` cut to 4 KiB); on an error put a gap `unknown` and log "collector failed" (warn) only
        at the first failure after a success, "collector recovered" (info, `failed_runs`) at the next success; on success
        stamp each record (origin `agent`, seq 0, `captured_at` = `Run.Start` when zero), check it with `wire.CheckRecord`
        on a copy whose seq is 2^63 - 1 (the largest storable, so the size check is an upper bound for any real
        sequence number), drop refused records and add one gap `unknown` for the sample if any was refused (log "record
        refused", warn, with the first error). Put the records (one `Put` per cycle). Deadline: mark the run abandoned,
        put a `collector_timeout` gap `[d, d+iv)` (`captured_at` = `d + timeout`), log "collector timed out" (warn) once
        per stuck episode.
     5. **Next due** `n`, computed from the cycle's `d` and `seen` and the current mode `cur`: when `cur == seen`,
        `n = d + iv`; when the mode changed after the cycle started (then `since` is after the cycle's start), `n =
        max(d + iv(cur), since)` — the grid is **re-anchored**: counted from the last due time, but never before the
        moment of the switch, so the switch itself never makes a run late. The liveness deadline becomes `n + timeout +
        LivenessSlack`. Wait until `n` (no wait when `n` ≤ now) or the end of `ctx`; when the runner's wake channel fires,
        compute `n` and the deadline again by the same rule and wait on. At `n`, `d = n` and the next cycle starts (1).
        The late check of 1 is the only place a late gap is recorded, so a switch that makes `n` lie in the past (DA
        example: 60 s / 10 s job, last due 0, switch at 45 s → `n = max(10, 45) = 45`, no gap) runs at once without one,
        while a runner kept from a due time by a whole slot (`Put` until 50 s after a switch at 24 s → `n = 24`, gap
        `[24, 50)`) records exactly the span in which samples were due.
   - Gaps and stamped records go to a `Sink` (`Put(ctx, records) error`), called with the `ctx` given to `Run` (not one
     bounded by the run's timeout; a sink must honor it, AC12). Sink errors are logged with the same first-failure /
     recovery rule ("sink failed" error, "sink recovered" info) and not retried; the scheduler records **no gap** for
     records a `Put` refused — the sink that refuses them owns recording that loss (the spool, #38, records its own
     gap). The `Sink` doc comment, the area document and record 0110 say so.
   - `SetPressure(bool)` stores `{high, since: time.Now()}` in an atomic pointer when the mode really changes (a repeated
     value changes nothing and wakes no one) and wakes every runner that has a pressure interval (a buffered channel of
     1 per runner, non-blocking send; a token left over from a switch during a run only makes the next wait compute the
     same `n` again). `Healthy(now)` is true when no started runner's liveness deadline is before `now`; deadlines are
     atomics (unix nanoseconds, 0 = not started), written at every cycle start and every computation of `n`.
   - Every source of time is the `time` package (`time.Now`, timers); the tests drive it with `testing/synctest`, so no
     clock parameter is added (0062: timers cannot be driven by an own clock).
   - Gap records of the scheduler have source `agent`; the collector name goes into `gap.collector`.
   - Keep each function below gocognit / S3776 15: the loop body is split into the wait, the skip check, the run and the
     stamp/check steps (no new gocognit exclusion, record 0047).
2. **`internal/selfstats`** — the collector `self` (source `agent`): RSS from `self/status` of a `/proc` `fs.FS` (VmRSS
   guard, AC18; the read is capped at 64 KiB through `io.LimitReader`), CPU from `getrusage(RUSAGE_SELF)` user + system
   (`syscall.Getrusage`), goroutines from `runtime.NumGoroutine`, and the optional `SpoolStats` / `SendStats` providers.
   Any failure fails the sample (no records, the scheduler records the gap).
3. **`internal/sdnotify`** — the `sd_notify` protocol by hand: one `unixgram` datagram per state to the address in
   `NOTIFY_SOCKET` (absolute path, or `@` for the abstract namespace, which Go's `syscall` maps to a leading NUL; checked
   in `syscall.SockaddrUnix.sockaddr` of Go 1.27, `src/syscall/syscall_linux.go:547`), a dial and a write deadline of
   `WriteTimeout` = 1 s per notification, so a full socket buffer cannot block the caller (measured by Security in the
   plan review: a write to a unixgram socket whose queue is full — after about 11 datagrams — returns
   `os.ErrDeadlineExceeded` at the write deadline); `WATCHDOG_USEC` / `WATCHDOG_PID` parsed by ASCII digit guards (AC23),
   with a floor of `MinWatchdogPeriod` = 100 ms on `WATCHDOG_USEC` (a smaller value is refused and the watchdog disabled
   with a warning, so `WATCHDOG_USEC=1` cannot make the agent ping every 500 ns). Variables are read from the `environ`
   slice given (never `os.Getenv`), and a variable that appears twice is an error, as in `internal/config`
   (`checkEnviron`).
4. **`internal/agent`** — `Run(ctx, Options) int`: bootstrap logger (info, journal mode from `journalStream`), load the
   configuration (`config.LoadAgent`; error → "agent cannot start", exit 1), logger at `log.level`, notifier and
   watchdog interval (problems → a warning, the agent runs on), scheduler with the job `self` (interval
   `SelfInterval` = 60 s, timeout `SelfTimeout` = 10 s) writing to `Options.Sink` or `scheduler.DiscardSink`, `go
   scheduler.Run`, `READY=1`, "agent started" (`agent_id`, `version`), the watchdog loop (only with an enabled notifier
   and an interval > 0), wait for the context, `STOPPING=1`, wait for the scheduler, "agent stopped", return 0. The
   logger (`NewLogger`) wraps `slog.TextHandler`: one formatted line per record into a per-call buffer, prefixed with
   `<N>` in journal mode, the `time` attribute removed in journal mode (journald stamps every line), one `Write` per
   line, safe for concurrent use. The configuration value itself is never passed to a logger; only `agent_id` (a
   validated name) and the level are. The handler's `ReplaceAttr` cuts the message and every string, `error` and
   `fmt.Stringer` value (the latter two formatted first) to `MaxLogValueBytes` = 4 KiB at a rune boundary, and the
   handler cuts each formatted line at a rune boundary so that the written line, prefix and newline included, is at
   most `MaxLogLineBytes` = 32 KiB — below journald's default `LineMax=` of 48 KiB (`journald.conf(5)`), so the journal
   never splits an entry and no continuation can start with a forged `<N>`. The formatted text holds no raw control
   character (the handler escapes them), so a cut line is still one valid UTF-8 line.
   `journalStream(environ, stderr)` is true only when `JOURNAL_STREAM` appears once, has the form `<dev>:<ino>` (each 1
   to 20 ASCII digits, a `uint64`), stderr is an `*os.File` and its `Stat().Sys().(*syscall.Stat_t)` has that `Dev` and
   `Ino` — the check `systemd.exec(5)` prescribes, so a stream inherited by a child whose stderr was redirected does not
   switch on the prefix. Anything else means plain lines, without a warning (the effect is visible in the lines
   themselves, and the logger that could warn is the one being chosen).
5. **`internal/cli`** — `Run` is replaced by `Parse`, which adds `-config` (default `config.DefaultAgentFile`) and
   rejects positional arguments and an empty `-config` (AC32).
6. **`cmd/vandox-agent`** — `main` builds the context with `signal.NotifyContext(…, os.Interrupt, syscall.SIGTERM)` and
   calls `os.Exit(run(ctx, os.Args[1:], os.Environ(), os.Stdout, os.Stderr))` without a `defer` before `os.Exit`; `run`
   calls `cli.Parse` and `agent.Run`.
7. **`internal/model`** — `ValidName(s string) bool` exposes the existing name pattern check, so the scheduler can check
   collector names without a copy of the pattern.

### Real sample (reader of `/proc/self/status`)

`testdata/proc/self/status` is the real file of a running process, captured by the Tester with
`cat /proc/self/status > testdata/proc/self/status` on the session's Linux kernel (6.18). Its `VmRSS:` line has the form
`VmRSS:\t    1628 kB` (tab, padding spaces, digits, one space, `kB`), the form `proc_pid_status(5)` documents and Ubuntu
22.04's kernel 5.15 writes as well. The variants of AC18 are derived from this file inside the test (a line removed or
its value replaced with `strings.Replace`), so the base of every case is the real format. Before committing it the
Tester reads the whole file and checks that it holds no secret (expected: process name `cat`, IDs, memory counters and
capability masks of the sandbox; nothing else), and commits it **byte for byte as captured** — no line edited, removed or
added. The tests read the committed file and never a hand-edited copy; every variant is made in memory.

### Guard forms

**`NOTIFY_SOCKET`** — the forms `sd_notify` accepts (systemd `src/libsystemd/sd-daemon/sd-daemon.c`, `sd_pid_notify_with_fds`
via `socket_address_parse_unix`; Ubuntu 22.04 ships systemd 249):

| Form | systemd | This guard |
| ---- | ------- | ---------- |
| unset or empty | notification off | disabled, no error |
| absolute path `/…`, at most 107 bytes (108-byte `sun_path` with its NUL) | path socket | enabled |
| `@name`, 1 to 107 bytes after `@` | abstract socket | enabled (Go maps `@` to the leading NUL) |
| `@` alone | rejected | error |
| relative path, leading space, any other first byte | rejected | error |
| path or name over the limits | rejected (`EINVAL`) | error |
| `vsock:CID:PORT`, `vsock-stream:…`, `vsock-dgram:…` | systemd 253 and later (VM guests) | error — not used on the monitored server; the agent then sends nothing and says so |

**`WATCHDOG_USEC`** — systemd writes the unsigned decimal microseconds of `WatchdogSec=` (`usec_t`); the guard accepts
exactly 1 to 16 ASCII digits whose value is 100,000 (`MinWatchdogPeriod`, 100 ms) to 9,223,372,036,854,775 (the largest
whose nanoseconds fit an `int64` `time.Duration`); sign, space, exponent, hex prefix, other digit scripts and values
below the floor (0 included) are errors. The floor keeps a hostile or mistyped value from turning the watchdog loop into a
busy loop; a real unit sets seconds (#42), and 100 ms (a ping every 50 ms) is still the real-timer duration record 0062
allows for the end-to-end test (AC28). **`WATCHDOG_PID`** — decimal
PID; 1 to 10 ASCII digits, 1 to 2,147,483,647; set to another PID means "not for this process" (no watchdog, no error).
The interval used is `WATCHDOG_USEC / 2`, as `sd_watchdog_enabled(3)` recommends.

**`-config`** — Go's `flag` package accepts `-config v`, `-config=v`, `--config v` and `--config=v`; the value is passed to
`config.LoadAgent`, whose `readFile` checks that it is a regular file before opening it (a FIFO named as the file is
refused without being opened; the stat-then-open race is the related defect named in *Problem*) and caps it at 1 MiB
(0049). The parser adds only the empty-value check.

**`JOURNAL_STREAM`** — systemd sets it to `<st_dev>:<st_ino>` of the stream it connected to stdout/stderr, both printed
as unsigned decimals (`systemd.exec(5)`, *Environment variables*); the guard accepts exactly one occurrence of 1 to 20
ASCII digits, `:`, 1 to 20 ASCII digits, each fitting a `uint64` (`strconv.ParseUint` after the digit check), and then
requires the device and inode of the process's stderr to match. Anything else (sign, space, a missing part, a third part,
overflow, a second occurrence, a non-file stderr) means plain mode (AC27b).

## Options considered

1. **Scheduling model.** (a) One ticker loop that starts every due collector in a goroutine — a single loop must track
   each collector's in-flight state anyway and couples all collectors to one timer; (b) **one runner goroutine per
   collector, each run in a worker goroutine abandoned at its deadline** (chosen) — isolation is structural, a stuck
   collector costs one worker (0029), and each runner's state (stuck, failure streak, pressure) stays local; (c) a worker
   pool — bounds goroutines but a stuck worker takes a pool slot from the others, against 0029.
2. **Fixed rate vs. fixed delay.** Fixed rate from the first run (chosen): samples stay on a regular grid and a missed
   slot is detectable; fixed delay drifts by the run time and turns every slow run into a shifted series.
3. **Where records are checked.** (a) The spool checks them (it assigns the sequence number) — the "producer" rule of the
   wire format (gap instead of a refused record) would then have to be carried out by the spool for collectors it does
   not know; (b) **the scheduler checks every record once with `wire.CheckRecord` at the largest storable sequence number**
   (chosen) — one place for all collectors, the size bound is an upper bound; the spool may check again cheaply.
4. **"Report as event"** for a panic. (a) A new record kind or gap cause (`collector_panic`) — a wire format change in
   both languages (0043, golden fixture) for information the log already carries; (b) **an error log line plus a gap
   with cause `unknown` and the collector's name** (chosen) — the rule `docs/areas/wire-format.md` already gives for a
   lost sample, no format change.
5. **Gap span.** (a) `[due, deadline)` — leaves the rest of the slot uncovered, so the series between two samples looks
   partly normal; (b) **`[due, due + interval)`**, the slot the sample stands for (chosen) — consecutive gaps tile; `to`
   may lie after `captured_at` (the wire format allows it).
6. **systemd notification.** (a) `github.com/coreos/go-systemd/v22/daemon` — a new dependency (security tier, supply
   chain) for about 60 lines; (b) **`net.DialUnix("unixgram")` from the standard library** (chosen).
7. **Watchdog.** (a) Ping from a ticker unconditionally — proves only that the Go runtime schedules one goroutine; a
   runner stuck in the sink would never be noticed; (b) **ping only while every runner is within its liveness deadline
   (`due + timeout + LivenessSlack`, 30 s)** (chosen) — a stuck collector does not count (its runner keeps moving), a
   runner blocked in the sink or a frozen process does; the 30 s slack keeps a descheduled agent under memory pressure
   from being restarted for a few seconds of delay; (c) gate on a heartbeat of the scheduler goroutine only — misses a
   blocked runner. Lower bound of the period: none — `WATCHDOG_USEC=1` makes a 500 ns busy loop; a floor of 1 s —
   forces the end-to-end test to wait 500 ms on a real timer, against 0062; **100 ms** (chosen) — no busy loop, the test
   keeps the 50 ms of 0062.
8. **Logging.** (a) `slog.JSONHandler` — journald shows the JSON as the message, harder to read in `journalctl`, and the
   priority would still need the prefix; (b) the native journal protocol (`/run/systemd/journal/socket`) — a second
   socket and more code for field names nobody queries yet; (c) **`slog.TextHandler` on stderr with the `<N>` priority
   prefix that systemd's `SyslogLevelPrefix=` (on by default) reads, enabled by `JOURNAL_STREAM`** (chosen).
   Journal detection: (a) the variable's presence — a child or a redirected stderr inherits it and would get prefixes in
   a file; (b) **its `<dev>:<ino>` compared with stderr's** (chosen), the check `systemd.exec(5)` prescribes. Long lines:
   (a) leave them — journald splits a line over `LineMax=` (48 KiB) and the rest becomes an entry of its own; (b) cut
   each value only — ten long values still exceed the limit; (c) **cut each value to 4 KiB and each line to 32 KiB**
   (chosen).
11. **Pressure switch and the grid.** (a) Next due = last due + the new interval even when that is long past — the late
    check cannot tell a switch from a frozen agent and records a false `unknown` gap (Devil's Advocate: switch at 45 s
    of a 60 s / 10 s job → gap `[20, 45)`), against 0028; (b) restart the grid at the switch in every case — a switch
    shortly after a run would start the next run at once and sample twice within the pressure interval; (c) **next due =
    max(last due + new interval, moment of the switch)** (chosen) — on the grid where possible, never late because of the
    switch, and a runner held up past it records exactly the span in which samples were due.
12. **A sink that refuses records.** (a) The scheduler puts a gap for them — it cannot know what a failed `Put` kept, and
    the gap goes into the same failing sink; (b) **the sink owns recording the loss, the scheduler logs the error**
    (chosen; Devil's Advocate minor 4).
9. **Records before the spool exists.** (a) Wait for #38 with the whole frame — blocks #31 to #37, which all depend on
   #30; (b) **a `Sink` interface with `DiscardSink` wired until #38** (chosen) — the agent is not released before
   #38/#39 (v0.2.0, #45), so nothing deployed loses data; (c) log the records — would write process names and later log
   text into the journal (area 12).
10. **Spool size and send lag.** (a) Measure the spool directory's size now by walking it — a second definition next to
    the one #38 will own; (b) **provider interfaces, emitted once #38/#39 implement them** (chosen).

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Go | `internal/scheduler/scheduler.go` (new) | `Run`, `Collector`, `Schedule`, `Job`, `Scheduler`, `New`, `Run`, `SetPressure`, `Healthy`, `Source`, `LivenessSlack` |
| Go | `internal/scheduler/runner.go` (new) | per-job runner loop, worker, `PanicError` |
| Go | `internal/scheduler/sink.go` (new) | `Sink`, `DiscardSink` |
| Go | `internal/selfstats/selfstats.go` (new) | `Collector`, `Sources`, `SpoolStats`, `SendStats`, `New`, `ProcessCPUTime`, metric name constants |
| Go | `internal/sdnotify/sdnotify.go` (new) | `Notifier`, `FromEnviron`, `Notify`, `Enabled`, `WatchdogInterval`, state constants, `WriteTimeout`, `MinWatchdogPeriod` |
| Go | `internal/agent/agent.go` (new) | `Options`, `Run`, `SelfInterval`, `SelfTimeout` |
| Go | `internal/agent/logger.go` (new) | `NewLogger` and its handler, `MaxLogValueBytes`, `MaxLogLineBytes`, `journalStream` |
| Go | `internal/agent/watchdog.go` (new) | `notifier`, `runWatchdog` |
| Go | `internal/cli/cli.go` | `Run` replaced by `Parse`, `Command` |
| Go | `cmd/vandox-agent/main.go` | context from signals, `run` with environ, calls `cli.Parse` and `agent.Run` |
| Go | `internal/model/model.go` | `ValidName` |

## Signatures (for the Dev's skeleton)

```go
// internal/model/model.go
// ValidName reports whether s matches the name pattern of source, metric names, label keys and gap.collector
// (1 to MaxNameBytes bytes of ^[A-Za-z0-9][A-Za-z0-9._:/@+-]*$).
func ValidName(s string) bool
```

```go
// Package scheduler runs the agent's collectors, each in its own goroutine under its own deadline (record 0029).
package scheduler

// Source is the source of the records the scheduler writes itself (gaps).
const Source = "agent"

// LivenessSlack is how long after its run's deadline a runner may still be busy before Healthy reports false.
const LivenessSlack = 30 * time.Second

// Run describes one run of a collector.
type Run struct {
	Start    time.Time // when the run started, in UTC
	Pressure bool      // whether memory pressure mode was on when the run started
}

// Collector takes one sample per run.
type Collector interface {
	// Name returns the collector's name, a name of the wire format; it names the collector in gaps and logs.
	Name() string
	// Collect takes one sample. It honors ctx where its source allows; the scheduler stops waiting at the deadline
	// either way and drops a late result.
	Collect(ctx context.Context, run Run) ([]model.Record, error)
}

// Schedule is how often and how long a collector runs.
type Schedule struct {
	Interval         time.Duration // between the starts of two runs
	Timeout          time.Duration // deadline of one run; at most Interval, and at most PressureInterval when set
	PressureInterval time.Duration // interval under memory pressure; 0 keeps Interval
}

// Job is a collector with its schedule.
type Job struct {
	Collector Collector
	Schedule  Schedule
}

// Scheduler runs jobs.
type Scheduler struct{ /* unexported */ }

// New checks jobs and returns a scheduler that puts records into sink and logs to log.
func New(jobs []Job, sink Sink, log *slog.Logger) (*Scheduler, error)

// Run runs every job until ctx is done and returns when every runner has stopped. Abandoned runs are not waited for.
func (s *Scheduler) Run(ctx context.Context)

// SetPressure switches memory pressure mode on or off. A runner with a pressure interval then counts its next due time
// from its last due time with the new interval, but not earlier than the moment of the switch. Setting the mode it
// already has changes nothing.
func (s *Scheduler) SetPressure(high bool)

// Healthy reports whether every started runner is within its liveness deadline at now.
func (s *Scheduler) Healthy(now time.Time) bool

// PanicError is the error of a run whose collector panicked.
type PanicError struct {
	Value any    // the value passed to panic
	Stack []byte // the stack of the panicking goroutine
}

// Error returns "scheduler: collector panicked: " and fmt.Sprint(Value), cut to its first 1,024 bytes.
func (e *PanicError) Error() string
```

```go
// internal/scheduler/sink.go

// Sink takes the records the scheduler produced: origin agent, sequence number 0 (the sink assigns sequence numbers).
// Put is called with the context given to Scheduler.Run, not one bounded by a run's timeout, and must return when it is
// done. The scheduler logs an error of Put and records no gap for the refused records: a sink that refuses records owns
// recording their loss as a gap.
type Sink interface {
	Put(ctx context.Context, records []model.Record) error
}

// DiscardSink drops every record. The agent uses it until the spool exists (#38).
type DiscardSink struct{}

// Put drops records and returns nil.
func (DiscardSink) Put(ctx context.Context, records []model.Record) error
```

```go
// Package selfstats collects the agent's own resource use as metrics.
package selfstats

// Name is the collector's name.
const Name = "self"

// Metric names.
const (
	MetricRSS        = "agent_rss_bytes"
	MetricCPU        = "agent_cpu_seconds_total"
	MetricGoroutines = "agent_goroutines"
	MetricSpool      = "agent_spool_bytes"
	MetricSendLag    = "agent_send_lag_seconds"
)

// MaxStatusBytes is the most of self/status that is read.
const MaxStatusBytes = 64 << 10

// SpoolStats reports the size of the spool (implemented by the spool, #38).
type SpoolStats interface {
	SpoolBytes(ctx context.Context) (int64, error)
}

// SendStats reports how far sending lags behind capture (implemented by the sender, #39).
type SendStats interface {
	SendLag(ctx context.Context) (time.Duration, error)
}

// Sources are what the collector reads; a nil field takes its default.
type Sources struct {
	Proc       fs.FS                         // default os.DirFS("/proc"); self/status is read
	CPUTime    func() (time.Duration, error) // default ProcessCPUTime
	Goroutines func() int                    // default runtime.NumGoroutine
	Spool      SpoolStats                    // nil: no spool metric
	Sender     SendStats                     // nil: no send lag metric
}

// Collector collects the self-metrics; it implements scheduler.Collector.
type Collector struct{ /* unexported */ }

// New returns a collector reading src.
func New(src Sources) *Collector

// Name returns Name.
func (c *Collector) Name() string

// Collect returns the self-metrics of one sample, or an error and no records.
func (c *Collector) Collect(ctx context.Context, run scheduler.Run) ([]model.Record, error)

// ProcessCPUTime returns the user plus system CPU time this process has used (getrusage RUSAGE_SELF).
func ProcessCPUTime() (time.Duration, error)
```

```go
// Package sdnotify implements the systemd notification protocol (sd_notify) with the standard library.
package sdnotify

// Notification states.
const (
	Ready    = "READY=1"
	Watchdog = "WATCHDOG=1"
	Stopping = "STOPPING=1"
)

// WriteTimeout bounds the dial and the write of one notification.
const WriteTimeout = time.Second

// MinWatchdogPeriod is the smallest WATCHDOG_USEC accepted; a smaller one is an error and leaves the watchdog off.
const MinWatchdogPeriod = 100 * time.Millisecond

// Notifier sends notifications to the service manager. The zero value and a notifier without a socket are disabled.
type Notifier struct{ /* unexported: the socket address */ }

// FromEnviron returns a notifier for the socket NOTIFY_SOCKET in environ names; a disabled one when it is unset or empty,
// and a disabled one with an error when the address is not supported or the variable is set twice.
func FromEnviron(environ []string) (*Notifier, error)

// Enabled reports whether the notifier has a socket.
func (n *Notifier) Enabled() bool

// Notify sends state as one datagram; a disabled notifier sends nothing and returns nil.
func (n *Notifier) Notify(state string) error

// WatchdogInterval returns half of WATCHDOG_USEC in environ, or 0 when the watchdog is off or meant for another PID than pid.
func WatchdogInterval(environ []string, pid int) (time.Duration, error)
```

```go
// Package agent runs vandox-agent: configuration, logging, the scheduler with its collectors and the systemd notification.
package agent

// Schedule of the self-metrics collector.
const (
	SelfInterval = 60 * time.Second
	SelfTimeout  = 10 * time.Second
)

// Options are the process boundaries Run works with.
type Options struct {
	ConfigPath string            // the configuration file
	Environ    []string          // the environment, in os.Environ form
	Stderr     io.Writer         // the log output
	Sink       scheduler.Sink    // nil: scheduler.DiscardSink
	Self       selfstats.Sources // the self-metrics sources; zero fields take their defaults
}

// Run runs the agent until ctx is done and returns the exit code: 0 after a clean stop, 1 when it cannot start.
func Run(ctx context.Context, opts Options) int

// NewLogger returns the agent's logger: slog text lines on w at level (debug, info, warn or error; anything else is info).
// In journal mode every line starts with its syslog priority (<7> debug, <6> info, <4> warn, <3> error) and has no time.
// The message and every string, error and fmt.Stringer value are cut to MaxLogValueBytes, each line to MaxLogLineBytes.
func NewLogger(w io.Writer, level string, journal bool) *slog.Logger

// Log size limits of NewLogger.
const (
	MaxLogValueBytes = 4 << 10  // the message and each string, error or fmt.Stringer value, cut at a rune boundary
	MaxLogLineBytes  = 32 << 10 // one written line, priority prefix and newline included; journald splits at 48 KiB
)

// journalStream reports whether JOURNAL_STREAM in environ, set once as <dev>:<ino>, names the device and inode of
// stderr, which must be an *os.File.
func journalStream(environ []string, stderr io.Writer) bool

// notifier is the part of *sdnotify.Notifier the watchdog uses.
type notifier interface {
	Notify(state string) error
}

// runWatchdog sends sdnotify.Watchdog every interval while healthy(now) holds, until ctx is done.
func runWatchdog(ctx context.Context, n notifier, interval time.Duration, healthy func(time.Time) bool, log *slog.Logger)
```

```go
// internal/cli/cli.go

// Command is what the command line asks the agent to run.
type Command struct {
	ConfigPath string // the configuration file; default config.DefaultAgentFile
}

// Parse parses args (without the program name) for the binary called name. done reports that the process ends with
// code without running the agent: after -version (0, or 1 when writing stdout fails), -h/-help (0), and a usage error
// (2: an undefined flag, a positional argument, an empty -config). Usage and errors go to stderr.
func Parse(name string, args []string, stdout, stderr io.Writer) (cmd Command, code int, done bool)
```

```go
// cmd/vandox-agent/main.go
func run(ctx context.Context, args, environ []string, stdout, stderr io.Writer) int
```

Existing files the skeleton rewrites: `internal/cli/cli.go` (whole file: `Run` removed, `Parse` added),
`cmd/vandox-agent/main.go` (whole file). `internal/model/model.go` gets one function. All other files are new.

## Test files

- `internal/scheduler/scheduler_test.go` — `TestNew_InvalidJobs`, `TestNew_ValidJobs`,
  `TestScheduler_HangingCollectorDoesNotDelayOthers`, `TestScheduler_Healthy`, `TestScheduler_HealthyAcrossPressureSwitch`,
  `TestScheduler_RunStopsWithoutWaitingForStuckWorker`, `TestScheduler_RunStopsSinkBlockedOnContext`, plus the shared doubles of the package's tests (scripted
  collector, hanging collector released in `t.Cleanup`, recording sink with an optional blocking `Put`, recording
  `slog.Handler`)
- `internal/scheduler/runner_test.go` — `TestScheduler_TimeoutRecordsGap`, `TestScheduler_StuckCollectorSkipsCyclesWithGaps`,
  `TestScheduler_LateResultDiscardedAndCollectorResumes`, `TestScheduler_PanicRecoveredAsGap`, `TestPanicError_Error`,
  `TestScheduler_CollectorErrorRecordsGap`, `TestScheduler_RecordsGetAgentMetadata`,
  `TestScheduler_InvalidRecordReplacedByGap`, `TestScheduler_PressureShortensInterval`, `TestScheduler_LateWakeUpRecordsGap`
- `internal/scheduler/sink_test.go` — `TestDiscardSink_Put`
- `internal/model/model_test.go` — `TestValidName` (added)
- `internal/selfstats/selfstats_test.go` — `TestCollector_RecordsSelfMetrics`, `TestCollector_WithoutSpoolAndSender`,
  `TestCollector_StatusErrors`, `TestCollector_ProviderErrors`, `TestNew_Defaults`, `TestProcessCPUTime`
- `internal/sdnotify/sdnotify_test.go` — `TestFromEnviron`, `TestNotifier_Notify`, `TestNotifier_NotifyDisabled`,
  `TestNotifier_NotifyNoListener`, `TestWatchdogInterval`
- `internal/agent/agent_test.go` — `TestRun_ReadyWatchdogAndSelfMetrics`, `TestRun_ConfigErrorExitsOne`,
  `TestRun_DoesNotLogSecret`, `TestRun_NotificationProblemsDoNotStopTheAgent`
- `internal/agent/logger_test.go` — `TestNewLogger`, `TestJournalStream`
- `internal/agent/watchdog_test.go` — `TestWatchdog_PingsWhileHealthy`, `TestWatchdog_SkipsWhileUnhealthy`,
  `TestWatchdog_NotifyErrorLoggedOnce`
- `internal/cli/cli_test.go` — `TestParse`, `TestParse_VersionWriteFails`, `TestParse_NoSharedFlagState` (replace
  `TestRun`, `TestRun_StdoutWriteFails`, `TestRun_NoSharedFlagState`)
- `cmd/vandox-agent/main_test.go` — `TestRun_Version`, `TestRun_UndefinedFlag` (adapted), `TestRun_MissingConfigExitsOne` (new)
- Fixture: `testdata/proc/self/status` (real sample, *Real sample*).

Socket paths: a unix socket path is limited to 107 bytes; `t.TempDir()` under `/tmp` plus the test name stays far below
that for the names above, and the tests bind `<TempDir>/n.sock`. A test may also use an abstract `@vandox-test-<random>`
name (AC22).

Existing test code that calls a changed signature:

- `internal/cli/cli_test.go` (`cli.Run` in `TestRun`, `TestRun_StdoutWriteFails`, `TestRun_NoSharedFlagState`): the
  **Dev** in step 4 replaces each call mechanically by a call of `cli.Parse` that returns its code (no assertion
  touched), so *Build* passes; the **Tester** rewrites the file in step 5 into the tests above (the behavior of "no
  arguments" and "positional argument" changes from usage to run / usage error).
- `cmd/vandox-agent/main_test.go` (`run(args, &stdout, &stderr)` in `TestRun_Version`, `TestRun_UndefinedFlag`): the
  **Dev** adapts both calls in step 4 to `run(t.Context(), args, nil, &stdout, &stderr)`, assertions unchanged.

Files of the change no unit test can cover: none. The body of `main` (signal context, `os.Exit`) is the accepted
uncovered wiring (`docs/UNIT_TESTS.md`, *Code coverage*; records 0072, 0074); every other line is reachable from the tests
above (`ProcessCPUTime` and the default sources are called for real in `TestProcessCPUTime` / `TestNew_Defaults`).

## Areas

- `docs/areas/agent.md` — status; new sections *Command line and start-up*, *Logging*, *Scheduling* (intervals, deadlines,
  skips, gap spans and causes, late runs, refused records, memory pressure mode, liveness), *Self-metrics*,
  *Service manager* (states, socket forms, watchdog rule); *Connection* says that the local notification socket and the
  journal are not network connections. Owner: Lead (written with this plan; the Dev reports any deviation of the
  implementation, and the Lead's revision or approval fixes the text).
- `docs/areas/wire-format.md` — *Metric names*: the five `agent_*` metrics with units, and the source `agent`; *Records*
  (`captured_at`) and *`gap`*: a gap's `to` may lie after its `captured_at` (a skipped or abandoned run's gap
  covers the slot to one interval after the due time); confirmed against `internal/model/gap.go:38-44`, which relates
  only `to` and `from`. Owner: Lead.
- `docs/areas/configuration-and-secrets.md` — the agent now reads its file at start-up and takes `-config <path>`; the
  FIFO sentence reworded to "the type is checked before the file is opened" (*Problem*, related defect). Owner: Lead.

## Documentation updates

| File | Change | Owner |
| ---- | ------ | ----- |
| `docs/areas/agent.md`, `docs/areas/wire-format.md`, `docs/areas/configuration-and-secrets.md` | see *Areas* | Lead (done with this plan) |
| `docs/ARCHITECTURE.md` | status paragraph (the agent's runtime frame exists), *Components* (scheduler, self-metrics, systemd notification, record 0110), *Configuration* (the agent calls `LoadAgent`) | Lead (done with this plan) |
| `docs/decisions/0110-…md` and the index row | new record, `Proposed` | Lead (done with this plan) |
| `README.md` | *Agent* paragraph near line 138 and the binaries table (line 16): the agent binary now runs the frame (configuration, scheduler, self-metrics, systemd notification) but still sends nothing; line 48 "the agent binary of v0.1.0 does not call it yet" stays true for v0.1.0 and gets "from v0.2.0 on the agent reads it at start-up, path with `-config`" | Dev |
| `docs/CONTRIBUTING.md` | line 31 "The agent only prints its version so far" → it runs with a configuration file and the token, collects only its own metrics and sends nothing yet | Dev |
| `deploy/agent/agent.yaml` | header lines 3-5: the binary reads this file at start-up (`-config` for another path); comment only, the keys stay (`TestLoadAgent_Example`) | Dev |
| `.squad/project.md` *Security areas* | area 10: the agent's `-config` flag (`internal/cli` `Parse`) and the systemd variables (`internal/sdnotify`: `FromEnviron`, `WatchdogInterval` with its 100 ms floor, ASCII digit guards; `internal/agent` `journalStream`: `JOURNAL_STREAM` compared with stderr's device and inode); area 11: besides the ingest port the agent writes only to the local systemd notification socket named by `NOTIFY_SOCKET` (unix datagram, 1 s deadline, no network); area 12: the agent logs through `slog.TextHandler` on stderr (values with control characters quoted and escaped, panic values and stacks as attributes, values cut to 4 KiB and lines to 32 KiB below journald's `LineMax=`, the configuration never logged, `internal/agent` `NewLogger`); area 8: the agent token is loaded by the running agent and never logged (`TestRun_DoesNotLogSecret`); *Guarantees*, hanging collector: implemented by `internal/scheduler` | Dev |
| `.squad/project.md` *Test doubles* | `/proc` row: the self-metrics read `testdata/proc/self/status` (implemented for `self/status`); new row "systemd notification socket": a unixgram socket bound in `t.TempDir()` (or an abstract name) that the test reads, and a recording `notifier` for the watchdog loop (implemented); new row "collectors and sink (scheduler)": scripted, hanging (released in `t.Cleanup`) and panicking collectors, a recording sink with an optional blocking `Put`, a recording `slog.Handler` (implemented); *time* row: the scheduler and the watchdog loop are tested in `testing/synctest` bubbles (0062) | Tester |
| `docs/UNIT_TESTS.md` | Go bullet on the network: a unix datagram socket bound in `t.TempDir()` (or an abstract name) stands in for the systemd notification socket; it is not network, and real-timer tests on it follow 0062 | Tester |

## Architecture check

- **A hanging collector never blocks the agent (0029)** — implemented here as specified: one runner and one worker per
  collector, abandonment at the deadline, no restart while stuck, at most one stuck worker per collector (AC1, AC3, AC12).
- **No data gaps unless explicitly recorded (0028)** — every missed sample the scheduler knows of is a gap: timeout,
  skip, panic, error, refused record, late wake-up (AC2–AC10). The records dropped by `DiscardSink` are not a gap in a
  deployed agent: the agent is not released before the spool and the sender (#38, #39, #45), and the area document and
  record 0110 say so. The gap of a stopped agent (`agent_not_running`) stays with #38, and so does the gap for records
  a `Put` refuses: the scheduler cannot know what of a refused batch the sink kept, so the sink that refuses owns the
  gap (the `Sink` contract, the area document and 0110 say so). A pressure switch never produces a gap (option 11).
- **Outbound only (0006), never contacts another service (0012)** — the notification socket is local IPC with the
  service manager that started the process, not a network connection; journald gets stderr. The area document states
  this explicitly; `ARCHITECTURE.md`'s "connects outbound only and never listens on a port" stays true (the agent binds
  no socket; a datagram is sent from an unbound socket).
- **Server RAM stays at 2 GB (0025), frugal agent** — goroutines: one runner per collector plus one worker per run, plus
  at most one abandoned worker per collector; the self-metrics make the agent's own use visible (#44 measures it).
- Backfill and alerting guarantees: untouched.

## Security considerations

- [ ] Limits on parsed input: `self/status` is read up to 64 KiB **raw bytes** (before any decoding; the guard works on
  ASCII bytes, so a non-ASCII byte fails it); `NOTIFY_SOCKET` 107 bytes after the first byte, **raw bytes**;
  `WATCHDOG_USEC` 16 ASCII digits and at least 100 ms, `WATCHDOG_PID` 10 ASCII digits, `JOURNAL_STREAM` two parts of 20
  ASCII digits, raw bytes; a panic value is cut at 1,024 bytes and a stack at 4 KiB of the formatted text before it
  reaches the logger (a cut inside a UTF-8 sequence is escaped by the handler's quoting, not removed); the logger cuts
  every value to 4 KiB and every line to 32 KiB, below journald's 48 KiB `LineMax=` (AC27). The config file keeps its
  1 MiB cap (0049).
- [ ] Texts that reach the user (the log on stderr / journal): the sdnotify errors above (no variable value, never the
  socket path except inside the `net` error of a failed `Notify`, which names the socket path systemd gave); the
  `config` errors of `LoadAgent` (never a value, 0049/0050); "collector failed" / "sink failed" carry the collector's or
  sink's `error` text — for `self` these are the exact `selfstats:` texts above, which name only `self/status` and the
  `fs` error; panic values and stacks are attributes, quoted by the handler, so a newline cannot start a forged line
  (AC27). The secret is never an argument of a log call (AC30); the Go CodeQL rule of `.squad/stack.md` (*Logger
  arguments*) — no logger argument named `secret`, `password`, `token`-like names — is checked by grepping the new
  logger calls.
- [ ] `NOTIFY_SOCKET` comes from the process environment, which only the service manager (or whoever starts the binary)
  controls; a hostile value can only make the agent send the fixed strings `READY=1`, `WATCHDOG=1` and `STOPPING=1` to a
  local socket of the agent user's reach; each send is bounded by `WriteTimeout`.
- [ ] Claims on library behavior, both **measured by Security in the plan review** (Go 1.27, kernel 6.18) and stated as
  fact in record 0110 and the area document: (1) `slog.TextHandler` keeps each record on one line — it escapes `\n`,
  `\r`, a `<N>` inside a value is quoted, U+2028, U+202E, invalid UTF-8 bytes and control characters in keys as well;
  AC27 (`TestNewLogger`) pins it so a Go update that changes it fails the build; (2) a `unixgram` write to a socket
  whose queue is full (after about 11 datagrams) returns `os.ErrDeadlineExceeded` at the write deadline instead of
  blocking. No measurement task is left for the Dev.

## Challenge

Devil's Advocate, verdict OBJECTIONS (1 major, 6 minor), on plan commit fb00bb7:

- **Major 1 — a pressure switch records a false `unknown` gap.** **Accepted**, plan revised. The objection is right: with
  `d` = last due + pressure interval recomputed at the switch and the late check after the run, the DA example (60 s /
  10 s, switch at 45 s) recorded `[20, 45)`, against 0028. New rule (*Approach* 1, steps 1 and 5; option 11): the next
  due time after a switch is `max(last due + new interval, moment of the switch)`, so the switch re-anchors the grid and
  is never late; the late check sits at the cycle start only and compares against the interval of the mode the cycle
  sees. AC9 gets the DA's case (b) — switch at 45 s, runs at 45, 55, 65 s, no gap — and three more: (c) a switch shortly
  after a run keeps the grid (`0 + 10 s`, not the switch time), (d) a switch while the runner is held in `Put` but less
  than a slot, no gap, (e) a runner held a whole pressure slot past the switch records exactly `[switch, now)`. Found on
  the way: the old plan checked lateness only after a run (step 5), so a process frozen during the **wait** recorded no
  gap; the check at the cycle start now covers both (the wait case cannot be driven in a bubble, whose timers fire on
  time; it runs the same branch AC10 tests).
- **Minor 1 — how `d` is chosen after the re-anchor, and liveness.** **Accepted.** Step 5 defines `n`; the liveness
  deadline is rewritten at every cycle start and at every computation of `n`, including each time the wake channel
  fires. New test `TestScheduler_HealthyAcrossPressureSwitch` (AC11): with the switch at 45 s `Healthy` stays true at
  every second from 0 to 120 s.
- **Minor 2 — wire format on `to` after `captured_at`.** **Accepted.** `docs/areas/wire-format.md` now says, at
  `captured_at` and at *`gap`*, that a gap's `to` may lie after its `captured_at` and why; the model's check
  (`internal/model/gap.go:38-44`) relates only `to` and `from`.
- **Minor 3 — `JOURNAL_STREAM` as `dev:ino`.** **Accepted** (Security finding 2 as well). `journalStream` compares the
  variable's `<dev>:<ino>` with stderr's `Stat` (*Guard forms*, AC27b, `TestJournalStream`); AC28 makes stderr a real
  file and sets `JOURNAL_STREAM` to its device and inode, so the end-to-end test runs the real check, not a fake.
- **Minor 4 — a failing `Put` loses records without a gap.** **Accepted** as documentation of ownership, not as a
  scheduler gap: the scheduler cannot know which records a failed `Put` kept, and a gap written into the same failing
  sink would fail too. The `Sink` doc comment, `docs/areas/agent.md` (*Scheduling*) and 0110 (*Consequences*) say that
  the sink that refuses records owns recording their loss (#38's spool); the scheduler logs the error.
- **Minor 5 — `Put` gets the agent context.** **Accepted.** The `Sink` doc comment says so; AC12 gets
  `TestScheduler_RunStopsSinkBlockedOnContext` (a `Put` blocked on its `ctx` is still blocked after the timeout and returns
  `context.Canceled` when `Run`'s context is cancelled).
- **Minor 6 — the fixture as committed.** **Accepted.** *Real sample* and task 3: the Tester reads the whole capture,
  checks it for secrets and commits it byte for byte; the tests read the committed file and derive every variant in
  memory.

## Security review

Security, verdict APPROVED with four non-blocking findings; both claims the plan marked unverified were measured and hold.

- **1 — `WATCHDOG_USEC=1` spins a core.** **Accepted.** A floor `MinWatchdogPeriod` of 100 ms: a smaller value is an
  error and the watchdog stays off with a warning (AC23 cases `0`, `1`, `99999`; `100000` → 50 ms). 100 ms rather than the
  1 s suggested: it already removes the busy loop (at most 20 datagrams a second), and AC28 keeps the 50 ms real-timer
  duration of 0062 (option 7).
- **2 — `JOURNAL_STREAM` `dev:ino`, and a second occurrence.** **Accepted**, as DA minor 3. A variable set twice means plain
  mode, not an error and not a warning: the logger that could warn is the one being chosen, and plain lines are harmless
  (AC27b). `NOTIFY_SOCKET`, `WATCHDOG_USEC` and `WATCHDOG_PID` set twice stay errors with a warning (AC21, AC23).
- **3 — journald splits lines over `LineMax=` (48 KiB).** **Accepted, fixed now** rather than deferred: `NewLogger`'s
  handler cuts the message and every string, error and `fmt.Stringer` value to 4 KiB and each line to 32 KiB (AC27,
  `MaxLogValueBytes`, `MaxLogLineBytes`); the panic stack cut moves from 8 KiB to 4 KiB to match.
- **4 — "a FIFO cannot block".** **Accepted.** Reworded to "checked before opening" in the plan and in
  `docs/areas/configuration-and-secrets.md`; the stat-then-open race behind it is named as a related defect with a
  follow-up issue (*Out of scope*).
- **Measured claims.** `slog.TextHandler` one line per record and the unixgram write deadline are stated as verified in
  *Security considerations*, record 0110 and `docs/areas/agent.md`; the Dev's scratch measurement (task 11) is dropped.
  AC27 still pins the handler's behavior.

## Decision records

- `docs/decisions/0110-agent-runtime-frame-runner-per-collector-gaps-for-every-missed-sample-hand-written-sd-notify.md`
  (Proposed) — scheduling model, where records are checked, the panic as a gap, the gap span, `sd_notify` without a
  dependency, the liveness-gated watchdog and its floor, journal logging with the `JOURNAL_STREAM` check and the line
  caps, `DiscardSink` until #38 and the sink's duty for refused records, spool/lag providers, the pressure switch's
  re-anchoring.

## Out of scope / follow-ups

- The collectors (#31–#37); the thresholds that call `SetPressure` (#31/#32).
- The spool with sequence numbers, `agent_not_running` gaps and the spool size provider (#38); the sender and the send
  lag provider (#39).
- The systemd unit (`Type=notify`, `WatchdogSec=`, `NotifyAccess=main`) and its hardening (#42): the area document names
  what the unit must set for the watchdog to work.
- Each open item above already has its issue. One new follow-up issue (the orchestrator opens it): **"config: open the
  configuration file without a stat-then-open race"** — `internal/config/config.go:75-82` stats the path and then opens
  it, so a FIFO put in place between the two calls blocks start-up; open with `O_NONBLOCK` (or `O_PATH`) and check the
  type with `Fstat` on the opened descriptor, and mirror the fix in the backend's loader if it has the same order.
