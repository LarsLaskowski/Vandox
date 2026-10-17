# Plan: Evaluate gosec G115 as a local stand-in for CodeQL go/incorrect-integer-conversion

Source: Issue #268 (follow-up to #266 / PR #267; evidence from PR #265)
Status: Draft
Tier: security — the change enables a linter in `.golangci.yml`, which CI's Lint step runs on the whole module, so it
changes a check the pipeline gates on (issue note; `.squad/project.md`, *Integration surface*: a configuration file a
decision record constrains).

## Problem / root cause

CodeQL's `go/incorrect-integer-conversion` runs only in CI. On PR #265 it raised five alerts that no local gate had
reported. PR #267 added a written rule and a grep to `.squad/stack.md`, but no tool reports the pattern locally. The
issue asks for a measurement of gosec G115 and, only if it holds up, for the linter to be enabled in `.golangci.yml`.

Claims of the issue, checked:

- "PR #267 documents the rule but leaves no local gate": **confirmed** — `.squad/stack.md` lines 231-240 (*Writing code*)
  and line 145 (*Not checkable in a cloud session*) say so; `.golangci.yml` does not enable gosec.
- "CodeQL alerts on PR #265": **confirmed** from the five review comments of `github-advanced-security[bot]` on commit
  `68ddee1412` (`internal/sysstats/memory.go`, lines 118 twice, 130, 132 twice; code-scanning alerts 2 to 6). The
  code-scanning API itself returns HTTP 403 in this session, so CodeQL's current alert state on `main` is *unverified*.
- "Changing `.golangci.yml` is security tier": **confirmed** — CI (`.github/workflows/ci.yml`, step *Lint*,
  golangci-lint v2.14.0) runs it on the whole module, and the local *Analyzer gate* runs it on changed files.

### Measurement (Lead, golangci-lint v2.14.0 — the CI pin — with only `gosec` and `includes: [G115]` enabled)

| Tree | G115 findings (one per line; golangci-lint keeps one issue per line) |
| ---- | -------------------------------------------------------------------- |
| PR #265 before the fix, `68ddee1412` | `internal/sysstats/memory.go` 118, 119, 130, 132, 140; plus the three test sites below |
| PR #265 after the fix, `604b950a6c` | none in production code; the three test sites below |
| `main` (`40f8b36`, this branch) | none in production code; three in tests: `internal/config/decode_test.go:132` (`byte(u)`, uint16 to byte), `internal/wire/decode_test.go:1415` (`byte(r)`, rune to byte), `internal/wire/decode_test.go:1762` (`uint64(size)`, int to uint64) |

- Every CodeQL alert line (118, 130, 132) is reported. The two extra lines are not false positives under the project's
  rule: line 119 converts a sum of three map values to `int64`, line 140 converts `l.value`, bounded only in
  `parseMemLine` — both a bound checked elsewhere, which `.squad/stack.md` forbids and which the fix commit changed too.
- The accepted bound check of the fix (`if v > maxKBValue { return … }` then `int64(v)`) and every production conversion
  on `main` (`config.go` `ParseUint(text, 10, 16)` into `uint16`, `sysstats.go`, `pressure.go`, `logger.go`,
  `sdnotify.go`) are silent.
- The three test findings are not on parsed values; CodeQL's query (source: a `strconv` parse) would not report them.
  G115 is broader: it looks at every integer conversion.
- Full gosec (every rule) on `main` adds 5 G101 and 4 G304 findings — out of scope (see *Options considered*).

Scratch probe (Lead; Security re-runs it in its review — a package with these functions, linted with the same config):

