# 0096: Outage incidents are rebuilt from the stored data by `vandoxd incidents`: OOM-kill windows merged by overlap, an incident's identity kept by overlap, a structured cause hypothesis from fixed rules

- **Status:** Accepted
- **Date:** 2026-10-12
- **Area:** Detection, alerts and reports
- **Source:** Issue #23; two consequences updated by issues #212 and #213
- **Supersedes:** —

## Context

Issue #23 asks that every outage in the imported data become an incident with a timeline and a cause hypothesis:
incidents start from outage signatures, primarily OOM kills, overlapping events are merged, the window before and
after a kill is configurable, and the incident collects the events, log templates, system metrics, process and
connection history and boots of its window. The hypothesis names the programs with the strongest memory growth
before the kill, the largest consumers at the time and correlated activities (a running backup, a recent boot,
service restarts). Incidents are stored and re-computed after further imports, which must update them instead of
duplicating them.

What exists: the signature detector recognizes OOM kills, boots, service events and MariaDB and mail events on
stored log lines, merges the copies of one line from `syslog`, `kern.log` and the journal, returns events with their
record IDs and stores nothing; nothing calls it yet, and 0094 leaves to this issue how its caller pages through lines
that share a capture time ([0094](0094-log-signatures-embedded-yaml-linear-patterns-on-stored-lines.md)). The legacy
monitoring log gives metrics and process and connection snapshots every five minutes
([0093](0093-legacy-monitoring-log-snapshots-by-header-zone-and-whole-snapshot-validity.md)); the template statistics
answer which templates are new or unusually frequent
([0095](0095-log-templates-token-rules-statistics-in-the-write-path-usual-level-from-covered-hours.md)). Detection and
incident reconstruction are deterministic ([0008](0008-deterministic-detection-and-alerting.md)); missing data is
unknown, never normal ([0028](0028-data-gaps-are-always-recorded.md)). Incidents are kept without limit while log lines
are kept 90 days, so an incident must outlive the lines it was built from. Any local process can write a line that
reads as an OOM kill, so the number of kills and the size of a window are not bounded by anything trustworthy.

## Options considered

When the reconstruction runs:

1. **A sub-command `vandoxd incidents` that rebuilds from everything stored** (chosen) — one explicit step after
   imports, its cost visible, the import's output, interruption and exit codes untouched; the operator has to run it.
2. **At the end of every `vandoxd import`** — nothing to remember; but an import of several directories in a row
   repeats a full scan each time, and an import that stored records would fail or succeed on the analysis as well.
   Calling the same reconstruction from the import later is a small change.
3. **Inside the service, continuously** — the service does not read the store yet; continuous detection on live and
   backfilled data is #53.

How a re-run treats stored incidents:

4. **Rebuild from all stored lines, match each rebuilt incident to the stored ones whose window overlaps it** (chosen)
   — the oldest overlapped incident keeps its number and gets the new content (untouched when nothing changed), the
   other overlapped ones are merged into it, a rebuilt incident without overlap is added, and a stored incident that
   no rebuilt one overlaps is kept, because its lines may have expired. Imports never delete records, so a window
   only grows or merges.
