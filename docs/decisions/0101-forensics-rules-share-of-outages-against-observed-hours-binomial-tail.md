# 0101: Recurrence and correlation rules compare the share of outages with the share of observed system-log hours and decide by a binomial tail

- **Status:** Accepted
- **Date:** 2026-10-13
- **Area:** Detection, alerts and reports
- **Source:** Issue #28
- **Supersedes:** —

## Context

The forensics report ([0100](0100-forensics-report-as-markdown-from-vandoxd-report.md)) has to say whether outages recur at a time
of day and whether they follow a backup, a boot or use of the Plesk panel, and which programs are the largest permanent and transient
memory consumers. The Product Manager fixed the frame: fixed, tested rules, each comparing its share of incidents with the base rate
over the whole period; missing data counts as unknown, never as "no" ([0028](0028-data-gaps-are-always-recorded.md)); detection is
deterministic ([0008](0008-deterministic-detection-and-alerting.md)).

What the data allows: incidents have a first kill and a stored hypothesis (growth and consumers after the last boot of the lookback,
[0096](0096-outage-incidents-rebuilt-from-stored-data-merged-by-overlapping-windows.md)); boots are events of the stored log lines;
backups are recognized by the program `pzstd` only (0096); process snapshots come every five minutes from the legacy monitoring log
and miss samples under memory pressure ([0093](0093-legacy-monitoring-log-snapshots-by-header-zone-and-whole-snapshot-validity.md)).
The panel's PHP engine shows as `sw-engi` in the old format (cut by `top`, `testdata/logs/legacy-monitor-old.log`) and as
`sw-engine` in the new one (`testdata/logs/legacy-monitor-new.log`); 0093 names `sw-engine-fpm` as a further form. The real server
has had a handful of outages, so the rules must hold for small numbers.

## Options considered

What the base rate is measured on:

1. **Observed hours: UTC hours with a stored line of the system log (`journal`, `syslog`)**, and for an activity its state over the
   lookback before the end of each such hour (chosen) — the system log holds the kill and boot lines, so an hour without it is time in
   which an outage could not have been seen; such hours are unknown and left out.
2. **Calendar time from the first to the last record** — counts unrecorded time as time without outage and without activity.
3. **The share of process snapshots that show the activity** — an instant, while an outage's activity is judged over its lookback, so
   the two shares are not comparable (a 30-minute backup is 2 % of the instants and 6 % of the hours with a one-hour lookback).

How an outage's activity is judged:

4. **The report's own indicator over the lookback `[first kill − incidents.window_before, first kill)`**, the same definition as for
   the base rate (chosen) — the two shares measure the same thing; unknown when no snapshot (backup, panel) or not every overlapping
   hour observed (boot).
5. **The stored hypothesis** (`backup_running`, `recent_boot_at`) — consistent with the outage's own entry, but cut at the last boot
   and without an unknown state for the boot, so it is not comparable with a base rate.

When a share counts as a pattern:

6. **A binomial tail: at least 2 outages, a share above the base rate, and the probability of at least that many outages under the
   base rate at most 1 % (for the 24 hours of the day 1 %/24 each); fewer than 3 known outages are unknown** (chosen) — small numbers
   are judged by how unlikely they are, not by a ratio; with 24 hours tested at once, random clusters rarely pass. With evenly
   observed hours, three of three outages in one hour pass and two of three do not; of ten outages, five must share an hour (four
   give 0.052 % against the 0.042 % allowed). That is strict; a nightly cause whose kills spread over two hours shows through the
   backup rule instead.
7. **A fixed factor and a minimum count (share at least three times the base rate, at least three outages)** — simpler to explain,
   but with 24 hours tested, ten outages spread at random put three into one hour in about one case of six (16 % in a
   simulation of 200,000 draws).
8. **A chi-square test** — needs expected counts of about five per cell, which a handful of outages never reaches.

Time-of-day bins:

9. **The 24 UTC hours** (chosen) — one table row each, readable; a cluster that straddles an hour boundary is split.
10. **Sliding windows of three hours** — catches straddling clusters, but overlapping detections have to be merged for the reader.

How panel use is recognized:

11. **Programs whose name starts with `sw-engi`, high when their summed RSS reaches the 90th percentile (nearest rank) of all
    process snapshots** (chosen) — matches both formats; a relative threshold needs no number nobody has measured; no panel memory
    at all makes the activity unknown.
12. **A fixed threshold in MiB** — would have to be guessed; 0093's fixtures show 99 MiB and 412 MiB for the same engine.
13. **The number of `sw-engine` processes** — the old format sums nothing else reliably; count and memory move together.

Permanent and transient consumers:

14. **Present in at least 90 % of the process snapshots: permanent, ranked by mean RSS while present; otherwise transient, ranked by
    peak RSS; top five each** (chosen) — simple, stable against missed samples, matches how the operator reads "always there" and
    "comes and goes".
15. **By the variance of the RSS** — mixes a large stable program with a small fluctuating one.

## Decision

Options 1, 4, 6, 9, 11 and 14. The rules, thresholds, formats and the unknown cases are in
[Detection, alerts and reports](../areas/detection-and-reports.md) (*Forensics report*).

## Consequences

- A pattern needs at least three known outages; with fewer the report says unknown. With two outages nothing is decided.
- The 1 % level is per rule (and per hour), not over the whole report: with the three activities and 24 hours tested, a report on
  random data still shows a false pattern in a few percent of the cases.
- An outage's backup can read "yes" in the report and `backup_running` false in its stored hypothesis when the backup ran before a
  boot inside the lookback (the hypothesis is cut at the last boot); the report lists its own indicator for every outage.
- Hours in which only other logs (web server, mail, MariaDB's own file) were stored are not observed; an archive without the system
  log has no base rate.
- Forged boot lines, `pzstd` or `sw-engi*` processes shift the rates like the forged kills of 0096 shift the outages.
- To revisit when live data (#53) gives more outages, when the agent's backup detection (#36) replaces `pzstd`, or when a real
  archive shows clusters across hour boundaries.
