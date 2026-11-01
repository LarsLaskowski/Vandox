# Plan: Save boot and backup state only after the run's records are kept

Source: Issue #300
Status: Draft
Tier: standard — a behavior change in when the agent's state file takes the boot and the open backup run (and which uptime a save refreshes); no security control is created, changed or removed.
Security areas touched: 9 File writes and paths derived from external input (`internal/agentstate` `Saver.prepare`: which uptime a save of `agent-state.json` writes; path, mode, open flags and the spool barrier stay as they are)

## Problem / root cause

Both collectors write their state change into the `*agentstate.Store` inside `Collect`, before the scheduler hands the run's
records to the spool:

- `kernel`: `Kernel.prepare` (`internal/kernevents/kernevents.go:120`) calls `bootCheck` at the first run, which calls `saveBoot`
  (`internal/kernevents/boot.go:44`), which runs `k.store.Update(true, …)` (`boot.go:62`).
- `backups`: `advance` (`internal/backups/backups.go:164`) calls `begin` (`:198`), `see` (`:205`) and `clear` (`:217`), each a
  `store.Update`; `remember` (`:97`) also writes, restoring the pre-check run after a dropped check.

The `state` job runs in its own goroutine; every save first syncs the spool (`agentstate.go:341-345`, barrier set at
`internal/agent/agent.go:404`), but a save that falls between `Collect` and the hand-over (`scheduler/runner.go`, `handle` →
`deliver`) syncs a spool that does not hold the run's records yet. A power loss before the spool's next one-second sync then loses
the `boot` / `backup_run` record while the state file holds the change, so `bootCheck` (`saved.ID == raw`) or `advance` never
produces it again, and no gap records it.

Related defect found on the way: `Saver.prepare` (`agentstate.go:760-778`) refreshes the uptime of **any** stored boot. At start-up
`lastSave` is zero, so the first `state` run is a heartbeat, and it runs concurrently with the kernel's first run (`scheduler.go:129`
starts every runner at once): when it reads the state before `bootCheck`, the previous boot's saved uptime is overwritten with the
new boot's uptime and `previous_uptime_ns` is wrong. Moving the boot save behind `Kept` widens that window (the state holds the
previous boot until the event is kept), so the fix includes it.

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | The boot change is written inside `Collect`, before the hand-over (issue: `boot.go:62`) | `kernevents.go:99-120` → `boot.go:44` → `boot.go:62` | confirmed |
| 2 | The backup run state is written inside `Collect` (issue: `backups.go:198,205,217`) | `backups.go:87` → `advance` → `begin`/`see`/`clear` at 198/205/217; also `remember` at 97 (a restore, issue does not name it) | confirmed |
| 3 | A lost record is never produced again and no gap says so (issue) | boot: `bootCheck` returns nil when `saved.ID == raw` (`boot.go:31`); after a reboot the next event names the lost boot only as `previous_boot_id`. backups: the stored run is continued or ended at the next check, its start never re-emitted (`backups.go:167-189`) | confirmed |
| 4 | The `logs` collector commits behind the scheduler's `Kept` (issue) | `logship.go:281-298` (`Kept` → `adopt` → `save` → `store.Update`) | confirmed |
| 5 | #38 keeps this as a stated residual (issue: spec E5, record 0123, agent area) | record 0123 *Consequences* (before this plan), `docs/areas/agent.md` *Kernel events* and *Backup runs* residuals; the #38 spec is not in the repository (working records leave it), not needed | confirmed |
| 6 | The scheduler calls `Observer.Kept` after `deliver` (the spool's put) and only for a run that finished in time; an abandoned or skipped run gets no call | `runner.go` `finish` → `handle` (`deliver`, then `o.Kept`); `cycle` timeout path calls nothing | confirmed |
| 7 | `Kept` never overlaps `Collect` of the same collector: the runner calls it after receiving the worker's result, and after an abandoned run starts no `Collect` until that worker returned | `runner.go` `collect` (channel receive), `cycle` (`r.pending` check before a new run) | confirmed |
| 8 | Adding a `Kept` method is enough for the scheduler to call it: it type-asserts the job's collector | `runner.go` `finish`: `r.job.Collector.(Observer)`; `agent.go:270,283` pass `*Kernel` and `*Backups` directly | confirmed |
| 9 | Every state save first syncs the spool; the spool syncs once a second; a hand-over is written without `fsync` | `agentstate.go:336-353`, `agent.go:404`; `docs/areas/agent.md` *Durability and the stop* | confirmed |
| 10 | The kernel's journal position is already saved only after the hand-over (committed at the next run's start when `!PreviousLost`) | `kernevents.go:121-147` (`commit`) | confirmed |
| 11 | `Saver.prepare` refreshes the uptime of whatever boot the state holds, and its first run is a heartbeat | `agentstate.go:760-778`, `lastSave` zero at `Open` | confirmed |
| 12 | `internal/agentstate` may import `internal/procstats` (for `ReadBootID`) without a cycle | `go list -deps ./internal/procstats`: model, wire, scheduler only | confirmed |
| 13 | Records 0120 and 0123 are unreleased (edited in place) | `git ls-remote --tags origin`: only `v0.1.0`; `git ls-tree v0.1.0 docs/decisions/` holds neither | confirmed |
| 14 | A `kill -9` between a run's hand-over and the next state save already repeats the boot event or backup start/end at the restart today; the change keeps that window, it does not widen it | today the change sits in memory until the save just as after `Kept`; `Kept` follows `deliver` in the same goroutine | confirmed |