5. **Delete all incidents and insert them anew** — simplest; but incident numbers change on every run (the notes of
   #27 would lose their incident), and a run after the 90-day log retention would delete incidents whose lines expired.
6. **Process only lines stored since the last run (a cursor on the record ID)** — less work; but an older log imported
   later lands anywhere in time and changes the neighbors of existing incidents, so the cursor would have to reopen
   windows and keep state of its own.

When kills belong to one incident:

7. **Their windows (before to after) overlap** (chosen) — two incidents never show the same stretch of time, and
   the chain the issue describes (reboot, catch-up backup, kill eight minutes later) is one incident with its prelude.
8. **A kill falls into the after-window of the previous one** — shorter incidents; but two incidents whose windows
   overlap would both list the lines between them.

How the hypothesis is formed:

9. **Structured, from fixed rules** (chosen) — growth is the change of each program's summed RSS between the first and
   the last process snapshot in the *before* period of the first kill after its last boot, the top three that grew;
   the largest consumers are the top three of the last snapshot; activities are a backup program in a snapshot, the
   last boot and the number of service events before the kill; each is explainable from the stored records.
10. **A slope over all snapshots, or the maximum minus the minimum** — robust against one odd snapshot; but needs every
    snapshot's programs in memory and reacts to sampling gaps the legacy script has under memory pressure.
11. **A written text, by rules or by the optional AI** — excluded by 0008 for anything but the nightly report.

How a backup is recognized:

12. **A built-in list of backup program names, `pzstd` only** (chosen) — the one name the analysis of the server so far
    confirms (issue #23); a name a program does not match is not a backup.
13. **A configurable list** — adjustable without a release; but the backend's strict configuration decoder has no
    lists, and nobody knows the other names yet. The agent's backup detection with configurable patterns (#36) and
    real data will settle it.
14. **Plesk's backup log lines** — no line format of them is verified.

How memory and atomicity are held against a flood of forged kills:

15. **Stream: one open incident at a time, each saved in its own transaction, caps on what an incident keeps**
    (chosen) — memory does not grow with the number of lines, kills or incidents; an interrupted run leaves the
    incidents it saved and the rest as before, and the next run completes it.
16. **Collect all incidents, save them in one transaction** — all or nothing; but memory grows with the number of
    incidents, which forged lines control.

How the reader pages through lines that share a capture time:

17. **A keyset on the record ID in the record query** (chosen) — the page after a record is everything that comes after
    it in the order capture time, then ID, compared on the stored values; one optional query field.
18. **Offset paging** — each page rescans all earlier rows (quadratic on a million lines).
19. **A streaming reader that keeps one statement open for the whole pass** — no paging; but holds a read transaction
    for minutes, which stops WAL checkpoints for that time, and needs a new reader interface.

Where the growth baseline starts when the *before* period holds a reboot (the 2 Oct chain: reboot 06:51, kill 06:59,
both inside the default hour):

20. **After the last boot** (chosen) — snapshots at or before it show processes the reboot ended; the growth after it
    (services starting, a catch-up backup) is what explains such a kill; with fewer than two snapshots after the boot,
    growth is unknown.
21. **At the first snapshot of the period, or growth unknown whenever a boot lies in it** — the first makes every
    service started after the boot "grow from nothing"; the second gives up exactly on the chain the issue describes.

How an incident stays within the 8 MiB the store accepts, when forged text is escaped to six bytes per character:

22. **A serialized byte budget for the timeline (4 MiB), measured with the options the store serializes with**
    (chosen) — every other part is bounded by counts at the worst escape; normal incidents keep 1,000 entries, a forged
    one keeps fewer and counts the rest. An incident the store still refuses is skipped and counted; one bad window does
    not stop the run.
23. **Count caps sized for six-fold escaping** — about 300 timeline entries for every incident, also the normal ones.
24. **Another encoder** — control and supplementary characters still expand, so it gives no bound by itself, and it
    would differ from the payload encoder.

How much of the stored snapshots is held at once:

25. **One snapshot per page, the payload read as UTF-8 bytes** (chosen) — one forged maximum process snapshot (about
    100 MiB of stored JSON) peaks at about 240 MiB instead of about 440 MiB.
26. **A query that reads only the programs of a snapshot** — smaller still; but a new query form for one caller, and
    snapshots without programs need their processes anyway.

Whether a rebuilt incident may replace a richer stored one (after retention, #46): no guard here; refusing a lower kill
count would also freeze legitimate corrections and misses a shorter timeline with the same kill count, and retention
deletes nothing yet.

## Decision

Options 1, 4, 7, 9, 12, 15, 17, 20, 22 and 25: `vandoxd incidents` runs the signature detector over every stored log line in
capture order, merges OOM kills whose configured windows overlap into incidents, builds each incident (timeline,
structured hypothesis, metrics, connections, templates) from the stored records of its window, and saves it in its
own transaction, matched to the stored incidents by overlapping windows so that a re-run updates instead of
duplicating. The rules, defaults and limits are in [Detection, alerts and reports](../areas/detection-and-reports.md)
(*Incidents*), the table in [Storage](../areas/storage.md).

## Consequences

- An incident's number is stable across re-runs as long as its window keeps overlapping; merging two incidents keeps
  the older number. Anything attached to an incident later (notes, #27) has to follow a merge.
- The reconstruction reads every stored log line on each run; its time grows with the stored lines, its memory does
  not. The template spike query it calls is not bounded by its range (0095); the sub-command runs off any request
  path.
- A stored incident without a rebuilt match is kept unchanged. Retention (#46) must keep re-runs from rebuilding an
  incident from a window whose lines have partly expired: either keep the data of incident windows or stop
  rebuilding incidents older than the log retention. Incidents are kept without limit, so forged kills spaced apart
  leave incidents (up to 8 MiB each) that outlive their lines; #46 has to bound that, too.
- Reading one forged maximum process snapshot (about 100 MiB of JSON) takes about 240 MiB at its peak, in a container
  of 512 MiB that the service shares. Since #212 the store refuses a payload above its JSON limit
  ([0063](0063-storage-schema-records-table-typed-metric-and-log-tables-json-payloads.md)), so the import skips such a
  snapshot and only a database written by an earlier build can hold one.
- `IncidentsAsync` returns every incident of a page decoded at once, up to the query limit of 10,000, each from up to
  8 MiB of JSON; it has no product caller yet. Its first caller, the incidents view (#27), pages with a small limit
  instead of relying on the store (#213).
- A reboot without a stored boot line is not seen; snapshots before it then count as the growth baseline.
- Backups are recognized only by the program name `pzstd`; another backup program shows as growth or consumer, not as
  a backup. To revisit with #36 or with real snapshots.
- The program names of the legacy log's old and new format differ for one process; names that start with `/` are
  reduced to their last element, which joins the common cases, not all.
- Kills, boots and gaps recorded by the agent (`kernel_event`, `gap`) are not read yet; live incidents (#53) add them.
- An incident built from forged lines is as forged as its lines; incidents list their record IDs so a reader can see
  where each statement comes from.
