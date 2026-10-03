# 0020: Analysis before alerting — v0.1.0 is the forensics release

- **Status:** Proposed
- **Date:** 2026-10-03
- **Source:** Issue #6
- **Supersedes:** —

## Context

The immediate need is to understand why the server goes down (memory exhaustion, OOM killer, MariaDB,
Plesk and mail failing). Alerts without understanding produce noise; rules are only good once the failure
patterns are known.

## Options considered

1. **Alerting first** — early warnings sooner; thresholds guessed without evidence.
2. **Analysis first** — v0.1.0 collects, imports, stores and reconstructs outages; alert rules follow,
   based on what the analysis showed.

## Decision

Option 2: v0.1.0 is the forensics release — collection, log import, spool and backfill, storage and the
historical views needed to reconstruct outages. Alerting, the nightly report and remote actions build on
it in later releases.

## Consequences

- The first release is useful for post-mortems but does not yet warn.
- The data model has to serve later rules without migration surprises.
