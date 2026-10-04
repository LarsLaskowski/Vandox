# 0035: Format check and coverage gate run locally and in CI

- **Status:** Proposed
- **Date:** 2026-10-04
- **Source:** Issue #8
- **Supersedes:** 0001

## Context

Decision 0001 put *Format*, the *Analyzer gate* and the *Coverage gate* (`.squad/stack.md`) before the push
and left CI with the build, the tests, `go vet`, golangci-lint, govulncheck and the SonarQube Cloud
analysis, the system of record for the quality gate. 0001 named "adding a CI step back" as the remedy if
unchecked code reaches `main`. Issue #8 asks for CI that checks every pull request for both binaries,
including the *Format check* "as configured in `.squad/stack.md`" and the *Coverage gate* "as defined by the
template" (`python3 .squad/tools/coverage-check.py`: at least 80 % line coverage on new or changed
production lines and overall, measured against `origin/main`). Contributions also arrive outside the squad
(`create-pr`, Dependabot, manual edits), where the local gates are not enforced.

## Options considered

1. **Keep 0001 unchanged (local only, SonarQube in CI)** — no duplicate runs, but a push that skipped the
   local gates is caught only by SonarQube's own coverage measure, and formatting only indirectly through
   golangci-lint's `gofmt` formatter; does not meet the issue.
2. **Run *Format check* and the *Coverage gate* locally and in CI, blocking** — every pull request is held to
   the same, named gates the squad runs; some checks run twice; CI depends on the template-managed
   `coverage-check.py`.
3. **Run them in CI as report-only (`continue-on-error`)** — never turns CI red for a Lead-accepted coverage
   gap, but a gate nobody has to pass is not a gate.

## Decision

Option 2. Locally, before a push, *Format*, the *Analyzer gate* and the *Coverage gate* still pass, as in
0001. In addition, `.github/workflows/ci.yml` job `build-test-lint` checks out the full history
(`fetch-depth: 0`), runs a *Format check* step (fails when `gofmt -l .` lists a file), *Test with coverage*
(`go test ./... -race -coverprofile=coverage.out`) and a blocking *Coverage gate* step
(`python3 .squad/tools/coverage-check.py`). On a push to `main` or a scheduled run the diff against
`origin/main` is empty, so the gate only reports overall coverage. SonarQube Cloud stays in CI and remains
the system of record for the quality gate (duplication, hotspots, its own coverage on new code). Squad and
agent tooling (`.squad/**`, `.claude/**`) stays excluded from the coverage measure in SonarQube.

## Consequences

- A pull request below 80 % on new/changed lines or overall, or with an unformatted Go file, fails CI even
  if it bypassed the local gates.
- A coverage gap the Lead accepts with a recorded decision (`.squad/routing.md`, *Loop limits*) now shows as
  a red *Coverage gate* check on that pull request; the PR description must name the record, and merging it
  is a conscious act of the maintainer.
- CI depends on `.squad/tools/coverage-check.py` and `squad_settings.py`; a template refresh that changes the
  script changes the CI gate too, and is reviewed in its own PR.
- Two coverage measures run in CI (the script's line-based count and SonarQube's); they can differ slightly.
- Tests run in two CI jobs (`build-test-lint` and `sonarqube`, which is skipped for Dependabot).
