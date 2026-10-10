# 0001: Quality gates run locally before the pull request; CI builds, tests, checks the format and runs the code analysis

- **Status:** Accepted
- **Date:** 2026-10-03
- **Area:** —
- **Source:** Squad adopted from Squad-Spec-Repository-Template; issues #7 and #8
- **Supersedes:** —

## Context

Formatting slips, analyzer findings and missing tests that only surface in CI or in the SonarQube Cloud analysis of the pull
request cost an extra fix-and-push round each, and every round costs reviewer attention. The squad has a Code Officer whose
sole job is to leave nothing of that kind behind, and the `create-pr` skill runs the same checks outside the squad.

Two facts came up when CI was set up (issue #8). The template defines the *Coverage gate* as a local script (at least 80 % line
coverage on new or changed production code and overall); SonarQube Cloud already measures coverage on new code in CI. And
accepted coverage gaps exist by design: an untested `main` body (0072, `docs/UNIT_TESTS.md`), or a documentation-only change
(issue #7) that cannot cause or fix a gap that already exists on `main`.

## Options considered

- **Keep everything in CI** — a safety net for every contributor, but findings keep arriving late.
- **Check locally and in CI** — earliest feedback plus a safety net; some checks run twice.
- **Check locally first, CI as the system of record** — chosen: format, analyzer gate and coverage gate run before the push
  (`.squad/stack.md`), CI keeps the build, the tests and the code analysis.
- **The coverage script as a blocking CI step** — rejected: a Lead-accepted gap would turn CI red by design, against the
  squad's exit criterion "CI green". **As a report-only step** — rejected: a gate nobody has to pass is not a gate and
  duplicates SonarQube's figure.
- **Block a documentation-only change until the overall coverage reaches 80 %, or add the missing tests to it** — rejected:
  it puts tests for unrelated code into a pull request that changes only what its issue asks for.

## Decision

Before a push, *Format*, the *Analyzer gate* (no diagnostic of any severity in a changed file) and the *Coverage gate* from
`.squad/stack.md` pass. CI builds, tests and runs the code analysis, with SonarQube Cloud's quality gate as the system of
record for coverage on new code, and has an explicit *Format check* step per language, so an unformatted file fails by name
even if it skipped the local step. Squad and agent tooling (`.squad/**`, `.claude/**`) is excluded from the coverage measure
in CI. A coverage gap that predates a change and that the change cannot fix may be accepted by the Lead for that change only:
the pull request says so and a follow-up issue closes the gap.

## Consequences

- Findings appear while coding; the Code Officer clears them before the review.
- A push that skips the local gates is only caught by what CI still checks: the PR template checklist and the `create-pr` skill
  require the local gates, and SonarQube's new-code coverage condition catches missing coverage. If unchecked code starts
  reaching `main`, the remedy is a new record that supersedes this one and adds the script to CI.
- The local analyzers may differ from the CI quality profile, and some checks (duplication, security hotspots, taint analysis)
  only run in CI; such findings arrive in squad step 11.
- The 80 % rule applies to new or changed lines, so it does not force retroactive tests on old code. An accepted gap is not a
  precedent: the next change that adds code must meet the gate on its own new code.
