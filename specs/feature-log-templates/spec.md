# Spec: Log pattern normalization and frequency tracking

Status: Approved by Lead

Source: issue #22 (depends on #21, merged as PR #204).

## Problem / motivation

v0.1.0 is the forensics release: Vandox imports the server's existing logs and explains past outages (memory exhaustion, the
OOM killer ending MariaDB, Plesk and mail going down). The signatures of #21 recognize **known** events. What is missing is the
**unknown**: an error pattern that appears for the first time, or one that suddenly occurs far more often than usual, shortly
before an outage. Lines of one pattern differ in numbers, IDs, addresses and paths, so they cannot be counted by their text.

## Behavior

- Every log line that the backend stores (import today, ingest later) is normalized into a **template**: numbers, IDs, IPv4 and
  IPv6 addresses, e-mail addresses, paths and hex values are replaced by placeholders, white space is collapsed. The same message
  always gives the same template. The exact rules are in `docs/areas/detection-and-reports.md`, *Log templates*.
- Per template (identified by the record's source, the line's program and the template text) the backend stores the first and last
  time it was seen, its total count and its count per UTC hour; per source it stores the number of lines per hour. A line is counted
  when, and only when, it is stored.
- Four queries are available to later consumers (incident reconstruction #23, the web UI): the template of a given line with its
  first-seen time; the hourly counts of a template; the templates **new** in a time range (first seen in the range, while the source
  already had lines before it); the templates whose hourly count is **above their usual level** (more than a factor times the mean
  over the covered hours of a baseline window, at least a minimum count; hours in which the source has no line at all are unknown and
  never count as quiet).
- No command, view or alert shows templates yet.
- The time to write one million log lines with their templates is measured with the storage benchmark tool and noted in
  `docs/BENCHMARKS.md`, on the development host by the squad and on the reference host (DS918+) by the operator.

## Acceptance criteria

- [ ] AC1: Similar lines (differing only in numbers, IDs, IPs, e-mail addresses, paths, hex values or white space) map to the same
  template; lines that differ in literal text do not.
- [ ] AC2: First seen, last seen and hourly counts of a template are stored with every stored log line and are queryable.
- [ ] AC3: The templates new in a time range and the templates above their usual hourly level are queryable, with hours without data
  of the source treated as unknown.
- [ ] AC4: Performance for one million lines is measured and noted (development host now; reference host procedure ready and result
  recorded when the operator has run it).

## Out of scope

- Showing templates in the web UI or a command; alerting on spikes (alerting comes after v0.1.0).
- Rebuilding the statistics from stored lines (after an upgrade from schema version 7 or a rule change): follow-up issue.
- Learning variable words without digits (clustering); per-host templates; seasonal baselines.

## Open questions

None for the Product Manager: the issue leaves "usual level" undefined and no user-visible behavior depends on it yet; the Lead
settles it in record 0095 with documented, query-level defaults. The reference-host measurement needs the operator (the squad has
no access to the DS918+).