## Acceptance criteria

- [ ] AC1: After the first run in a new boot returned the `boot` event and before `Kernel.Kept` reported that run, `store.State().Boot`
  is the previously saved boot (or nil). A state save at that moment (`agentstate.Saver.Collect`) writes that previous boot with its
  saved uptime, and a `Kernel` started on the saved file in the same boot returns the `boot` event again with the same `boot_id`,
  `previous_boot_id` and `previous_uptime_ns` (the previous boot's saved uptime).
- [ ] AC2: `Kept(run, true)` for the latest run that returned the `boot` event sets the state's boot to the current boot (`ID`, `BootedAt`,
  the uptime of `/proc/uptime` read at that moment, 0 when unreadable) as an urgent change; later runs, and a `Kernel` started on the
  saved file in the same boot, return no `boot` event.
- [ ] AC3: `Kept(run, false)`, no report at all (an abandoned run, or a power loss before the report), or a report for a `run.Start`
  other than the latest run that returned the event leaves the state's boot unchanged, and the next run returns the event again; a
  later `Kept(true)` for a run that returned it saves it.
- [ ] AC4: For a check that starts a backup run, raises its process count, moves its last sighting, or ends it, `store.State().Backup`
  is unchanged until `Backups.Kept(run, true)` for that check; a state save before it writes the previous open run, and a collector
  started on the saved file returns the start (or the end) again. After `Kept(true)` the state holds the change, urgent for a start,
  an end and a higher count, not urgent for a sighting alone (as today).
- [ ] AC5: After `Kept(run, false)` or no report, the open run, the gone-by time and the first-check flag are as before the check, so the
  next check decides it again — the four cases of `TestBackups_LostRunIsDecidedAgain` (dropped start while the process runs, dropped
  start after it ended, dropped end with the gone-by of the last kept check, dropped first check after a restart) — whatever the next
  run's `Run.PreviousLost` says.
- [ ] AC6: A save of the `state` job refreshes the stored boot's uptime from `/proc/uptime` only when the stored boot's `ID` equals the boot
  ID of its proc (`procstats.ReadBootID(v.proc)`); a stored boot of another ID, or an unreadable or malformed boot ID, keeps its saved
  uptime, at the heartbeat and at an urgent save (a save due for another change still happens; the heartbeat makes no change of its
  own then).
- [ ] AC7: `*kernevents.Kernel` and `*backups.Backups` implement `scheduler.Observer`, and through the real scheduler a kept run's boot
  reaches the state saved at the stop.

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `TestBoot_SaveBeforeTheHandOverKeepsThePreviousBoot` (subtests: a previous boot saved; no boot saved, first start) | `internal/kernevents/boot_test.go` |
| AC2 | `TestBoot_KeptRunSavesTheCurrentBoot` (subtests: uptime readable; `/proc/uptime` missing → 0); the adapted `TestBoot_NewBootRecordsPreviousBootAndUptime`, `TestBoot_UptimeIsZeroWhenProcUptimeIsUnreadable` | `internal/kernevents/boot_test.go` |
| AC3 | `TestBoot_UnkeptRunLeavesTheSavedBoot` (subtests: reported not kept; never reported; report for another start); `TestBoot_EventIsCarriedAgainUntilARunIsKept` unchanged in its calls | `internal/kernevents/boot_test.go`, `internal/kernevents/coverage_test.go` |
| AC4 | `TestBackups_SaveBeforeTheHandOverKeepsThePreviousOpenRun` (subtests: start, higher count, sighting, end) | `internal/backups/backups_test.go` |
| AC5 | the adapted `TestBackups_LostRunIsDecidedAgain` (dropped check reported `Kept(false)`); `TestBackups_UnreportedCheckIsDecidedAgain` (no report; next run with and without `PreviousLost`) | `internal/backups/backups_test.go` |
| AC6 | `TestSaverRefreshesOnlyTheUptimeOfTheCurrentBoot` (heartbeat and urgent save × current boot, another boot, boot ID missing, malformed) | `internal/agentstate/agentstate_test.go` |
| AC7 | `TestKernel_IsASchedulerObserver`; `TestBackups_IsASchedulerObserver`; `TestRun_SavesTheStateWhenItStops` extended: the saved `Boot.ID` is the world's boot (`kernelBootRaw`); the existing `TestNewScheduler_BackupsAndStateReadTheKernelProc` (one start in 25 s needs the kept start in the state) unchanged | `internal/kernevents/kernevents_test.go`, `internal/backups/backups_test.go`, `internal/agent/agent_test.go` |