| # | Form | G115 |
| - | ---- | ---- |
| 1 | `v, _ := strconv.ParseUint(s, 10, 64); return int64(v)` | reports |
| 2 | `ParseUint(…, 64)`; `if v > maxKB { return 0 }`; `int64(v)` (constant `1 << 40`) | silent |
| 3 | as 2 with `math.MaxInt64` | silent |
| 4 | `ParseUint(s, 10, 16)` then `uint16(v)` | silent |
| 5 | `n, _ := strconv.Atoi(s); return int32(n)` | reports |
| 6 | `Atoi`; `if n < math.MinInt32 \|\| n > math.MaxInt32 { return 0 }`; `int32(n)` | silent |
| 7 | `ParseInt(s, 10, 64)` then `int(n)` (amd64; Security: also with `GOARCH=386`) | silent — CodeQL's behavior here *unverified*; the `stack.md` grep stays for it |
| 8 | `ParseInt(s, 10, 32)` then `int32(n)` | silent |
| 9 | bound checked in a called function `ok(v)`, then `int64(v)` | reports (as CodeQL) |
| 10 | `if err != nil \|\| v > maxKB { return 0 }`; `int64(v)` | silent |
| 11 | `Atoi`; only `if n > math.MaxUint32 { return 0 }`; `uint32(n)` | reports (negative wraps — a true positive) |
| 12 | `ParseInt(s, 10, 64)` then `uint64(n)` | reports |
| 13 | `ParseInt`; `if n < 0 { return 0 }`; `uint64(n)` | silent |
| 14 | `if r > 0xFF { t.Fatalf(…) }` (no `else`, no `return`), then `byte(r)` | reports — `t.Fatalf` is not seen as the end of the path |
| 15 | `if r < 0 \|\| r > 0xFF { t.Fatalf(…) } else { …byte(r)… }` | silent |
| 16 | `binary.LittleEndian.AppendUint16(out, u)` (no conversion) | silent |
| 17 | `float64(allocated) / float64(size) > maxAllocFactor` | silent |
| 18 | `Atoi`; only `if n > math.MaxInt32 { return 0 }`; `int32(n)` | reports (Reviewer, confirmed by Lead) — a signed value needs the lower bound too |
| 19 | `ParseInt(s, 10, 64)`; only `if n > math.MaxInt32 { return 0 }`; `int32(n)` | reports (as row 18) |
| 20 | `ParseInt(s, 10, 64)`; `if n < math.MinInt32 \|\| n > math.MaxInt32 { return 0 }`; `int32(n)` | silent |
| 21 | `Atoi`; `if n < 0 \|\| n > math.MaxUint32 { return 0 }`; `uint32(n)` | silent |
| 22 | `ParseUint(s, 10, 64)`; only an upper bound (`math.MaxInt32`, `math.MaxUint16`); `int32(v)`, `uint16(v)` | silent — an unsigned value needs the upper bound only |

Result: G115 reports the CodeQL alerts and no accepted bound check from `.squad/stack.md`. The issue's condition holds;
the linter is enabled.

Related defect found on the way: none (the three test conversions are correct for their inputs; they only lack a form
G115 can see).

## Acceptance criteria

- [ ] AC1: `.golangci.yml` enables `gosec` limited to rule G115 (`linters.settings.gosec.includes: [G115]`), with no
  exclusion rule and no `//nolint` directive of any form (`//nolint:gosec`, bare `//nolint`, `//nolint:all`) anywhere in
  the module (none exists on `main` today).
