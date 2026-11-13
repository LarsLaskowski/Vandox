Lead, mode plan, issue #422. No plan.md written (tier patch).

**Tier: `patch/security`.** The change is two script lines, an edited comment and one added list entry, with no logic or control-flow change. It edits `.github/scripts/stress-go-tests.sh`, which is a CI file in a security area, so the tier is `patch/security`. It creates, changes and removes no security control (no permission, secret, trigger or action change).

**Claims checked against the code and the issues:**
- Confirmed: `docs/UNIT_TESTS.md:147-156` (*Load-dependent tests (Go)*) says a Go test is listed in the stress script "when it failed intermittently in CI".
- Confirmed: the test failed intermittently in CI. Issue #414 (closed) records `TestScheduler_TriggerReplacesNextRunUnderPressure` failing once on the CI run of #413 with `Run.Pressure = [false true true true true]`, and a re-run passed.
- Confirmed: the test exists under its new name `TestScheduler_TriggerReplacesNextRunUnderPressure_AC1` at `/home/user/Vandox/internal/scheduler/trigger_test.go:158`. `go test -list '^TestScheduler_TriggerReplacesNextRunUnderPressure_AC1$' ./internal/scheduler` prints it.
- Confirmed: the comment the issue names exists today as `# Failed intermittently in CI (#329, #336).` at `/home/user/Vandox/.github/scripts/stress-go-tests.sh:13`. Its group currently ends with the scheduler entry at line 16.
- Confirmed: the `Go stress` workflow `paths` filter (`.github/workflows/go-stress.yml:8-13`) includes `.github/scripts/stress-go-tests.sh`, so the PR itself triggers the stress run.
- No related defect found.

**Edits (file `/home/user/Vandox/.github/scripts/stress-go-tests.sh`):**
1. Line 13: replace `  # Failed intermittently in CI (#329, #336).` with `  # Failed intermittently in CI (#329, #336, #414).`
2. After line 16 (`  "internal/scheduler TestScheduler_TriggerWhileStuckRecordsGap"`), insert `  "internal/scheduler TestScheduler_TriggerReplacesNextRunUnderPressure_AC1"` with two spaces of indent, as the other entries have.

The diff is 2 changed lines, well within `PATCH_MAX_LINES`.

**Check covering each edit:**
- Edit 1 (comment only): the *Analyzer gate* with `--lint-only` (shellcheck, actionlint and the shell rules on the changed file) plus `bash -n .github/scripts/stress-go-tests.sh`.
- Edit 2 (list entry): the same lint-only gate and `bash -n`, plus the script's own fail-closed `check_listed`. A misspelled or missing test name fails the script with `listed test ... not found`.
  - CI: the `Go stress` job on the draft PR is green and its log shows `all 15 listed tests exist`, with the new name in the `tests:` line.
  - No unit test is needed, since no production code changes.

**Acceptance criteria:**
- AC1: the `LISTED` array in `.github/scripts/stress-go-tests.sh` contains `"internal/scheduler TestScheduler_TriggerReplacesNextRunUnderPressure_AC1"` in the group under `# Failed intermittently in CI (#329, #336, #414).` Check: `CI:` the `Go stress` job on the PR passes and reports 15 listed tests that exist.
- AC2: no other line of the script changes. Check: `scope-check.py --tier patch` and the diff review (`squad-security` mode `diff`).

**Documentation sweep:**
- `docs/UNIT_TESTS.md` states the listing rule but enumerates no tests.
- Record 0196 (`docs/decisions/0196-load-dependent-go-tests-are-stress-run-from-a-list-in-a-path-filtered-workflow.md`) names #329/#336 only as history and does not list the tests.
- `docs/ARCHITECTURE.md` and the area documents are unaffected.
- A changelog entry is not needed: the change is internal to CI and invisible to users.

So there are no documentation updates, and no decision record is needed (the decision is already record 0196).

**Options considered:** one option. The issue prescribes the exact entry, and record 0196 fixes the mechanism.

RESULT: DONE