`TestKernel_IsASchedulerObserver`, `TestBackups_IsASchedulerObserver` and the `TestRun_SavesTheStateWhenItStops` assertion cannot fail
on the skeleton (it adds the methods, and `Collect` still writes the boot): deferred to step 6 as *Tests first* allows.

## Approach

**Kernel.** `bootCheck` keeps deciding and building the event once, at the first run, but no longer writes: it keeps the boot to save
(`pendingBoot`: ID and boot time). `Collect` records the run's start in `bootStart` whenever it emits `k.boot`. `Kept(run, kept)`:
when `kept`, `k.pendingBoot != nil` and `run.Start.Equal(k.bootStart)`, it calls `saveBoot` (reads `/proc/uptime` then, urgent update
as today) and clears `pendingBoot` and `k.boot`; otherwise it does nothing. `commit` keeps clearing `k.boot` (harmless; the next run
after a kept one has `PreviousLost` false). The journal position stays on its `PreviousLost` commit (fact 10).

**Backups.** `Collect` computes the check's outcome from the committed state — the store's open run, `b.goneBy`, `b.checked` — without
writing: `advance` returns the records and a `change` (the open run after the check, whether the state's run changes, whether that is
urgent, the new gone-by, and the run's start). `Collect` stores it in `b.pending`, replacing any earlier one (an abandoned check's).
`Kept(run, kept)`: when `b.pending` matches `run.Start` and `kept`, it applies it (`store.Update(urgent, …)` only when the run
changes; `b.goneBy`, `b.checked = true`); in every case it clears `b.pending`. `remember`, `memory`, `before`, `sameRun` and the
store writes of `begin`, `see`, `clear` go; `Run.PreviousLost` is no longer read by this collector. A `Collect` that fails (boot ID,
boot time, listing, deadline) sets no `pending`.

**Saver.** Both refresh sites of `prepare` take the uptime through `currentUptime(s.state.Boot)`, which returns `ReadUptime(v.proc)`
only when `procstats.ReadBootID(v.proc)` succeeds and equals the boot's `ID`; it runs under `s.mu` like `ReadUptime` today.

**Test harnesses (Tester, step 5).**

- `internal/kernevents/harness_test.go`: the env keeps the last run not yet reported. `collect(lost)` first reports it with
  `Kept(prev, !lost)` (what the scheduler did right after its hand-over); `keep()` reports it kept now; `abandon()` marks it never
  reported; `newKernel()` reports it kept first unless abandoned (the scheduler reports every run that finished in time before a stop).
  Existing tests that read the state right after a run (`TestBoot_NewBootRecordsPreviousBootAndUptime`,
  `TestBoot_UptimeIsZeroWhenProcUptimeIsUnreadable`) call `keep()` first.
- `internal/backups/harness_test.go`: `runWith` reports `Kept(run, true)` after `Collect`; new `runDropped(sec)` (`Kept(run, false)`)
  and `runUnreported(sec)` (no report).
- `internal/agentstate/agentstate_test.go`: `procFS` also holds `sys/kernel/random/boot_id` with `bootID` (the boot `fullState`
  stores), so the existing uptime tests keep their meaning.

No new user-visible output (no log line, no record field).

## Options considered

1. **`Kept` on both collectors** (chosen) — the order the `logs` collector already uses; the change reaches the state right after the
   hand-over, so the spool barrier covers it.
2. **Kernel: save the boot in `commit` at the next run** (no new method) — a stop between the kept run and the next one leaves the boot
   unsaved and the restart records the event again; it also leaves two mechanisms for one collector's state.
3. **Backups: keep the restore on `PreviousLost` and only defer the write** — two mechanisms for the same rollback; `Kept` alone already
   leaves a dropped check without effect.