- [ ] AC2: `golangci-lint run ./...` (v2.14.0, the invocation CI's Lint step runs on the whole module) reports
  `0 issues.` on the branch, and the *Analyzer gate* passes.
- [ ] AC3: with the branch's `.golangci.yml`, `golangci-lint run ./...` on the PR #265 tree at `68ddee1412` reports G115 on
  `internal/sysstats/memory.go` lines 118, 130 and 132 (the CodeQL alert lines), and on the tree at `604b950a6c` reports
  no G115 finding outside the three test sites the branch rewrites.
- [ ] AC4: the three test helpers keep their output: `utf16LE` still yields the BOM `FF FE` followed by each UTF-16 code
  unit low byte first; `latin1Stream` still writes one byte per character; the allocation bound in
  `TestDecoder_AllocationIsBoundedForDeeplyNestedRecords` is still `allocated <= maxAllocFactor * size`.
  Tests: `TestDecodeStrict_Valid` (subtest `utf-16 le bom`), `TestDecoder_SharedCases` (the `latin1` cases),
  `TestDecoder_AllocationIsBoundedForDeeplyNestedRecords` — all pass unchanged.
- [ ] AC5: `.squad/stack.md` no longer says that no local gate reports the CodeQL query, and names G115, its scope and
  record 0112 in the three places below, claims no more than was measured (G115 reported every measured CodeQL alert and
  accepts the rule's two forms; a 64-bit parse result converted to `int`/`uint` is accepted by G115, CodeQL's view
  unverified), states in the rule's first form that a signed value also needs a lower-bound check against the target
  type's minimum (zero for an unsigned target; probe rows 18 to 22), and keeps the grep for `strconv` results converted to `int`/`uint` in the *Local substitute* column of
  *Not checkable in a cloud session*; `.squad/project.md` lists 0112 among the records that constrain `.golangci.yml`.

## Verification without tests

The change touches test code (three helpers) but no production code and no test case. Steps 4 (*Skeleton*) and the
*Coverage gate* are not applicable (no production line changes, no new member); step 5 is the Tester's helper rewrite,
whose "failing first" signal is G115 itself (it reports the three sites on `main` with the new config). Per criterion:

| AC | Verified by | Who |
| -- | ----------- | --- |
| AC1 | reading `.golangci.yml`; `grep -rn "nolint" --include=*.go .` empty (covers `//nolint:gosec`, bare `//nolint` and `//nolint:all`) | Dev (step 6), Reviewer (step 8), Security (diff review) |
| AC2 | `golangci-lint run ./...` from the repository root, whole module, output `0 issues.`; then the *Analyzer gate* | Dev before the review; CI step *Lint* on the PR is authoritative |
| AC3 | download the two trees (`gh api repos/LarsLaskowski/Vandox/tarball/<sha>` into the scratchpad, extract), run `golangci-lint run --config <branch .golangci.yml> ./...` in each, compare with the lines above | Dev once before the review, log the output in `log.md`; Security re-checks in its diff review together with the scratch probe table above |
| AC4 | *Test* from `.squad/stack.md` (`go test ./... -race`) green, the three named tests included | Tester (step 5), Dev (step 6) |
| AC5 | reading the three `stack.md` places and the `project.md` line; `python3 .squad/tools/config-check.py`, `python3 .squad/tools/scope-check.py` | Reviewer (step 8), orchestrator (step 7) |

## Approach

1. Tester rewrites the three test helpers (exact edits under *Test files*). Order: before the Dev's linter change, so the
   module is clean when gosec is enabled.
2. Dev edits `.golangci.yml`:

   ```yaml
   linters:
     default: standard
     enable:
       - errorlint
       - gocognit
       - gocritic
       - gosec
       - misspell
       - revive
       - unconvert
     settings:
       gocognit:
         # SonarQube's go:S3776 threshold; stricter for bare `if err != nil` checks (record 0047).
         min-complexity: 15
       gosec:
         # Only G115, the local stand-in for CodeQL's go/incorrect-integer-conversion (record 0112).
         includes:
           - G115
   ```

   (`exclusions` and `formatters` unchanged.) The key `linters.settings.gosec.includes` was exercised: with it only G115
   reports.
3. Dev edits `.squad/stack.md` (owner Dev; the Lead may not write under `.squad/`):
   - *Analyzer gate*, item 2: append after the sentence ending "(two existing validators are excluded by name, without
     a complexity cap, record 0047)." the text:
     "`gosec`, limited to rule G115, is the local stand-in for CodeQL's `go/incorrect-integer-conversion` (record 0112):
     it reported every CodeQL alert measured on PR #265 and accepts the two forms of the integer-conversion rule under
     *Writing code*, but reports an upper-bound check alone on a signed value (the rule's lower bound is missing). It looks at integer conversions of any source, not only parsed values. It does not match CodeQL
     everywhere: a conversion of a 64-bit parse result to `int` or `uint` is accepted by G115 (measured for `int(n)` of
     `ParseInt(s, 10, 64)`, also with `GOARCH=386`), and whether CodeQL reports it is unverified — check such
     conversions by hand (see *Not checkable in a cloud session*). A guard that ends in `t.Fatalf` needs the conversion in its `else` branch or a
     `return`. A G115 exclusion needs a Lead decision record."
   - *Writing code* (Go), the integer-conversion bullet: replace "CodeQL's `go/incorrect-integer-conversion` (high
     severity) runs only in CI, no local gate reports it, and on PR #265 a bound checked elsewhere (a digit-count limit,
     a check inside a helper) did not count: five alerts on `ParseUint` results converted to `int64`." with
     "CodeQL's `go/incorrect-integer-conversion` (high severity) runs only in CI and stays authoritative; gosec G115 in
     the *Analyzer gate* reported every CodeQL alert measured on PR #265 and accepts both forms above, but accepts a
     conversion of a 64-bit parse result to `int` or `uint` whose treatment by CodeQL is unverified (record 0112). On PR #265 a bound checked elsewhere (a
     digit-count limit, a check inside a helper) did not count: five alerts on `ParseUint` results converted to `int64`."
   - *Not checkable in a cloud session*, the row "Go conversions of a parsed number …": column *Missing locally* becomes
     "CodeQL runs only in CI"; column *Local substitute* becomes "gosec G115 in the *Analyzer gate* (record 0112), which
     reported every measured CodeQL alert but accepts a 64-bit parse result converted to `int` or `uint` (CodeQL's view
     unverified); the integer-conversion rule under *Writing code*, checked by grepping the new `strconv.ParseUint`,
     `strconv.ParseInt` and `strconv.Atoi` calls and every conversion of their results to `int` or `uint`"; the other
     columns unchanged. The grep stays: it is the only local check for the form G115 accepts and CodeQL may not.
