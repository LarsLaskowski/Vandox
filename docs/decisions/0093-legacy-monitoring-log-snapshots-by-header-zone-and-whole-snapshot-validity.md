# 0093: The legacy top/lsof monitoring log is read snapshot by snapshot, timed by the zone abbreviation of its header within import.time_zone, and a snapshot whose top output is broken is skipped whole

- **Status:** Accepted
- **Date:** 2026-10-11
- **Area:** Log import
- **Source:** Issue #20
- **Supersedes:** —

## Context

Before the agent exists, a shell script on the monitored server appends a snapshot to a log file every minute (every
five minutes at first): a header line with the output of `date` (`===== Fri Oct  2 09:15:01 CEST 2026 =====`), the
whole output of `top -b -n 1`, and in its newer form sections with `/proc/meminfo`, the pressure files, four
`/proc/vmstat` counters and `lsof -ni`. It is the only per-process memory history before the outages the release
v0.1.0 explains ([0020](0020-analysis-before-alerting-forensics-release.md)), and it is imported as history, not
collected again in this form ([0019](0019-agent-reads-proc-instead-of-top-lsof.md),
[0014](0014-log-import-is-a-core-component.md)). Its records are process and connection snapshots and metrics of the
shared model, which the agent's collectors (#31, #32, #33) will produce later; the model has no metric names yet.

What shapes the reading: `date` writes the local time with a zone abbreviation (`CEST`, `CET`), which tells the two
passes of the repeated hour at the end of daylight saving time apart; every other parser with local times reads them
in `import.time_zone` ([0085](0085-syslog-time-zone-from-import-time-zone-with-embedded-tzdb.md)). A snapshot is many
lines written by several programs in a row; one cut by a crash of the script or mixed with the next run's output is
possible, and a record emitted early cannot be taken back, because the importer resumes by count
([0069](0069-log-import-idempotent-per-file-content-hash-with-resumable-batches.md)). An import is idempotent per
content, so a content completed with every snapshot skipped cannot be imported again after the operator fixes the
cause.

## Options considered

The time of a header:

1. **The local time in `import.time_zone`, the offset chosen by the header's abbreviation** (chosen) — exact across
   both daylight saving changes, whatever the interval between snapshots; the same option as every other local-time
   parser. The abbreviation must be a name of the zone's interval at that local time (NodaTime's TZDB names them
   `CEST` and `CET` for `Europe/Berlin`, checked).
2. **The local time with the repeated-hour rule of `SyslogClock.ResolveLocal`, the abbreviation ignored** — no new
   rule, but a heuristic where the header states the answer, and wrong when the script did not run across the
   change.
3. **A fixed table of abbreviations to offsets, no option** — works without `import.time_zone`, but abbreviations are
   ambiguous worldwide (`CST`, `IST`) and the table would be a second, partial zone database.

A header whose abbreviation is not a name of the zone at that time:

4. **Always fail the file** — a wrong option is noticed at once, but one odd header (the server's zone changed in the
   middle of the file) makes the file impossible to import.
5. **Always skip the snapshot** — robust, but with a wrong option every snapshot is skipped, the content completes
   with no record and can never be imported again.
6. **Fail the file when no header of this parse has matched yet, else skip the snapshot** (chosen) — a wrong option
   fails the file before anything is stored, and a later odd header costs only its snapshot.

A snapshot that is not well-formed:

7. **Skip the whole snapshot when the structure of its `top` part is broken (a summary line missing or out of order,
   no table header, a summary line or table header among the rows); leave out a single unreadable or duplicate row and
   mark the process snapshot incomplete; read every later section on its own and leave out single unreadable socket
   and section lines** (chosen) — records are emitted only for a snapshot whose summary and table were read in order,
   so a cut or mixed snapshot never yields a partial process snapshot that looks complete, while one wrong guess about
   a row of a format checked without the real sample costs a row, not every snapshot.
8. **Skip the whole snapshot on any bad row as well** — strictest, but the row format is unverified against the
   sample: one wrong assumption would skip every snapshot of the file, and an import cannot be repeated once its
   content completed.
9. **Read every line that parses, wherever it is** — keeps more data, but rows of one run would land in another run's
   snapshot under the wrong time.

A snapshot ended by the end of the input rather than by the next header (the log is copied while it grows):

10. **Emit it, with `complete` false on the list that was still open** (chosen) — the log has no end marker, so a list
    open at the end may be short; what was read is kept.
11. **Emit nothing until a following header is read** — never claims too much, but the last snapshot of every file
    would be lost for good, because a continued import starts at the line in which the earlier content ends, not at
    its last header.

Further choices:

12. **Connections**: the snapshot also carries the full `connections` list (chosen) rather than the counts the issue
    names alone, so `complete` keeps its meaning (true only when nothing was left out) and the forensics see which
    remote address talked to which process; its size is that of the `lsof` section it comes from.
13. **Port names** (`lsof -ni` without `-P` prints service names): a built-in table of the names of Ubuntu's
    `/etc/services` (chosen) rather than a short hand-picked list, which would drop every socket on a port it forgot,
    or a lookup on the backend host, which the wire format forbids.
14. **Truncated command names** (`sw-engi+`): the `+` is removed and the process flagged as truncated (chosen); a
    table of full names would be guessed, and learning names from other snapshots would make the result depend on
    where a continued parse starts.
15. **Metric names** are fixed here for the import and named in the wire format area for the agent to reuse (chosen),
    rather than left to each producer.

## Decision

Options 1, 6, 7, 10, 12, 13, 14 and 15: a parser `legacy-monitor`, claimed by the header grammar of its first line, reads
the log snapshot by snapshot, times each snapshot by its header in `import.time_zone` with the offset the abbreviation
names, and emits a snapshot's records only after its `top` part was read in order; a snapshot that ends with the
input is emitted with its open list marked incomplete. The rules are in
[Log import](../areas/log-import.md) (*Legacy monitoring log*) and the metric names in
[Wire format](../areas/wire-format.md) (*Metric names*).

## Consequences

- Imported process snapshots and metrics use the names the agent's collectors must reuse; a collector that needs
  another name changes this record and the wire format area.
- A snapshot cut before its table header, or with another run's summary lines in its `top` part, is lost as a whole
  and reported as skipped; a bad row costs that row and the process snapshot's `complete`; a snapshot cut later keeps
  what was read, with `complete` false where a list may be short. The last snapshot of a file is always marked so,
  and the rest of a snapshot cut by a copy boundary is skipped in the continued import.
- The rate of a vmstat counter needs the previous snapshot of the same parse, so the first snapshot of every parse,
  each continuation included, has none; the raw counter is complete.
- An old-format command (`comm`, cut by `top`) and a new-format command (taken from the command line) of the same
  program can differ (`sw-engi` and `sw-engine-fpm`); program sums are per name as written.
- A snapshot of the new format cut before its first section is read as the old format.
- Revisit when the agent's collectors define the same values differently, or when a sample shows a header, section or
  `lsof` shape the rules do not read.
