# 0196: Load-dependent Go tests are stress-run from a list in a path-filtered workflow

- **Status:** Proposed
- **Date:** 2026-11-10
- **Area:** —
- **Source:** Issue #370
- **Supersedes:** —

## Context

Go tests whose verdict depends on runner load failed intermittently in CI and passed on a re-run: the scheduler gap test and the
connstats heap bound (#336), the sender's 4 KiB cut test (#329, #369). CI runs `go test ./... -race` once per pull request, so such a
test shows up only as a random red run on an unrelated pull request. The issue asks for a CI job that runs selected tests with
`-race -count=20` while other processes load the CPU, with its runtime and trigger in proportion. One plain run of the whole Go suite
with `-race` takes about 2 min 20 s on 4 CPUs, of which `internal/sender` takes 90 s and `internal/spool` 71 s; record
[0074](0074-two-language-toolchain-and-combined-quality-gates.md) set up the existing Go job, which this does not change.

## Options considered

1. **A listed selection of load-dependent tests, run by a script under CPU load in a workflow with a `paths` filter** (chosen) —
   one plain run of the selection takes about 25 s summed over its packages, so 20 runs under load fit a job of minutes; the list
   is kept by a rule in `docs/UNIT_TESTS.md`, and a test missing from it is not stress-run.
2. **The whole Go suite 20 times under load** — needs no list, but costs about 45 min of plain runs before the load slows them.
3. **The packages a pull request changes, 20 times under load** — follows the change, but one change to `internal/sender` alone
   costs about 30 min of plain runs, and the job needs its own diff logic.
4. **A job in `ci.yml` with change detection** (a third-party paths-filter action or a `git diff` step) — keeps one workflow, but
   adds an action to pin and trust, or diff logic; GitHub's own `paths` filter works only per workflow.
5. **`stress-ng` from the runner's package manager as the load** — a finer load model, but an unpinned download in the job; plain
   busy-loop processes need nothing.
6. **`-cpu 1,2,4,8` as in the #336 reproduction** — varies `GOMAXPROCS`, but multiplies the runs by four.
7. **A test opts in by a name suffix instead of a list** — needs no list, but renames existing tests and their history.

## Decision

A separate workflow, `.github/workflows/go-stress.yml`, runs on pull requests to `main` that change `internal/`, `go.mod`, `go.sum`,
the script or the workflow, and on manual dispatch; it runs `.github/scripts/stress-go-tests.sh`, which runs the listed tests with
`go test -race -count=20` while two busy-loop processes per CPU run, and fails when a listed test fails, no longer exists, or the
load ends early. The selection rule and the local command are in [`docs/UNIT_TESTS.md`](../UNIT_TESTS.md).

## Consequences

- A load-dependent test fails on the pull request that adds or changes it, provided it is listed; the reviewer of a new test checks
  the rule (the checklist item in `docs/UNIT_TESTS.md`).
- The job is not a required check: a `paths`-filtered workflow that does not run would leave a required check pending. A red run
  is still visible on the pull request, and the squad's *Draft until green* waits for it.
- Pull requests that do not touch the agent's packages pay nothing; one that does waits a few more minutes for this job.
- To revisit: if listed tests become too slow, split the list or lower the count; if a flake escapes the list, the rule is
  widened, not the job turned into a full-suite run.