4. **Backups: commit at the next kept check**, as the journal position — rejected in record 0120 (option 10): the stored run lags one
   more check.
5. **Move the kernel's journal-position commit to `Kept` too** — not part of the defect (fact 10) and it would rework the re-read and
   give-up logic of record 0120 option 8; out of scope.
6. **Saver: leave the uptime refresh as is** — keeps a wrong `previous_uptime_ns` after a start-up race that the fix makes longer;
   **cache the boot ID once in the Saver** — saves a 64-byte read per due save at the cost of state; one read per refresh is simpler.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| agent (Go) | `internal/kernevents/boot.go`, `kernevents.go` (`Kernel`) | `Kept`; `bootCheck` stops writing; `Collect` notes `bootStart` |
| agent (Go) | `internal/backups/backups.go` (`Backups`) | `Kept`, `change`, `apply`; `advance` returns the change; restore machinery removed |
| agent (Go) | `internal/agentstate/agentstate.go` (`Saver`) | `currentUptime`; `prepare` refreshes only the current boot |
| docs | `docs/areas/agent.md`, `docs/ARCHITECTURE.md` | see *Documentation updates* |
| records | `docs/decisions/0120-…`, `0123-…` | edited in place (done by the Lead) |

## Signatures (for the Dev's skeleton)

```go
// internal/kernevents (boot.go)
// Kept saves the boot of the boot event when the scheduler reports the run that last returned it kept; any other report changes nothing.
func (k *Kernel) Kept(run scheduler.Run, kept bool)
// Kernel gains: pendingBoot *agentstate.Boot (ID and BootedAt of the boot to save, nil when none); bootStart time.Time.
// unchanged: func (k *Kernel) bootCheck(start time.Time) *model.Record; func (k *Kernel) saveBoot(id string, bootedAt time.Time)

// internal/backups (backups.go)
// Kept applies the change of the check run when the scheduler reports its records kept and forgets it otherwise.
func (b *Backups) Kept(run scheduler.Run, kept bool)
// change is what one check decided for the collector's state.
type change struct {
	start  time.Time           // the check's run start, matched by Kept
	open   *agentstate.OpenRun // the open run after the check, nil for none
	write  bool                // whether the state's open run changes
	urgent bool                // a start, an end or a higher process count
	goneBy time.Time           // the gone-by time after the check
}
func (b *Backups) advance(now time.Time, bootID string, found sighting) ([]model.Record, change)
func (b *Backups) apply(c *change)
// Backups: field before *memory replaced by pending *change; removed: memory, remember, sameRun, begin, see, clear.

// internal/agentstate (agentstate.go)
// currentUptime returns the uptime of proc when boot is the boot of proc.
func (v *Saver) currentUptime(boot *Boot) (time.Duration, bool)
```

The skeleton adds both `Kept` methods as stubs and `currentUptime` returning `ReadUptime(v.proc)`, leaving behavior unchanged.

## Test files

`internal/kernevents/boot_test.go`, `internal/kernevents/kernevents_test.go`, `internal/kernevents/harness_test.go`,
`internal/backups/backups_test.go`, `internal/backups/harness_test.go`, `internal/agentstate/agentstate_test.go`,
`internal/agent/agent_test.go`.

Existing test code that calls a changed signature: none — `advance`, `begin`, `see`, `clear`, `remember` have no test callers. Existing
tests whose input the new criteria handle differently, adapted by the Tester in step 5: the three harnesses above;
`TestBoot_NewBootRecordsPreviousBootAndUptime`, `TestBoot_UptimeIsZeroWhenProcUptimeIsUnreadable` (`keep()`);
`TestBackups_LostRunIsDecidedAgain` (dropped check via `runDropped`); the agentstate uptime tests through `procFS`. Kept unchanged:
`TestBackups_MalformedBootIDFailsTheRun` (a failing `Collect` sets no change) and the kernel position tests (only the boot moves).

Files no unit test can cover: none.

## Areas

`docs/areas/agent.md` (Agent): *Scheduling*, *Kernel events* (residuals), *Boots*, *Backup runs*, *Agent state*, *Spool and sending* —
the boot and the open backup run reach the state only after a kept run, two residuals close, the uptime refresh is bound to the
current boot.

## Documentation updates

All by the Dev, against the code as built:

- `docs/areas/agent.md` *Scheduling*: the `Run.PreviousLost` bullet no longer names `backups`; the observer bullet names `logs`,
  `kernel` (the boot) and `backups` as observers.
