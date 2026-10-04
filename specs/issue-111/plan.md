# Plan: Local analyzer gate catches cognitive complexity (go:S3776)

Source: Issue #111
Status: Draft
Tier: security — the change edits `.golangci.yml`, which CI's Lint step consumes (CI/build configuration),
and changes what the *Analyzer gate* in `.squad/stack.md` enforces, which `.squad/routing.md` (*Tiers*)
classifies as `security`; when in doubt, the higher tier.

## Problem / root cause

`.golangci.yml` (lines 3–10) enables `default: standard` plus `errorlint`, `gocritic`, `misspell`, `revive`
and `unconvert`. None of them measures cognitive complexity, so neither `.squad/tools/analyzer-check.py`
(`golangci-lint run --new-from-merge-base=origin/main --whole-files ./...`) nor CI's Lint step
(`.github/workflows/ci.yml`, `golangci-lint-action` v9.3.0, golangci-lint v2.13.1, whole module) can report
what SonarQube Cloud's `go:S3776` reports. `.squad/stack.md` (*Analyzer gate*) even states that SonarQube
"has no local Go equivalent here".

Claims of the issue, checked:

- "SonarQube Cloud reported 7 new go:S3776 issues in test files after PR #110 was opened" — **confirmed**
  (SonarCloud API, `pullRequest=110`, rule `go:S3776`): 7 issues, all in `internal/model/model_test.go` (17),
  `internal/wire/decode_test.go` (38, 30, 26, 22) and `internal/wire/encode_test.go` (33, 22), all
  `FIXED`/`CLOSED`. `main` has 0 open `go:S3776` issues.
