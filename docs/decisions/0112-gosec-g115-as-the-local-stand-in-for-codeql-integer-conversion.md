# 0112: gosec G115 as the local stand-in for CodeQL's integer-conversion query; enabled for the whole module, no exclusions

- **Status:** Proposed
- **Date:** 2026-10-17
- **Area:** —
- **Source:** Issue #268
- **Supersedes:** —

## Context

CodeQL's `go/incorrect-integer-conversion` (high severity) raised five alerts on PR #265 (`internal/sysstats/memory.go`
at commit `68ddee1412`, lines 118 twice, 130, and 132 twice: `ParseUint` results converted to `int64`). No local gate
reported them, so the fix cost a push, a CI cycle and a delta review after the pull request was open. Issue #266 (PR #267)
added a written rule to `.squad/stack.md` and a grep as the local substitute. Decision 0001 accepts that local analyzers
differ from CI's analysis; 0047 narrowed that gap for one SonarQube rule with a golangci-lint linter. CodeQL itself runs
only in CI.

golangci-lint v2.14.0 (the version CI pins) ships gosec, whose rule G115 reports integer conversions that can overflow.
Measured with only G115 enabled:

- on PR #265 before its fix (`68ddee1412`): `memory.go` lines 118, 119, 130, 132 and 140 — every line CodeQL flagged, plus
  two conversions whose bound was checked in another function (a sum of bounded values, and a value bounded in
  `parseMemLine`), the form the `stack.md` rule already forbids and that the fix changed as well;
- on PR #265 after its fix (`604b950a6c`): nothing in production code — the bound check right before `int64(v)` is accepted;
- on `main`: nothing in production code; three findings in test helpers, none on a parsed value (a deliberate low/high
  byte split, a rune already limited to U+00FF by another helper, a non-negative `int` widened to `uint64`);
- on a scratch probe of the forms the `stack.md` rule names: silent on a same-function upper-bound check followed by
  `return`, on `ParseUint(s, 10, 16)` into `uint16`, on `ParseInt(s, 10, 32)` into `int32` and on a two-sided check before
  `int32(n)`; it reports a conversion whose check sits in a called function (as CodeQL does), a conversion to unsigned with
  only an upper bound, and a signed-to-unsigned conversion without check. It does not accept a guard that ends in a call
  such as `t.Fatalf` without `return` or `else`.

G115 is broader than CodeQL's query: it looks at every integer conversion, not only values from `strconv`. Whether CodeQL
reports `int(n)` of a `ParseInt(s, 10, 64)` result, which G115 accepts on 64-bit targets, is *unverified* (CodeQL cannot
run in the session).

## Options considered

- **Enable `gosec` limited to G115 for the whole module and rewrite the three test helpers into forms G115 accepts** —
  chosen: the measured findings are a superset of CodeQL's alerts, no accepted bound check is reported, CI's Lint step runs
  it on every pull request, and no exclusion list has to be kept.
- **Enable G115 for production files only (an exclusion rule for `_test.go`)** — rejected: test code would stay unchecked,
  and once the three helpers change for any reason `warn-unused` reports the rule as stale; the three rewrites are small and
  keep the helpers' output.
- **`//nolint:gosec` at the three test sites** — rejected: spreads the exceptions, and a stale directive is reported only
  by `nolintlint`, which is not enabled (as in 0047).
- **Enable every gosec rule** — rejected: on `main` it adds nine findings outside the issue's scope (five G101 "hardcoded
  credentials" on option names, four G304 "file inclusion" on configured paths), each needing a decision of its own.
- **Keep only the written rule and the grep** — rejected: G115 met the issue's condition, and the grep is a reading duty
  that missed the five alerts once.
- **Run CodeQL locally** — rejected: the CodeQL CLI is not part of the toolchain and the session cannot install it; CI's
  code scanning stays the authoritative check either way.

## Decision

`.golangci.yml` enables `gosec` with `settings.gosec.includes: [G115]` and no exclusion for it; the three test helpers that
G115 reported on `main` are rewritten so that the whole module passes. `.squad/stack.md` names G115 as the local stand-in
for `go/incorrect-integer-conversion` in the *Analyzer gate*, the integer-conversion rule and *Not checkable in a cloud
session*, and points here.

## Consequences

- A narrowing or sign-changing integer conversion without a range check in the same function now fails the local
  *Analyzer gate* (changed files) and CI's Lint step (whole module) before CodeQL sees it. Because G115 is broader than
  CodeQL, it can report a conversion CodeQL would accept; it is fixed like any diagnostic, by one of the forms in
  `.squad/stack.md`.
- A guard in a test that ends in `t.Fatalf` needs the conversion in its `else` branch (or a `return`), because G115 does
  not treat the call as the end of the path.
- A new G115 exclusion (a rule in `.golangci.yml` or a `//nolint:gosec` directive) needs its own Lead decision record.
- CodeQL code scanning remains authoritative; a CodeQL alert G115 missed is evidence to revisit this record. Revisit also
  when a golangci-lint update changes G115's range analysis (a new finding on unchanged code after the pin moves).
