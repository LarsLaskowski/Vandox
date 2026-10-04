# 0047: gocognit at 15 as the local stand-in for SonarQube's cognitive complexity rule; two existing validators excluded by name

- **Status:** Proposed
- **Date:** 2026-10-04
- **Source:** Issue #111
- **Supersedes:** —

## Context

After PR #110 was opened, SonarQube Cloud reported seven `go:S3776` issues ("Refactor this method to
reduce its Cognitive Complexity from N to the 15 allowed", N = 17, 22, 22, 26, 30, 33, 38), all in test
files (`internal/model/model_test.go`, `internal/wire/decode_test.go`, `internal/wire/encode_test.go`).
They were fixed in squad step 11 and are closed. The local *Analyzer gate* (`go vet` and golangci-lint with
`.golangci.yml`) had been clean, because no enabled linter measures cognitive complexity. Decision 0001
already accepts that the local analyzers may differ from the CI quality profile; this record narrows that
gap for one rule.

SonarQube's rule threshold is 15 (rule parameter `threshold`, default of the "Sonar way" Go profile the
project uses) and it flags a function whose complexity is above 15. golangci-lint v2.13.1 (the version CI
uses) ships `gocognit`, which measures cognitive complexity after the same published specification and,
with `min-complexity: 15`, reports a function whose complexity is above 15.

The two measures are close but not identical, and the comparison rests on a small sample (the seven
PR #110 findings and the `main` tree). Per-function scores differ by 1–7 points on several constructs; on
some they agree (both score `TestDecoder_Limits` 38). The largest known difference: SonarQube's Go analyzer
does not count an `if` whose condition is the bare `err != nil` check (neither as an increment nor as
nesting); gocognit does. A compound condition such as `if err != nil || len(recs) != 1` is counted by both.
Measured on `main` at a7e9b4a:

- SonarQube rates the whole file `internal/model/connection.go` at 15 and `internal/model/mariadb.go` at
  11 and raises no `go:S3776` there; gocognit rates `(*ConnectionSnapshot).Validate` at 37 and
  `(*MariaDBStatus).Validate` at 21 — both made of sequential error checks.
- No other function in `cmd/` or `internal/`, test files included, is above 15 for gocognit.
- On the tree SonarQube analyzed for PR #110 (389aef8), gocognit at 15 reports nine test functions, a
  superset of the seven SonarQube flagged, plus the two validators above.

So in every case measured (PR #110, `main`) gocognit at 15 flagged at least what SonarQube flagged, and
in code built from bare error checks it is stricter. That is evidence, not a guarantee: a SonarQube
`go:S3776` finding gocognit misses can still occur and then still arrives in squad step 11.

`.golangci.yml` and `.squad/stack.md` are seeded files owned by this repository; `.squad/tools/analyzer-check.py`
is template-managed and is not changed here. CI's Lint step runs golangci-lint on the whole module with
the same `.golangci.yml`, so whatever is enabled there must pass on all existing code.

## Options considered

1. **Enable `gocognit` with `min-complexity: 15` in `.golangci.yml` for all files, and exclude exactly the
   two existing validators by file and function name** — the local gate and CI's Lint step then catch, in
   every case measured, at least what SonarQube flagged; CI stays green without touching production code; the price is a check
   stricter than SonarQube for code built from many bare `if err != nil` checks.
2. **Enable gocognit at 15 and suppress the two validators in the code with `//nolint:gocognit // <reason>`**
   on `(*ConnectionSnapshot).Validate` (`internal/model/connection.go:75`) and `(*MariaDBStatus).Validate`
   (`internal/model/mariadb.go:47`) — as narrow as option 1 (one linter, one function each) and the reason
   sits next to the code. Rejected: it edits two production Go files in a squad-maintenance change, which
   issue #111's plan excludes (no Go file changes) and which would turn a tooling change into a code change;
   the exceptions would be spread over the code instead of one auditable list in `.golangci.yml`; and a
   stale `//nolint` is reported only by `nolintlint`, which is not enabled, whereas `warn-unused` reports a
   stale exclusion rule.
3. **Enable gocognit at 15 and refactor the two validators in the same change** — no exclusions, but a
   production code change (with tests and the coverage gate) in a squad-maintenance change, for two
   functions SonarQube itself accepts.
4. **Enable gocognit at 15 for `_test.go` files only** — matches where the seven findings occurred and needs
   no exclusion, but leaves production code unchecked although SonarQube applies the rule there too.
5. **A threshold above 15 (e.g. 37, so the validators pass)** — no exclusion, but misses every SonarQube
   finding between 16 and 37, i.e. six of the seven from PR #110.
6. **Run a separate cognitive-complexity tool from the analyzer gate script** — the script is
   template-managed and cannot be changed in this repository, and CI would still not run it.
7. **Keep relying on step 11** — status quo; every such finding keeps costing a fix round after the PR is
   open, which is what issue #111 reports.

## Decision

Option 1. `.golangci.yml` enables `gocognit` and sets `linters.settings.gocognit.min-complexity: 15`.
`linters.exclusions.rules` holds two rules, each limited to linter `gocognit`, one file
(`path: ^internal/model/connection\.go$`, `path: ^internal/model/mariadb\.go$`) and one function name in the
message (`` `(*ConnectionSnapshot).Validate` ``, `` `(*MariaDBStatus).Validate` ``, backticks included so no
other function name matches); `linters.exclusions.warn-unused: true` makes golangci-lint warn once a rule
no longer matches. `.squad/stack.md` (*Analyzer gate*) states that gocognit stands in for `go:S3776`, that
it is stricter than SonarQube for bare `if err != nil` checks, without claiming parity, and points to this record for the exclusions.

## Consequences

- A function above 15 now fails the local *Analyzer gate* (changed files) and CI's Lint step (whole module)
  before SonarQube sees it.
- gocognit can flag a function SonarQube would accept. Such a finding is still fixed like any analyzer
  diagnostic (usually by extracting helpers); a new exclusion needs its own Lead decision record. Adding
  more exclusions without one is a review finding.
- The two excluded validators stay as they are. When one of them is refactored below 16, its exclusion rule
  becomes unused, golangci-lint warns, and the rule should be removed in that change.
- Revisit if SonarQube's Go analyzer or gocognit changes how error checks are counted, or if a local tool
  that implements SonarQube's Go measure becomes available.