- "the local Analyzer gate was clean because gocognit is not in the linter set" — **confirmed** (see above).
- "add gocognit with a threshold of 15 so the local gate matches the CI code analysis" — **partly refuted**:
  in every case measured (PR #110 at 389aef8, `main` at a7e9b4a) gocognit at 15 flagged at least what
  SonarQube flagged (on 389aef8 it reports 9 test functions, a superset of the 7), but it does **not** match
  SonarQube exactly and the sample is small. Per-function scores differ by 1–7 points on several constructs
  (both tools score `TestDecoder_Limits` 38). The largest known difference: SonarQube's Go analyzer does not
  count an `if` whose condition is the bare `err != nil` check; gocognit does. A compound condition such as
  `if err != nil || len(recs) != 1` is counted by both. On `main` (a7e9b4a) gocognit
  at 15 reports two production functions SonarQube accepts:
  `internal/model/connection.go:75` `(*ConnectionSnapshot).Validate` (gocognit 37; SonarQube: 15 for the
  whole file) and `internal/model/mariadb.go:47` `(*MariaDBStatus).Validate` (gocognit 21; SonarQube: 11
  for the whole file). Adding gocognit alone would therefore turn CI's Lint step red on `main`.
- Threshold semantics — **confirmed equal**: SonarQube rule parameter `threshold` = 15, message "from N to
  the 15 allowed" (flags > 15); gocognit `min-complexity: 15` reports "is high (> 15)".
- "or run gocognit in the analyzer gate" — not possible here: `.squad/tools/analyzer-check.py` is
  template-managed (*Template-managed files* in `.squad/routing.md`) and CI would still not run it.

Where each edit belongs: `.golangci.yml` ("the stack profile's … tool configuration files") and
`.squad/stack.md` are **seeded** files owned by this repository, so they are changed here in a
squad-maintenance PR (*Squad lessons*: project knowledge). No managed or marked file is touched
(`.squad/tools/*.py`, `.squad/routing.md`, `.claude/`, skills, `CLAUDE.md`, `AGENTS.md`,
`.github/copilot-instructions.md` stay unchanged; their generic "golangci-lint configured in
`.golangci.yml`" wording stays true).

Related observation (not a defect of this repository, a lesson for step 12): the template's `go` profile
seeds a `.golangci.yml` without `gocognit`, so every repository using the profile has the same gap — a
candidate `squad` issue in the template repository.

## Acceptance criteria

- [ ] AC1: `.golangci.yml` enables `gocognit` and sets `linters.settings.gocognit.min-complexity: 15`; the
  existing linters and the `gofmt` formatter are unchanged.
- [ ] AC2: `.golangci.yml` has exactly two `linters.exclusions.rules`, each limited to linter `gocognit`, an
  anchored path (`^internal/model/connection\.go$` resp. `^internal/model/mariadb\.go$`) and a text regex
  matching only that function's backticked name (`` `\(\*ConnectionSnapshot\)\.Validate` `` resp.
  `` `\(\*MariaDBStatus\)\.Validate` ``), plus `linters.exclusions.warn-unused: true`.
- [ ] AC3: golangci-lint v2.13.1 (built with Go ≥ 1.27) reports `0 issues.` for `golangci-lint run ./...`
  on the whole module, and `golangci-lint config verify` passes.
- [ ] AC4: With the new `.golangci.yml`, gocognit reports every function SonarQube flagged on PR #110: on the
  tree of commit 389aef8 it reports at least `TestQuoteName` (model_test.go), `TestDecoder_Limits`,
  `TestDecoder_BatchRules`, `TestDecoder_AcceptedForms`, `(want).check` (decode_test.go),
  `TestEncodeBatch_RoundTrip`, `TestEncodeBatch_StreamLayout` (encode_test.go), and does not report the two
  excluded validators.
- [ ] AC5: The exclusions do not hide other functions: a function above 15 added to a scratch copy of
  `internal/model/connection.go` is reported.
- [ ] AC6: `.squad/stack.md`, *Analyzer gate*, states that gocognit (threshold 15) is the local stand-in for
  SonarQube's `go:S3776` — without claiming it reports every function SonarQube would — that it counts
  bare `if err != nil` checks, which SonarQube does not, and so can be stricter than SonarQube, that
  a gocognit finding is fixed like any diagnostic and a new exclusion needs a Lead decision record, and
  points to record 0047; the sentence "SonarQube Cloud has no local Go equivalent here" is narrowed to the
  findings that still have none (other rules, duplication, hotspots).
- [ ] AC7: The diff contains only `.golangci.yml`, `.squad/stack.md`, `docs/decisions/0047-…md`,
  `docs/decisions/README.md` (index row, at approval) and `specs/issue-111/`; no Go file and no
  template-managed file; `python3 .squad/tools/config-check.py` passes; the *Format check* and the
  *Analyzer gate* pass.

## Verification without tests

The change touches no production or test code: steps 4 (*Skeleton*), 5 (*Tests first*) and the *Coverage
gate* of step 6 are **not applicable** (*Changes without production or test code* in `.squad/routing.md`).
The tier stays `security` (plan challenge, steps 3 and 8 run). Each criterion is verified instead:

| AC | Where | Who |
| -- | ----- | --- |
| AC1, AC2 | read-only inspection of the `.golangci.yml` diff | Reviewer (step 8), Security (steps 3 and 8) |
| AC3 | `golangci-lint config verify` and `golangci-lint run ./...` in the repository root, besides the *Analyzer gate* | Code Officer (step 7); CI Lint step in step 11 |
| AC4 | scratch copy `git archive 389aef8 \| tar -x -C <scratch>`, the branch's `.golangci.yml` copied in, `golangci-lint run ./...` | Reviewer (step 8, scratch copy) |
| AC5 | scratch copy of the branch, a function with complexity > 15 appended to `internal/model/connection.go` there (never in the working tree), `golangci-lint run ./...` | Reviewer (step 8, scratch copy) |
| AC6 | read-only inspection of the `.squad/stack.md` diff | Reviewer (step 8) |
| AC7 | `git diff --stat origin/main...HEAD`, `python3 .squad/tools/config-check.py`, *Format check*, *Analyzer gate* | Code Officer (step 7), Reviewer (step 8), Lead (step 9) |

golangci-lint must be a build with Go ≥ 1.27 (*Known pitfalls* in `.squad/stack.md`); the one on the PATH in
this session is built with Go 1.25 and refuses to run — use the v2.13.1 build at
`/tmp/claude-0/-home-user-Vandox/1cd38667-8446-5814-9391-179b53f23763/scratchpad/bin/golangci-lint`
(go1.27.0). The Lead verified AC3, AC4 and AC5 in scratch copies while planning (0 issues on HEAD;
9 test functions and no validator on 389aef8).

## Approach

1. Dev edits `.golangci.yml` to exactly this content (verified by the Lead in a scratch copy):

   ```yaml
   version: "2"

   linters:
     default: standard
     enable:
       - errorlint
       - gocognit
       - gocritic
       - misspell
       - revive
       - unconvert
     settings:
       gocognit:
         # SonarQube's go:S3776 threshold; stricter for bare `if err != nil` checks (record 0047).
         min-complexity: 15
     exclusions:
       warn-unused: true
       rules:
         # Sequential error checks that SonarQube rates at most 15 (record 0047).
         - path: ^internal/model/connection\.go$
           linters:
             - gocognit
           text: "`\\(\\*ConnectionSnapshot\\)\\.Validate`"
         - path: ^internal/model/mariadb\.go$
           linters:
             - gocognit
           text: "`\\(\\*MariaDBStatus\\)\\.Validate`"

   formatters:
     enable:
       - gofmt
   ```

2. Dev updates `.squad/stack.md`, *Analyzer gate* (AC6). Suggested text replacing the last sentence of that
   section: "`gocognit` (threshold 15, `.golangci.yml`) stands in for SonarQube Cloud's cognitive-complexity
   rule `go:S3776`. The two measures are close but not identical: in every case measured (PR #110, `main`)
   gocognit flagged at least what SonarQube flagged, and it also counts an `if` on the bare `err != nil`
   check, which SonarQube does not, so it can flag a function SonarQube accepts. Such a finding is fixed like any
   other diagnostic; a new exclusion in `.golangci.yml` needs a Lead decision record (two existing
   validators are excluded by name, record 0047). Other SonarQube Cloud findings (further rules,
   duplication, hotspots) have no local Go equivalent here and arrive in squad step 11."
3. Code Officer runs *Format check*, the *Analyzer gate*, `golangci-lint config verify` and
   `golangci-lint run ./...`.

### Exclusion scope (input golangci-lint consumes)

golangci-lint v2 matches an exclusion rule when **all** its fields match: `linters` (exact linter name),
`path` (regex against the file path relative to the config file — `run.relative-path-mode` default `cfg`,
`.golangci.yml` sits at the repository root; forward slashes on every OS) and `text` (regex against the
issue message, here ``cognitive complexity N of func `(*T).Validate` is high (> 15)``).

| Form | Behavior |
| ---- | -------- |
| the named validator in the named file | excluded (verified) |
| another function in the same file above 15 | reported: the text regex requires the backticked exact name (AC5) |
| a function whose name only starts with `Validate` (e.g. `(*ConnectionSnapshot).ValidateAll`) or a value receiver `(ConnectionSnapshot).Validate` | reported: the closing backtick and `\(\*` do not match |
| the same method name in another file (moved or copied) | reported: the anchored path does not match |
| a file named e.g. `internal/model/connection.go.bak` or `x/internal/model/connection.go` | not a Go file resp. path anchored with `^…$`: not excluded |
| any other linter on the two validators | reported: the rule lists only `gocognit` |
| an unused rule after a refactoring | warning from `warn-unused: true`; the rule is removed in that change |

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| tooling | `.golangci.yml` | enable `gocognit` at 15, two named exclusions, `warn-unused` |
| squad project knowledge | `.squad/stack.md` (*Analyzer gate*) | describe gocognit as the `go:S3776` stand-in |
| docs | `docs/decisions/0047-gocognit-as-local-stand-in-for-sonar-cognitive-complexity.md` | new record (Proposed, by the Lead) |

## Signatures (for the Dev's skeleton)

None.

## Test files

None: no production or test code changes (*Verification without tests*). No existing test is affected.

## Documentation updates

`.squad/stack.md` (*Analyzer gate*), by the Dev. `README.md`, `docs/CONTRIBUTING.md`, `docs/UNIT_TESTS.md`
and `docs/ARCHITECTURE.md` do not list the linter set and stay unchanged; the instruction files' generic
wording stays true. The index row for 0047 in `docs/decisions/README.md` is added by the Lead at approval.

## Architecture check

No guarantee from `docs/ARCHITECTURE.md` or `.squad/project.md` is touched: no product behavior changes.
Consistent with record 0001 (check locally first, CI as the system of record; local analyzers may differ
from the CI profile — this narrows that difference) and 0035 (CI keeps its steps; no workflow change).

## Security considerations

- Tightens a gate only; no workflow, action, permission, secret or dependency changes (`gocognit` ships with
  the pinned golangci-lint v2.13.1, nothing is added to `go.mod`).
- The only loosening is the two exclusions, scoped to one linter, one file and one exact function name each
  (table above), recorded in 0047; new exclusions need a Lead record.
- CI's Lint step now enforces gocognit on the whole module; `main` passes today (AC3), so no PR is blocked by
  pre-existing code.

## Decision records

- `docs/decisions/0047-gocognit-as-local-stand-in-for-sonar-cognitive-complexity.md` (Proposed)

## Challenge

Devil's Advocate objections (plan challenge, step 3):

1. *minor — the claim that gocognit "reports every function SonarQube would" / "implements the same
   cognitive-complexity specification" rests on 7 samples; the tools differ by 1–7 points on other
   constructs, and SonarQube does count compound conditions such as `if err != nil || len(recs) != 1`.*
   **Accepted.** The equivalence claim is dropped everywhere: *Claims of the issue, checked*, AC6, the
   suggested `.squad/stack.md` text (Approach, step 2) and record 0047 (*Context*, option 1, *Decision*)
   now say "in every case measured (PR #110, `main`) it flagged at least what SonarQube flagged", and the
   idiom wording is narrowed to an `if` on the bare `err != nil` check. The `.golangci.yml` comment is
   reworded to match. The decision itself is unchanged: the gate is a stand-in that narrows the gap of
   record 0001, not a guarantee of parity; a SonarQube finding gocognit misses still arrives in step 11.
2. *minor — record 0047 omits `//nolint:gocognit // reason` on the two `Validate` methods
   (`internal/model/connection.go:75`, `internal/model/mariadb.go:47`).* **Accepted as an option, rejected
   as the choice.** Record 0047 now lists it (option 2) with the reason: it edits two production Go files,
   which AC7 excludes and which a squad-maintenance change (*Squad lessons*) should not touch; it would make
   the change a code change with its own review surface; and the exception would live in two places (the
   code and the record) instead of one auditable list in `.golangci.yml` that `warn-unused` keeps honest
   (an unused `//nolint` is reported only with `nolintlint`, which is not enabled). The two options are
   otherwise equivalent in scope (one linter, one function each).

## Out of scope / follow-ups

- Refactoring the two excluded validators (record 0047, option 3): not needed while SonarQube accepts them.
- Step-12 lesson for the template repository: the `go` profile's seeded `.golangci.yml` should enable
  `gocognit` at 15, and the profile's `stack.md` should not claim SonarQube has no local equivalent for
  `go:S3776`.
