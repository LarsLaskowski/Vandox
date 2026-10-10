# Plan: shellcheck SC2055 and SC2034 in two CI scripts

Source: Issue #161
Status: Draft
Tier: security — both files are under `.github/scripts/`, which `.squad/project.md` lists in security area 13
(*Release pipeline and published artifacts*), and `.squad/routing.md` puts every Docker/CI change in `security`;
the issue's suggestion `trivial` does not apply.

Steps 4 (*Skeleton*), 5 (*Tests first*) and the *Coverage gate* of step 6 are **not applicable**: the change
touches no production or test code, only two CI shell scripts (`.squad/routing.md`, *Changes without
production or test code*). The tier is not lowered by this: plan challenge (step 2), plan security review
(step 3), code check (step 7: *Format check*, *Analyzer gate*, `scope-check.py`), Reviewer and Security
(step 8) and step 9 all run. Each criterion is verified as *Verification without tests* below says.

## Problem / root cause

The *Analyzer gate* (since #160) runs `shellcheck` on every changed `*.sh` file
(`.squad/tools/analyzer_common.py`, `LINTERS` and `lint_check`). Two scripts on `main` carry shellcheck
warnings, so the next change to either one fails the gate on findings it did not introduce:

1. `.github/scripts/check-builder-dotnet-version.sh` line 26:
   `if [[ "$tfm" != "$build" || "$tfm" != "$run" ]]; then` — SC2055 ("You probably wanted && here").
   shellcheck's heuristic flags every `x != a || x != b`; here `a` and `b` are different variables, so the
   condition is not always true and means "not all three versions equal", which is the intent (record 0041).
   False positive, but the gate does not distinguish.
2. `.github/scripts/smoke-test-backend.sh` line 107 (function `check_image_owners`):
   `while read -r mode owner size date time name; do` — SC2034 for `size`, `date` and `time`, which are read
   only to skip the columns of the `tar -tv` listing before `name`. Line 105 declares them `local`.

Claims of the issue, checked against the code and with shellcheck 0.11.0 (`/root/.local/bin/shellcheck`, the
version the hook pins):

- SC2055 at `check-builder-dotnet-version.sh:26` — **confirmed** (exact output as in the issue).
- SC2034 three times at `smoke-test-backend.sh:107` (`size`, `date`, `time`) — **confirmed**.
- The `local` declaration is on line 105 — **confirmed** (`local mode owner size date time name uid gid path`).
- The logic of line 26 is right as written (fail unless all three versions are equal) — **confirmed**: the
  three values are each validated against `^[0-9]+\.[0-9]+$` on line 22 before line 26, and the right-hand
  sides are quoted, so both forms are plain string comparisons; truth table below.
- The proposed rewrites silence the warnings without a behavior change — **confirmed** in a scratch copy
  (`git archive HEAD`, outside the working tree): shellcheck exits 0 on every script in `.github/scripts/`,
  `bash -n` passes, and old and new versions behave identically (cases below).
- "Reading them into `_` is what shellcheck expects" — **confirmed**: SC2034 is not reported for `_`.
- These are the only shellcheck findings in tracked shell scripts — **confirmed**: shellcheck over all nine
  tracked `*.sh` files (including `.claude/hooks/*.sh` and the other three `.github/scripts/*.sh`) reports only
  these four warnings.
- The two CI jobs named in the issue: the scripts run in `.github/workflows/ci.yml`, job *Release build check*
  (`release-build`), steps "Check builder .NET version" (line 189) and "Smoke test backend container"
  (line 253), on every pull request to `main`; `check-builder-dotnet-version.sh` also runs in
  `.github/workflows/release.yml` (line 141, tag-triggered only) — **confirmed**.

Related defect found on the way (not fixed here, see *Out of scope*): `.squad/stack.md`, table *Not checkable
in a cloud session*, row "shell scripts", still lists `shellcheck` as missing locally ("the gate skips it"),
while the *Analyzer gate* paragraph above it (and this session) shows the SessionStart hook installs it. `shfmt`
is indeed not installed.

## Acceptance criteria

- [ ] AC1: `shellcheck .github/scripts/check-builder-dotnet-version.sh` (0.11.0) prints nothing and exits 0.
  Line 26 reads exactly `if ! [[ "$tfm" == "$build" && "$tfm" == "$run" ]]; then`; lines 27–29 (error text
  and `exit 1`) are unchanged.
- [ ] AC2: `shellcheck .github/scripts/smoke-test-backend.sh` prints nothing and exits 0. Line 105 reads
  exactly `  local mode owner name uid gid path` and line 107 exactly
  `  while read -r mode owner _ _ _ name; do`; no other line changes.
- [ ] AC3: the version check behaves as before: with `Directory.Build.props` at `net10.0`, exit 0 when
  `BASE_BUILD_TAG` and `BASE_RUNTIME_TAG` both start with `10.0`; exit 1 with the unchanged message
  `::error::target framework net<tfm>, builder tag '<build_tag>' and runtime tag '<run_tag>' must name the same .NET version`
  when the builder version differs, when the runtime version differs, when both differ from each other and
  from the framework, and when both are equal to each other but differ from the framework.
- [ ] AC4: `check_image_owners` behaves as before: for the same `tar --numeric-owner -tv` listing, old and new
  function give the same result — pass for a correct listing (including a file name with spaces, a symlink
  `a -> b` and a name containing `65532`), the same `fail` message for a wrong `/data` mode, and the same
  offenders list for an entry owned by uid or gid 65532 outside `data/` and `home/nonroot/` (including a hard
  link `x link to y`). The six variables still split the line into the same fields; `name` still receives
  the rest of the line.
- [ ] AC5: the diff against `main` changes only these two files (plus `specs/issue-161/`); the *Analyzer gate*
  reports `shellcheck (changed shell scripts): PASS` and `SonarQube shell rules S7679/S7688 (changed shell
  scripts): PASS`, and `scope-check.py` is clean.
- [ ] AC6: on the pull request head, CI job *Release build check* is green, including the steps "Check builder
  .NET version" and "Smoke test backend container"; SonarQube Cloud (step 11) reports no new `shelldre:*`
  finding on the two files.

## Verification without tests

No production or test code changes, so no unit test exists for these scripts; steps 4, 5 and the *Coverage
gate* are not applicable. Per criterion:

| AC | Where | Who |
| -- | ----- | --- |
| AC1, AC2 | `shellcheck` on both files after the edit, output quoted in the step-6 report; again inside the *Analyzer gate* lint pass in step 7; the Reviewer checks the exact lines in the diff | Dev (step 6), orchestrator (step 7), Reviewer (step 8) |
| AC3 | Scratch copy outside the working tree (`git worktree add` or `git archive HEAD \| tar -x`): run the old script (from `main`) and the new one against `deploy/backend/Dockerfile` edited in the copy for the five cases of AC3 and compare exit codes and stderr. The Lead ran this once while planning (all five cases identical: 0, 1, 1, 1, 1). The positive case is also covered by CI | Dev (step 6, report the table), Reviewer or Security re-check (step 8, read-only, scratch copy); CI step "Check builder .NET version" on the PR head |
| AC4 | Same scratch copy: extract `check_image_owners` from old and new script (`sed -n '/^check_image_owners() {/,/^}/p'`), source each with a stub `fail() { echo "FAIL: $*"; exit 1; }`, feed the listings of AC4 and compare the output. The Lead ran this once while planning (identical). The positive case on the real image is covered by CI | Dev (step 6, report the outputs), Reviewer (step 8); CI step "Smoke test backend container" on the PR head |
| AC5 | `git diff --stat main...HEAD`, *Analyzer gate*, `scope-check.py` | orchestrator (step 7), Reviewer (step 8) |
| AC6 | CI run and SonarQube Cloud analysis on the PR head (the cloud session has no Docker daemon, `.squad/stack.md`, *Not checkable in a cloud session*; "not verified locally, CI job *Release build check*" is logged once) | orchestrator (step 11) |

## Approach

Exactly the two edits the issue proposes, no other change:

- `check-builder-dotnet-version.sh:26`: replace the negative disjunction by the negated positive conjunction
  (De Morgan): `if ! [[ "$tfm" == "$build" && "$tfm" == "$run" ]]; then`. Inside `[[ ]]` a quoted right-hand
  side of `==` is a literal string, not a pattern, so this is the same string comparison as `!=` with a quoted
  right-hand side. `!` before a compound command in an `if` condition is not affected by `set -e`. The file
  already uses `! grep` and `! printf … | grep` in conditions (lines 12, 22), so the form is consistent.
- `smoke-test-backend.sh:107`: `while read -r mode owner _ _ _ name; do`, and line 105 drops `size date time`
  from the `local` list. `read` still gets six names, so default-IFS splitting and the "last name gets the
  rest of the line" rule are unchanged. `_` is not declared `local`: bash overwrites `$_` after every command
  anyway, and nothing in the script reads `$_`.

Options rejected: a `# shellcheck disable=SC2055` / `SC2034` directive (a suppression keeps the confusing
form, and the positive form states the intent better); splitting line 26 into two `[[ ]]` tests joined by
`||` (still a negative disjunction, only reshaped to dodge the heuristic).

Neither edit introduces `[ … ]` or a positional parameter used as a word inside a function, so the gate's own
`shelldre:S7688` / `S7679` checks stay clean (verified with the gate's regexes against the new lines). Whether
SonarQube Cloud has a further `shelldre:*` rule against `! [[ … ]]` is *unverified* locally; step 11 is the
authoritative check (AC6).

Indentation stays as is (two spaces in `smoke-test-backend.sh`, none at line 26); *Format check* does not
cover shell scripts and `shfmt` is not installed.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| CI | `.github/scripts/check-builder-dotnet-version.sh` | line 26 condition rewritten (Dev) |
| CI | `.github/scripts/smoke-test-backend.sh` | lines 105 and 107 (Dev) |

## Signatures (for the Dev's skeleton)

None (no API; step 4 not applicable). The function `check_image_owners` keeps its interface (one argument,
the listing; calls `fail` on a violation).

## Test files

None: no production or test code changes, and the stack has no unit-test layout for CI shell scripts
(`.squad/stack.md`, *Layout*). Verification as above.

Existing test code that calls a changed signature: none.

## Areas

None — no change in behavior. (The smoke test is mentioned in `docs/areas/backend-host.md`, the builder
version check in `.squad/project.md` area 13 and record 0041; neither text describes the changed lines.)

## Documentation updates

None. No document quotes the changed lines (checked with a search over `docs/`, `README.md` and `.github/`).

## Architecture check

No guarantee from `docs/ARCHITECTURE.md` or `.squad/project.md` is touched. Security area 13 is preserved: the
builder-version check still fails unless the framework, builder and runtime versions are equal (record 0041),
and the smoke test still enforces that only `/data` (0700, 65532:65532) and `/home/nonroot` are owned by the
container user (record 0060). Neither script gains a secret, network access, a `${{ }}` expression or a new
command.

## Security considerations

- [x] Byte or length limits on parsed input: no limits (none added or changed).
- [x] Exceptions reaching a user-visible message: none; the only user-visible output is the unchanged
  `::error::` text of line 27 and the unchanged `fail` messages of `check_image_owners`.
- Guard check (the version check acts as a guard on `deploy/backend/Dockerfile`): the forms it accepts are
  unchanged because line 22 restricts `tfm`, `build` and `run` to `^[0-9]+\.[0-9]+$` before line 26, so the
  only inputs line 26 sees are digit-dot-digit strings; for every combination (all equal / one differs /
  both differ / both equal but not the framework) old and new conditions give the same result (De Morgan,
  verified in AC3). No glob or regex semantics are involved on either side (quoted right-hand sides).
- The owner check is a guard on the image contents: its parsing is unchanged (same six-name `read`, same
  `path`, `uid`, `gid` derivation), verified in AC4 for names with spaces, `->`, `link to` and `65532`.

## Decision records

None: no decision beyond the obvious fix (a lint fix with no alternative of lasting weight; record 0041 and
0060 stay true as written).

## Out of scope / follow-ups

- The stale row in `.squad/stack.md` (*Not checkable in a cloud session*, "shell scripts": `shellcheck` listed
  as missing and skipped, although the SessionStart hook installs it since #160) is project knowledge that
  this change does not make untrue; it belongs to a squad lesson for step 12 (an issue labelled `squad` in
  this repository, fixed in a squad-maintenance PR checked with `config-check.py`), not to this product PR.
  Proposed title: "[Squad] stack.md still lists shellcheck as not checkable in a cloud session (from #161)".