4. Dev edits `.squad/project.md`, *Integration surface*, the list "Among them: … 0047 (`gocognit` and its exclusions in
   `.golangci.yml`) and 0052 (image labels)" to "… 0047 (`gocognit` and its exclusions in `.golangci.yml`), 0112 (`gosec`
   limited to G115 in `.golangci.yml`, no exclusions) and 0052 (image labels)".
5. Dev runs *Format* (`gofmt` touches nothing new), the whole-module `golangci-lint run ./...`, the *Analyzer gate*,
   *Test*, and the AC3 measurement.

## Options considered

- **G115 only, whole module, three test helpers rewritten** — chosen (record 0112).
- **G115 with an exclusion for `_test.go`** — lost: leaves tests unchecked; `warn-unused` turns the rule stale once the
  helpers change; the rewrites are three lines.
- **`//nolint:gosec` at the three sites** — lost: spreads exceptions, `nolintlint` is not enabled to report stale ones.
- **All gosec rules** — lost: 9 more findings on `main` (5 G101, 4 G304), each a separate decision outside the issue.
- **No linter, keep the grep** — lost: the issue's condition for enabling is met by measurement.
- **CodeQL CLI locally** — lost: not in the toolchain, not installable in the session; CI stays authoritative anyway.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Go tooling | `.golangci.yml` | enable `gosec` limited to G115 (Dev) |
| Go tests | `internal/config/decode_test.go` (`utf16LE`) | no conversion: `binary.LittleEndian.AppendUint16` (Tester) |
| Go tests | `internal/wire/decode_test.go` (`latin1Stream`, `TestDecoder_AllocationIsBoundedForDeeplyNestedRecords`) | guarded conversion; float ratio bound (Tester) |
| Squad project knowledge | `.squad/stack.md`, `.squad/project.md` | text as in *Approach* (Dev) |
| Records | `docs/decisions/0112-…md`, `docs/decisions/README.md` | Proposed record and index row (Lead, done) |

## Signatures (for the Dev's skeleton)

None — no new or changed member; the helpers keep their signatures (`func utf16LE(s string) []byte`,
`func latin1Stream(t *testing.T, lines []string) []byte`).

## Test files

No new test file. Edits (Tester), each exercised under the full branch config in the scratch probe (silent, no other
linter finding):

- `internal/config/decode_test.go`, `utf16LE`: replace `out = append(out, byte(u), byte(u>>8))` with
  `out = binary.LittleEndian.AppendUint16(out, u)`; add `"encoding/binary"` to the imports.
- `internal/wire/decode_test.go`, `latin1Stream`: replace the inner loop body `plain = append(plain, byte(r))` with

  ```go
  if r < 0 || r > 0xFF {
      t.Fatalf("latin1Stream: U+%04X is above U+00FF, want one byte per character", r)
  } else {
      plain = append(plain, byte(r))
  }
  ```

  (`requireLatin1Case` already rejects such a case; the guard makes the helper's own bound visible to G115. The `else`
  is required — probe row 14.)
- `internal/wire/decode_test.go`, `TestDecoder_AllocationIsBoundedForDeeplyNestedRecords`: replace
  `if limit := uint64(maxAllocFactor) * uint64(size); allocated > limit {` and its `t.Errorf` with

  ```go
  if ratio := float64(allocated) / float64(size); ratio > maxAllocFactor {
      t.Errorf("decoding allocated %d bytes (%.1f times the batch of %d bytes), want at most %d times", allocated, ratio, size, maxAllocFactor)
  }
  ```

  The bound is the same (`allocated` stays far below 2^53, where `float64` is exact; the existing `t.Logf` already uses
  this ratio).

Existing test code that calls a changed signature: none.

Files of the change no unit test can cover: `.golangci.yml`, `.squad/stack.md`, `.squad/project.md` — configuration and
documentation, verified as in *Verification without tests*; no coverage exclusion needed (not Go source).

## Areas

None — no product behavior changes; record 0112 has `Area: —` like 0047.

## Documentation updates

- `.squad/stack.md` (three places) and `.squad/project.md` (one list) — Dev, text in *Approach*.
- `docs/decisions/0112-gosec-g115-as-the-local-stand-in-for-codeql-integer-conversion.md` and its index row — Lead (done;
  status to `Accepted` at approval).
- `README.md`, `docs/ARCHITECTURE.md`, `docs/CONTRIBUTING.md`: none (CONTRIBUTING names golangci-lint only as a tool).

