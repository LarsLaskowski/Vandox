# 0008: Deterministic detection and alerting; AI only for the nightly report

- **Status:** Proposed
- **Date:** 2026-10-03
- **Source:** Issue #6
- **Supersedes:** —

## Context

An alert at night has to be trustworthy and explainable: why it fired must be traceable to rules and
values. AI models can summarize logs well but are non-deterministic, may need an external service and
can be wrong in ways that are hard to test.

## Options considered

1. **AI-driven detection and alerting** — may find unknown patterns; not reproducible, not unit-testable,
   possibly dependent on an external service.
2. **Deterministic rules and signatures for detection and alerting, AI optional for the report** —
   reproducible and testable; AI only adds a readable summary where a mistake does no harm.

## Decision

Option 2: detection, incident reconstruction and alerting are deterministic (rules, thresholds, log
signatures). AI is optional and only used to write the nightly report (0024).

## Consequences

- Every alert can be explained and covered by unit tests.
- New failure patterns need new rules or signatures.
- Vandox works fully without any AI service.
