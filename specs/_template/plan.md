# Plan: <title>

Source: Issue #<number> | [spec.md](spec.md)
Status: Draft | Revised (n) | Approved by Security
Tier: trivial | standard | security  (tier `docs` uses no plan.md) — <one-sentence justification>

## Problem / root cause

For a bug: the cause, with file and line. For a feature: a summary of `spec.md`.
Each factual claim of the issue, checked against the code: confirmed or refuted.

## Acceptance criteria

- [ ] AC1: ... (the Tester turns each one into at least one unit test; in a plan without production or test
  code, *Verification without tests* below says how it is verified instead)

When the issue or spec supplies assets or generated content verbatim (images, SVG, configuration,
fixtures) and "identical to the issue" is an acceptance criterion: render or otherwise exercise that
content once before fixing the criterion, and record here what you did and any mismatch between the literal
content and the evident intent as an escalation question — at plan time, not at approval time.

When an acceptance criterion requires a file path (or another environment-dependent value) in an error
message, state how the tests strip it from leak checks.

## Verification without tests

Only when the change touches no production or test code, in any tier: steps 4, 5 and the *Coverage gate*
are not applicable (*Changes without production or test code* in `.squad/routing.md`). Every acceptance
criterion is then verified here instead of by a test: where and by whom (workflow step, PR dry run,
read-only check). Otherwise delete this section.

## Approach

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |

## Signatures (for the Dev's skeleton)

Every new or changed member, with its full signature — or "none".

## Test files

Named strictly by the convention in *Layout* of [`.squad/stack.md`](../../.squad/stack.md) and
[UNIT_TESTS.md](../../docs/UNIT_TESTS.md) — no combined file, no alternatives.

Existing test code that calls a changed signature (factories, helpers): the call sites, and who adapts them —
the Dev in step 4 (skeleton) when the old signature goes away, so the suite keeps building; the Tester in
step 5 when old and new signature coexist (`.squad/routing.md`, *Loop limits*). "None" if no existing
test is affected.

## Documentation updates

`README.md` (configuration table, env vars), `docs/*.md` — or "none". Every documentation edit has exactly
one owner (Dev, Tester or Lead); the Lead's approval step only touches status, index rows and bookkeeping.

## Architecture check

Which guarantees from `docs/ARCHITECTURE.md` are touched and how they are preserved.

## Security considerations

## Decision records

- `docs/decisions/NNNN-title.md` (Proposed) — or "none: no decision beyond the obvious fix"

## Challenge

Left out when the plan is written. The Lead adds it in mode `revise` only after Devil's Advocate
objections: each objection and the answer (accepted — what changed; or rejected — why). A clean challenge
(`NO OBJECTIONS`) is recorded only in `log.md`.

## Out of scope / follow-ups
