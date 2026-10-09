# 0047: gocognit at 15 as the local stand-in for SonarQube's cognitive complexity rule; two existing validators excluded by name

- **Status:** Accepted
- **Date:** 2026-10-04
- **Area:** —
- **Source:** Issue #111
- **Supersedes:** —

## Context

SonarQube Cloud reported seven `go:S3776` issues (cognitive complexity above the allowed 15) in test files of a pull request. They
were fixed in squad step 11, but the local *Analyzer gate* had been clean, because no enabled linter measured cognitive
complexity. Decision 0001 accepts that local analyzers may differ from the CI quality profile; this record narrows that gap for one
rule. golangci-lint (the version CI uses) ships `gocognit`, which follows the same published specification; with
`min-complexity: 15` it flagged, on the pull request's tree, a superset of the seven findings. The two measures are close but not
identical. The largest known difference: SonarQube's Go analyzer does not count an `if` whose condition is the bare `err != nil`
check, gocognit does. On `main` that made gocognit flag two validators (`(*ConnectionSnapshot).Validate`, `(*MariaDBStatus).Validate`),
both a sequence of error checks, that SonarQube rates at 15 and 11. No other function was above 15. The evidence is a small sample, so
a `go:S3776` finding that gocognit misses can still arrive in step 11. CI's Lint step runs the same `.golangci.yml` on the whole
module, so whatever is enabled must pass on all existing code.

## Options considered

- **Enable `gocognit` at 15 for all files and exclude the two validators by file and function name in `.golangci.yml`** — chosen:
  one auditable list, no change to production code, and `warn-unused` reports a stale rule.
- **Suppress the validators with `//nolint:gocognit` in the code** — rejected: edits production files in a tooling change, spreads
  the exceptions, and a stale directive is reported only by `nolintlint`, which is not enabled.
- **Refactor the validators in the same change** — rejected: a production change with tests for two functions SonarQube accepts.
- **Enable it for `_test.go` files only** — rejected: SonarQube applies the rule to production code too.
- **A higher threshold so the validators pass** — rejected: it misses six of the seven findings.
- **A separate tool in the analyzer gate script** — rejected: the script is template-managed and CI would not run it.
- **Keep relying on step 11** — rejected: every such finding costs a fix round after the pull request is open.

## Decision

`.golangci.yml` enables `gocognit` with `min-complexity: 15` and holds two exclusion rules, each limited to the linter, one file and
one function name; `linters.exclusions.warn-unused: true` reports a rule that no longer matches. `.squad/stack.md` (*Analyzer gate*)
states that gocognit stands in for `go:S3776`, that it is stricter than SonarQube for bare `if err != nil` checks, and points here.

## Consequences

- A function above 15 now fails the local *Analyzer gate* (changed files) and CI's Lint step (whole module) before SonarQube sees it.
  gocognit can flag a function SonarQube would accept; it is fixed like any diagnostic, usually by extracting helpers.
- A new gocognit exclusion (a rule in `.golangci.yml` or a `//nolint:gocognit` directive) needs its own Lead decision record; adding
  one without a record is a review finding.
- The two exclusions apply at any complexity, so the validators are not capped locally; SonarQube's `go:S3776` (step 11) remains the
  check for them. When one is refactored below 16, `warn-unused` warns and removing the stale rule is a review duty.
- Revisit if SonarQube's Go analyzer or gocognit changes how error checks are counted, or if a local tool implementing SonarQube's
  measure becomes available.
