# Log: issue #161

One line per pipeline step: date, step, member, result. Lead decisions and escalations are quoted in full.

| Date | Step | Member | Result |
| ---- | ---- | ------ | ------ |
| 2026-10-10 | 1 Intake | Orchestrator | Issue #161 read (no comments): shellcheck SC2055 in .github/scripts/check-builder-dotnet-version.sh:26 and SC2034 x3 in .github/scripts/smoke-test-backend.sh:107, reported on main after #160; environment: main 88cca7c, shellcheck 0.11.0. Branch fix-issue-161-shellcheck-ci-scripts off origin/main 88cca7c |
