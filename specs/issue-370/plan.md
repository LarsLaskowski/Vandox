# Plan: Stress-run load-dependent Go tests under CPU load

Source: Issue #370
Tier: standard — a new CI job and the script it runs change what CI checks, but create, change or remove no security control: the workflow runs on `pull_request` with a read-only token, no secret and no cache it publishes, like the existing `ci.yml` jobs.
Security areas touched: CI workflow (new `.github/workflows/go-stress.yml`: triggers, permissions, the actions it uses); release pipeline and published artifacts (area 13) only through its directory `.github/scripts/` (new `stress-go-tests.sh`, which the release does not use)

## Problem / root cause

No CI step runs the Go tests under load. The only Go test runs are `go test ./... -race -cover` in the `agent` job
(`.github/workflows/ci.yml:41-42`) and `go test ./... -race -coverprofile=coverage.out` in the `sonarqube` job
(`ci.yml:122`), each once per pull request; the load a test sees is whatever the other test binaries of that run happen to
cause. A test whose verdict depends on scheduling, a duration ratio or the collector's timing therefore fails only by chance —
as `TestScheduler_TriggerWhileStuckRecordsGap` and `TestConnections_ManySocketsStayWithinCaps` did (#336) and
`TestHTTPTransport_ReadsAtMostFourKiBOfBody` (#329, fixed by #369).

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | Scheduling-dependent bounds failed on CI runners and passed on a re-run (#329, #336) | #329 body (sender 4 KiB test, connstats heap peak, on PR #324's runs); PR #336 body (scheduler gap `to 20s` want `10s`, connstats 1,032 bytes over) | confirmed |
| 2 | Nothing runs Go tests under load before merge today | `ci.yml` is the only workflow with `go test` (`grep -n "go test" .github/workflows/*.yml`: lines 42 and 122), both single runs without load | confirmed |
| 3 | The whole suite is too slow to run 20 times | `go test ./... -race -count=1` on 4 CPUs: 2 min 19 s wall; `internal/sender` 90 s, `internal/spool` 71 s, `internal/wire` 29 s | confirmed |
| 4 | The selected tests (*Approach*) take about 25 s for one plain run | `go test ./internal/... -race -count=1 -run '^(…)$'` over the list: 19 s wall; longest `connstats` 5.7 s, `logship` 4.7 s, `wire` 4.4 s, `sender` 4.3 s; the three `spool` tests about 6 s together | confirmed |
| 5 | `TestKernel_TimeBudgetEachRunGetsFurther` is not load-dependent | it runs inside `synctest.Test` (`internal/kernevents/limits_test.go:96`), so its clock is the bubble's | confirmed |
| 6 | The heap tests of the list read a heap size sampled through `runtime.ReadMemStats` (`HeapAlloc`) or `runtime/metrics` (heap objects), whose value depends on when the collector runs | helpers `heapAfterGC` (journal_test.go:1182), `retainedHeap` (stages_test.go:670), `heapNow`/`newHeapSampler` (sender_test.go:1915-1950), `liveHeap` (logship_test.go:576), `live` (zstdfield_test.go:243), `heapObjects`/`peakHeapGrowth` (spool/open_test.go:306-315), `measureRun` (connstats), `TestKernel_BacklogMemoryIsBounded` (limits_test.go:188) | confirmed |
| 7 | GitHub's `paths` filter applies per workflow, not per job | GitHub Actions workflow syntax: `on.<pull_request>.paths`; jobs have only `if:` | confirmed |
| 8 | All listed tests pass 20 runs each under the planned load | to verify: Dev, step 6, local run of the script (sandbox has 4 CPUs), then the job's run on the draft PR. A listed test that fails is reported, not dropped: the Lead decides (mode `decide`) | unverified |
| 9 | Two busy loops per CPU make a scheduling-dependent bound fail | to verify: AC2's dry run (Tester locally, then CI on the draft PR). #329's plan saw a past drain race *not* reproduce with one busy loop per CPU, hence two | unverified |
| 10 | The job fits its timeout on `ubuntu-latest` (expected 4 CPUs) | to verify: orchestrator, the job's run on the draft PR: the script prints `nproc` and the elapsed time | unverified |

## Acceptance criteria

- AC1: A workflow `.github/workflows/go-stress.yml` with one job, *Go tests under CPU load*, runs on every pull request to `main`
  that changes a file under `internal/`, `go.mod`, `go.sum`, `.github/scripts/stress-go-tests.sh` or the workflow itself, and on
  `workflow_dispatch`; on no other event. It runs `.github/scripts/stress-go-tests.sh`, which runs every listed test with
  `go test -race -count=20` while two busy-loop processes per CPU (`nproc`) run, and the job fails when any of those runs fails.
- AC2: A test with a scheduling-dependent bound, added to the list, fails in that job; the job passes again once it is removed.
- AC3: The script fails closed, with a `::error::` line naming the cause and a non-zero exit: a listed test that does not exist in
  its package (checked before the load starts), and a load process that is gone when the test run ends.

## Tests per criterion

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | CI: the job's run on this pull request (its own workflow and script trigger it), green | none |
| AC2 | CI: dry run on the early draft PR (*Verification without tests*) | none |
| AC3 | check: Dev's scratch runs (*Verification without tests*) | none |

## Verification without tests

The final diff holds no production or test code: steps 4 (*Skeleton*), 5 (*Tests first*) and the *Coverage gate* are not
applicable. Step 6 is the Dev's edits, then the Tester's planted dry-run test and the test documentation. **Early draft PR:**
yes — the first two criteria need CI results before the review (`.squad/routing.md`, *Early draft PR*). Before the push the Dev
runs the script locally once on the branch (step 6) and quotes the outcome and the elapsed time; the Reviewer checks the trigger
list by reading the `on:` block; step 7 runs the *Analyzer gate* (actionlint, shellcheck, the shell rules).

- AC1: CI: orchestrator, the job's run on the draft PR head: green, its log shows `nproc`, the number of load processes (2 ×
  `nproc`), the list and `-count=20`.
- AC2: CI: dry run. The Tester writes a planted test in a scratch file `internal/scheduler/stressdryrun_test.go` whose bound depends
  on scheduling (suggested: a CPU-bound loop that bounds wall-clock time over the process's CPU time at 1.5; layout free), adds it to
  the script's list, and shows locally that it passes `go test -race -count=20` without load and fails under the script. The
  orchestrator commits both as one dry-run commit on the draft PR, records the failing job run (URL and the test's failure line) in
  `log.md`, then pushes a `git revert` of that commit (no force push) and records the green run. The planted test never reaches
  `main` (squash merge; the reverted diff holds none of it — the Reviewer checks).
- AC3: check: Dev, step 6, in a scratch copy of the script: (a) one list entry misspelled → `::error::` naming package and test,
  exit non-zero, and no load process started; (b) one busy loop killed during the run (`kill` of one PID the script printed) →
  `::error::` naming the lost load, exit non-zero, and no load process left afterwards (`pgrep` count before and after).

## Approach

**Script `.github/scripts/stress-go-tests.sh`** (bash, `set -euo pipefail`, no arguments, run from any directory: it changes to
the repository root derived from its own path):

1. The list: a `readonly` array of entries `<package path> <TestName>`, grouped by the reason it is listed, with a one-line comment
   per group (the rule of `docs/UNIT_TESTS.md`):
   - failed intermittently in CI: `internal/sender TestHTTPTransport_ReadsAtMostFourKiBOfBody`,
     `internal/connstats TestConnections_ManySocketsStayWithinCaps`, `internal/scheduler TestScheduler_TriggerWhileStuckRecordsGap`;
   - a ratio of measured durations: `internal/wire TestDecoder_TimeIsInProportionToTheLine`,
     `internal/logstages TestClassifyMail_TimeGrowsLinearlyWithTheLengthOfTheMessage`;
   - a sampled heap size: `internal/journal TestFile_MemoryDoesNotGrowWithJournalSize`,
     `internal/kernevents TestKernel_BacklogMemoryIsBounded`, `internal/logship TestLogs_HeapStaysBounded`,
     `internal/logstages TestStages_BuffersExistOnlyWhileAGroupIsOpen`, `internal/sender TestSender_MemoryBound`,
     `internal/spool TestSpool_MemoryBound`, `internal/spool TestOpen_TooManyFiles`, `internal/spool TestOpen_ManyForeignEntries`,
     `internal/zstdfield TestDecoder_HeapStaysBoundedForTheLargestFrame`.
2. Existence check, before any load: for each entry, `go test -list "^<TestName>$" ./<package>` must print the name as a line of
   its own; otherwise `::error::` "listed test <package> <TestName> not found" and exit 1 after checking all entries.
3. Build first, without load: `go test -race -count=1 -run '^$'` on the listed packages compiles the race binaries into the
   build cache, so the load slows the tests, not the compiler.
4. Load: `nproc` and the load count (2 × `nproc`) printed; that many `bash -c 'while :; do :; done'` started in the background,
   their PIDs kept and printed; a `trap … EXIT` kills and waits for them on every exit.
5. Run: one `go test -race -count=20 -timeout 30m -run '^(<names joined by |>)$' <packages>`, its exit status captured
   (`|| status=$?`). A name that also exists in another listed package runs there too, which only adds runs.
6. After the run: every load PID is checked with `kill -0`; a gone one gives `::error::` "load process <pid> ended before the test
   run finished" and exit 1. Otherwise the script exits with the test run's status and prints the elapsed seconds.

The *Analyzer gate* shell rules apply: `[[ … ]]`, positional parameters into named `local` variables, a literal used three times
or more declared once as `readonly`.

**Workflow `.github/workflows/go-stress.yml`:** `name: Go stress`; `on: pull_request` (branches `[main]`, `paths`: `internal/**`,
`go.mod`, `go.sum`, `.github/scripts/stress-go-tests.sh`, `.github/workflows/go-stress.yml`) and `workflow_dispatch`; top-level
`permissions: contents: read`; one job `go-stress`, `name: Go tests under CPU load`, `runs-on: ubuntu-latest`,
`timeout-minutes: 45`; steps: `actions/checkout` and `actions/setup-go` pinned to the same SHAs as `ci.yml` (checkout with
`persist-credentials: false`, setup-go with `go-version-file: go.mod`, `cache: true`), then `run: .github/scripts/stress-go-tests.sh`.
No `${{ }}` in a `run:` script, no secret, no artifact. A short comment above the job names the script and record 0196.

User-visible output is the job's log only: layout free for the Dev, approved in step 9.

Runtime details no one can verify before CI (facts 8–10): the runner's CPU count, the job's duration under load, whether the
load reproduces a flake. Each fails closed: a missing test or lost load fails the job (AC3); a slow run hits `-timeout`/
`timeout-minutes` and fails; nothing is skipped on an error.

## Options considered

1. **A listed selection, run by a script under load in a path-filtered workflow** (chosen) — fits a job of minutes (fact 4).
2. **The whole Go suite 20 times under load** — about 45 min of plain runs (fact 3).
3. **The packages the pull request changes, 20 times** — `internal/sender` alone is about 30 min, and the job needs diff logic.
4. **A job in `ci.yml` with change detection** — needs a third-party paths-filter action or a `git diff` step; `paths` works only
   per workflow (fact 7).
5. **`stress-ng` as the load** — an unpinned package download in the job; busy loops need nothing.
6. **`-cpu 1,2,4,8`** (the #336 reproduction) — four times the runs; the issue asks for `-count=20`.
7. **Opt-in by a test-name suffix instead of a list** — renames existing tests.
8. **A canary test committed to `main` for AC2** — a test that fails under load would keep the job red; the dry run shows the same
   without one.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| CI | `.github/workflows/go-stress.yml` | new workflow, one job |
| CI | `.github/scripts/stress-go-tests.sh` | new script (executable, `git update-index --chmod=+x` or mode 100755) |
| Docs | `docs/UNIT_TESTS.md`, `.squad/stack.md` | see *Documentation updates* |
| Records | `docs/decisions/0196-…md`, index | new, Proposed |

## Signatures (for the Dev's skeleton)

None: no Go or C# API changes. The script takes no arguments and exits 0 on success, 1 on a missing test or lost load, and the
test run's status otherwise.

## Test files

None: the final diff holds no test code. The planted test of AC2 (`internal/scheduler/stressdryrun_test.go`) exists only in the
reverted dry-run commit.

Existing test code that calls a changed signature: none.

Files of the change no unit test can cover: `.github/scripts/stress-go-tests.sh` and the workflow — CI glue that starts processes
and runs `go test`; verified by the checks above. Not in any coverage measure (`COVERAGE_PATHSPECS` holds no `.github/`).

## Areas

None: no product behavior changes; the CI job is described in `docs/UNIT_TESTS.md`.

## Documentation updates

- `docs/UNIT_TESTS.md` (Tester): a short section *Load-dependent tests (Go)* after *Structure*: what the `Go stress` job runs and
  when (link to record 0196), the selection rule — a Go test is listed in `.github/scripts/stress-go-tests.sh` when its verdict
  depends on a ratio of measured durations, on a heap size sampled while or after the code runs (`runtime.ReadMemStats`,
  `runtime/metrics`), or on goroutine scheduling, or when it failed intermittently in CI — and the local command (run the script
  from the repository root). A *Checklist for new tests* item: "Go: a test of that kind is listed in
  `.github/scripts/stress-go-tests.sh`".
- `.squad/stack.md` (*Commands*, paragraph *Test time limits*; orchestrator, from the Dev's text): "CI runs `go test` without
  `-timeout`" becomes true only for the `agent` and `sonarqube` jobs; add that the `Go stress` job sets `-timeout 30m` for its 20
  runs under load.
- `CHANGELOG.md`: none (per release; no user-visible change). `docs/ARCHITECTURE.md`, `README.md`, `.squad/project.md`: none
  (sweep: no enumeration of CI jobs; area 13 covers `.github/scripts/` as a whole).

## Architecture check

No guarantee of `.squad/project.md` is touched: no product code, no release job, no secret or permission beyond `contents: read`.

## Security considerations

- [ ] No limits: the script parses no external input (its list is a constant).
- [ ] None: no exception reaches a user; the job's log shows `go test`'s own output.
- The workflow runs untrusted pull request code exactly like `ci.yml` does (`pull_request`, not `pull_request_target`), with a
  read-only token that checkout does not persist and no secret; `setup-go`'s cache is read and written as in `ci.yml` and is not a
  release input (releases restore no cache, `docs/CONTRIBUTING.md`, *Workflow rules*).

## Decision records

- `docs/decisions/0196-load-dependent-go-tests-are-stress-run-from-a-list-in-a-path-filtered-workflow.md` (Proposed)

## Out of scope / follow-ups

- Making the job a required check: a `paths`-filtered workflow that does not run would leave a required check pending (record 0196).
- Tests that bound a process-wide allocation total (`TotalAlloc`, e.g. `TestSession_DiscardsUnsolicitedSignalsWithoutKeepingThem`)
  and real-clock tests with injected durations (record 0062): not listed under the rule; a flake among them widens the rule.

## Security notes

- **Non-blocking (Security, plan review):** the *Workflow rules* in `docs/CONTRIBUTING.md` (lines 249–253) bind only `release.yml`, `base-image-digests.yml` and the *Release build check* job. The new `go-stress.yml` follows them by choice in the plan, and nothing keeps a later edit to it doing so. Smallest fix: add one line to the workflow's header comment. Insert verbatim, right below the planned comment that names the script and record 0196:
  `# Actions pinned by full commit SHA, checkout without persisted credentials, no ${{ }} inside a run: script (docs/CONTRIBUTING.md, Workflow rules).`