- *Kernel events*, *Residuals*: remove the bullet on the boot change written before the hand-over (record 0123, #300).
- *Boots*: the state's boot becomes the current one when the scheduler reports a run that returned the event kept (uptime read then);
  until then the state holds the previous boot, so a restart before it records the event again; the `state` job keeps the uptime
  current only for the current boot.
- *Backup runs*: *Lost runs* rewritten — a check's change (open run, gone-by, first check) takes effect only when the scheduler reports
  it kept; a dropped or abandoned check changes nothing and the next one decides again. *Residuals*: remove the bullet on a state saved
  between a dropped check and the next one, and the record-0123 bullet.
- *Agent state*, *Saving*: "Every save first refreshes the stored uptime" → only for a stored boot that is the current boot; remove the
  parenthesis "(the exception is the boot and backup change of record 0123 …)".
- *Spool and sending*, *Durability and the stop*: remove "The exception is the boot and backup change written before the hand-over …
  #300", and name the boot and the open backup run among what every state save's sync covers.
- *Related decisions*: the 0120 bullet adds "why boots and backup runs reach the state only once a run is kept".
- `docs/ARCHITECTURE.md` (agent component paragraph, "the kernel collector's journal position is committed only after a run whose
  result was kept"): extend with "and the boot and the open backup run are written to the state only once the scheduler reports the
  run's records kept", linking 0120.

Sweep: `grep` of `docs/areas/*.md`, `docs/ARCHITECTURE.md`, `.squad/project.md`, `README.md`, `deploy/agent/README.md` for `#300`,
`0123`, `observer`, `Kept`, `PreviousLost`, `uptime`: the hits above are all; `.squad/project.md` names no observer list and no
residual of this kind (no change).

## Architecture check

The guarantee *No data gaps unless explicitly recorded* is restored for the two records: no state save can hold a boot or backup
change whose record the spool has not taken. *A hanging collector never blocks the agent*: `Kept` runs in the runner goroutine and
does only in-memory work plus one `/proc/uptime` read (kernel, as before inside the run); the Saver's extra `boot_id` read is bounded
(64 bytes) like the uptime read beside it. No guarantee is weakened.

## Security considerations

- [x] No limits on parsed input change: `procstats.ReadBootID` keeps its 64-byte bound and strict UUID check.
- [x] Exceptions that reach a user-visible message: none (no new log line or error text).

## Decision records

- `docs/decisions/0120-oom-kill-events-triggered-snapshots-boots-from-saved-state-and-backup-runs.md` (Accepted, unreleased, edited in
  place): option 10 rewritten (backup change applied on `Kept`), new option 11 (boot saved on `Kept`, uptime refresh bound to the
  current boot), the consequence on a state saved between a dropped run and the next one replaced.
- `docs/decisions/0123-agent-spool-segment-files-clock-bound-sequence-numbers-range-acknowledgements.md` (Accepted, unreleased, edited
  in place): option 3 no longer excludes the boot and backup state; the stated residual and its condition removed.

## Out of scope / follow-ups

- The kernel's journal-position commit stays on `Run.PreviousLost` (option 5).
- How the backend treats a repeated `boot` event or backup start after a `kill -9` (fact 14) is unchanged.

## Challenge notes

Devil's Advocate, plan round 1 (0 major, 3 minor), appended verbatim:

1. minor — The boot can only be lost through one invariant, so state it and test it. Dev: `k.boot` and `k.pendingBoot` must always be set and cleared together. `commit` (kernevents.go:126-133) clears `k.boot`; if it ever ran while `pendingBoot` was still set, the boot would never be saved and the event never emitted again. Make `commit` clear `pendingBoot` only together with a save, never on its own; better, have `Kept` be the only place that clears either one. Tester: add a case to `TestBoot_UnkeptRunLeavesTheSavedBoot` that runs the next run with `PreviousLost` false after a `Kept(false)`. It must still return the event.
2. minor — AC6 only tests `currentUptime` in isolation, not the start-up race the plan describes. Tester: add one case to `TestSaverRefreshesOnlyTheUptimeOfTheCurrentBoot`, or a kernel test in `boot_test.go`, that runs the Saver's first heartbeat save on a store holding the previous boot before the kernel's first run in a new boot. The resulting `boot` event must carry the previous boot's saved uptime as `previous_uptime_ns`, and the file must still hold that uptime.
3. minor — For a check whose `Collect` fails, the plan says it "sets no pending", but it does not say whether an earlier `pending` (from an abandoned check) survives. Dev: clear `b.pending` at the start of every `Collect`, so a later `Kept` can never apply a stale change. The `run.Start` match makes this safe either way, but this way it is explicit.
