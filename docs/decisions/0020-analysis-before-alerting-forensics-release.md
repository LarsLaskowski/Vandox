# 0020: Analysis before alerting — v0.1.0 is the forensics release

- **Status:** Accepted
- **Date:** 2026-10-03
- **Area:** Detection, alerts and reports
- **Source:** Issue #6
- **Supersedes:** —

## Context

The immediate need is to understand why the server goes down (memory exhaustion, OOM killer, MariaDB,
Plesk and mail failing). Alerts without understanding produce noise; rules are only good once the failure
patterns are known.

## Options considered

1. **Alerting first** — early warnings sooner; thresholds guessed without evidence.
2. **Analysis first** — v0.1.0 imports, stores and reconstructs outages; alert rules follow, based on what
   the analysis showed.

## Decision

Option 2: v0.1.0 is the forensics release — log import, storage, outage incidents, the forensics report and the
web views needed to reconstruct outages from the logs the server already has. The agent (collection, spool and
backfill) and the ingest API follow in v0.2.0; alerting, the nightly report and remote actions build on both in
later releases.

The resulting rules are in the [Detection, alerts and reports](../areas/detection-and-reports.md) area.

## Consequences

- The first release is useful for post-mortems but does not yet warn, and it ships no agent: it works from
  imported logs only.
- The data model has to serve later rules without migration surprises.