## Architecture check

No guarantee from `docs/ARCHITECTURE.md` or `.squad/project.md` is touched; the change only adds a check. It fails
closed: a new G115 finding fails CI's Lint step and the local *Analyzer gate*. Record 0001 (local gates vs CI analysis)
and 0047 (gocognit and its exclusions) are not contradicted: the gocognit settings and exclusions are unchanged and
`warn-unused` keeps applying.

## Security considerations

- [x] No limits on parsed input change (no production code).
- [x] No user-visible message changes.
- Runtime details only CI can confirm: CI's golangci-lint-action runs v2.14.0, the version measured here; a different
  binary in CI could rate G115 differently; the pin in `ci.yml` and in `.squad/stack.md` is the same, and the Dev's
  whole-module run (AC2) uses that binary. Whether CodeQL accepts every form G115 accepts (probe row 7) is *unverified*; CodeQL code
  scanning on the PR stays the authoritative check and a missed alert is the trigger to revisit record 0112.

## Decision records

- `docs/decisions/0112-gosec-g115-as-the-local-stand-in-for-codeql-integer-conversion.md` (Proposed, index row added).

## Challenge

Challenge notes (Devil's Advocate, minor), verbatim:

> The rewritten latin1Stream guard puts the conversion in an else after t.Fatalf. The Dev should confirm that revive and
> gocritic stay silent on it in the whole-module run (AC2). If one does flag it, restructure to
> `if ok { append } else { Fatalf }` instead of adding a nolint or an exclusion.

Accepted. The Tester writes the guard as under *Test files*; the Dev's whole-module run (AC2) is the check that revive
(`indent-error-flow`, `superfluous-else`) and gocritic stay silent on it. If either reports it, the Tester restructures it
to `if r >= 0 && r <= 0xFF { plain = append(plain, byte(r)) } else { t.Fatalf(…) }` — no `//nolint`, no exclusion
(AC1). The Dev confirms G115 stays silent on the restructured form in the same run.

## Security review

Round 1 verdict: CHANGES_REQUIRED. Round 2 is a delta confirmation of the points below (plan text only; no scope change).

- **B1 (blocking, wording)** — accepted. The planned `.squad/stack.md` text claimed G115 "reports every integer
  conversion that can overflow" and "the same conversions" as CodeQL, which was not measured: probe row 7 (`int(n)` of
  `ParseInt(s, 10, 64)`) is silent in G115, also with `GOARCH=386`, and CodeQL's view on it is unverified. All three
  `stack.md` texts in *Approach* step 3 now say that G115 reported every measured CodeQL alert (PR #265) and accepts the
  rule's two forms, and that a 64-bit parse result converted to `int`/`uint` is accepted by G115 with CodeQL's view
  unverified. The grep for `strconv` results converted to `int`/`uint` stays in the *Local substitute* column of *Not
  checkable in a cloud session*. AC5 now states this. Record 0112 already worded it this way; one sentence of its *Context* was made precise (G115's
silence on row 7 measured on amd64 and with `GOARCH=386`, not only "on 64-bit targets"; the grep stays for that form).
- **N1 (non-blocking)** — accepted. AC1 and its check widened to `grep -rn "nolint" --include=*.go .` (covers bare
  `//nolint` and `//nolint:all`); the module has no `nolint` directive today.

## Lead decision (review round 1)

Reviewer round 1, blocking: the `.squad/stack.md` texts say G115 accepts the rule's two forms, but the rule's first form
("an upper-bound check against a constant no larger than the target type's maximum"), applied to its own example
`int32(n)` of an `Atoi` result, is reported by G115 (probe rows 18, 19, measured again by the Lead). The defect is in the
rule, not in the G115 sentences: an upper bound alone does not make a signed value fit a narrower type (a negative value
below the target's minimum wraps), so G115 is right to report it. Decision: the rule's first form is corrected to
require, for a signed value, also a lower-bound check against the target type's minimum (zero for an unsigned target);
with that correction "accepts the two forms" holds for every case the rule names (rows 2, 3, 4, 6, 8, 20, 21, 22), and
the Analyzer-gate sentence adds that an upper bound alone on a signed value is reported. The non-blocking finding
("below" -> "above") is applied too. The rule text is project knowledge in `.squad/stack.md`, which this plan already
edits (owner Dev); the scope does not change. Record 0112's *Context* is aligned (Lead). A delta review follows.

## Out of scope / follow-ups

- Other gosec rules (G101, G304 findings on `main`): not proposed; would need their own evaluation.
- No follow-up issue.
