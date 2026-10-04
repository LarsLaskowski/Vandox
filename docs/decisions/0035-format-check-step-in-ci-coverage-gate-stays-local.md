# 0035: Explicit format check step in CI; the coverage gate stays local, SonarQube measures coverage in CI

- **Status:** Accepted
- **Date:** 2026-10-04
- **Source:** Issue #8
- **Supersedes:** —

## Context

Issue #8 asks for CI that checks every pull request, with the *Format check*, `go vet`, golangci-lint and
govulncheck "as configured in `.squad/stack.md`" and the "Coverage gate as defined by the template", and
names "CI is green on every PR" as an acceptance criterion. The template defines the *Coverage gate* in
two places: `.squad/stack.md` (`python3 .squad/tools/coverage-check.py`, at least 80 % line coverage on new
or changed production lines and overall) and decision 0001 (source: the template), which runs it locally
before the push and leaves CI with the build, the tests and the code analysis, with SonarQube Cloud as the
system of record. The `sonarqube` job already sends `coverage.out` to SonarQube Cloud
(`sonar.go.coverage.reportPaths`), whose quality gate measures coverage on new code. No CI step runs the
format check by name; golangci-lint reports an unformatted file only through its `gofmt` formatter.

Accepted coverage gaps exist by design: 0033 accepted a red overall gate for one change, and 0034 /
`docs/UNIT_TESTS.md` accept each binary's uncovered `main` body. Overall line coverage on `main` is 81.8 %
(27 of 33 lines), so one more such block would bring it under 80 %.

## Options considered

1. **Read the requirement through 0001: coverage gate local, SonarQube in CI; add only an explicit *Format
   check* step** — matches the template's own definition of the gate, keeps 0001 intact and the issue's
   "CI is green on every PR" achievable; a push that skipped the local gate is caught by SonarQube's
   new-code coverage condition rather than by the script.
2. **Run `coverage-check.py` in CI as a blocking step, superseding 0001** — the same named gate everywhere,
   but a Lead-accepted gap (0033, 0034) turns the PR's CI red by design, which contradicts the issue's
   acceptance criterion and the squad's step-11 exit criterion "CI green"; with 81.8 % overall the margin is
   small. Rejected after the plan challenge.
3. **Run it in CI as report-only (`continue-on-error`)** — never red, but a gate nobody has to pass is not a
   gate and duplicates SonarQube's figure. Rejected.

## Decision

Option 1. `.github/workflows/ci.yml` job `build-test-lint` gets a *Format check* step before *Build* that
fails when `gofmt -l .` lists a file — an explicit form of a check CI already runs through golangci-lint,
within 0001's "CI keeps the build, the tests and the code analysis". The *Coverage gate* stays a local gate
before the push, as 0001 decides; in CI, SonarQube Cloud's quality gate on the `coverage.out` profile is the
coverage check. The *Test* step stays `go test ./... -race -cover`.

## Consequences

- An unformatted Go file fails the CI check by name, even if it bypassed the local *Format* step.
- A pull request under 80 % coverage that skipped the local gate is caught only by SonarQube Cloud's quality
  gate (new code; not run for Dependabot pull requests, which change no Go code); a Lead-accepted gap does
  not turn CI red.
- 0001 stays accepted unchanged. If unchecked coverage starts reaching `main`, 0001's own remedy applies: a
  new record superseding 0001 adds the script to CI.
