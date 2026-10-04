# 0033: Pre-existing overall coverage gap accepted for a documentation-only change

- **Status:** Accepted
- **Date:** 2026-10-04
- **Source:** Issue #7
- **Supersedes:** —

## Context

The coverage gate (`python3 .squad/tools/coverage-check.py`, 0001) requires at least 80 % line coverage on
new or changed production code and overall. The change for issue #7 edits only Markdown (`.squad/project.md`,
`SECURITY.md`, `docs/ARCHITECTURE.md`, decision records 0028–0032); no Go file changes, so the
"new/changed code" value has no lines to measure. The overall value is 0 of 21 lines: the scaffold
(`cmd/vandox-agent/main.go`, `cmd/vandoxd/main.go`, `internal/version/version.go`) has no tests on `main`,
and the gate fails identically on `origin/main`. The Reviewer reported this as non-blocking.

## Options considered

1. **Block the change until overall coverage reaches 80 %** — would put tests for unrelated Go code into a
   documentation PR, against the rule that a PR changes only what its issue asks for.
2. **Add the missing tests in this change** — same objection; the Tester and Dev steps were skipped
   because the plan has no code.
3. **Accept the gap here, state it in the PR, and split the fix into a follow-up issue** — the change
   neither causes nor worsens the gap.

## Decision

Option 3. The overall-coverage failure is accepted for the issue #7 change only, because the diff contains
no production code and the gate fails identically on `origin/main`. The PR description states this. A
follow-up issue covers unit tests for `internal/version` and the testable parts of both `main` packages so
that the gate passes on `main`.

## Consequences

- The issue #7 PR is approved with the overall coverage gate red; this acceptance does not carry over to
  any other change: the next change that adds Go code must meet the gate on its own new code, and the
  overall value stays a gate.
- Follow-up issue: "[Tests] Unit tests for the scaffold so the overall coverage gate passes on main".
