# 0093: The legacy top/lsof monitoring log is read snapshot by snapshot, timed by the zone abbreviation of its header within import.time_zone, and a snapshot whose top output is broken is skipped whole

- **Status:** Proposed
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

7. **Skip the whole snapshot when its `top` part is incomplete or out of order; read every later section on its own;
   leave out single unreadable socket and section lines** (chosen) — records are emitted only for a snapshot whose
   process table and summary were read in full order, so a cut or mixed snapshot never yields a partial process
   snapshot that looks complete.
8. **Read every line that parses, wherever it is** — keeps more data, but rows of one run would land in another run's
   snapshot under the wrong time.

Further choices:

9. **Connections**: the snapshot also carries the full `connections` list (chosen) rather than the counts the issue
   names alone, so `complete` keeps its meaning (true only when nothing was left out) and the forensics see which
   remote address talked to which process; its size is that of the `lsof` section it comes from.
10. **Port names** (`lsof -ni` without `-P` prints service names): a built-in table of the names of Ubuntu's
    `/etc/services` (chosen) rather than a short hand-picked list, which would drop every socket on a port it forgot,
    or a lookup on the backend host, which the wire format forbids.
11. **Truncated command names** (`sw-engi+`): the `+` is removed and the process flagged as truncated (chosen); a
    table of full names would be guessed, and learning names from other snapshots would make the result depend on
    where a continued parse starts.
12. **Metric names** are fixed here for the import and named in the wire format area for the agent to reuse (chosen),
    rather than left to each producer.

## Decision

Options 1, 6, 7, 9, 10, 11 and 12: a parser `legacy-monitor`, claimed by the header grammar of its first line, reads
the log snapshot by snapshot, times each snapshot by its header in `import.time_zone` with the offset the abbreviation
names, and emits a snapshot's records only after its `top` part was read completely and in order. The rules are in
[Log import](../areas/log-import.md) (*Legacy monitoring log*) and the metric names in
[Wire format](../areas/wire-format.md) (*Metric names*).

## Consequences

- Imported process snapshots and metrics use the names the agent's collectors must reuse; a collector that needs
  another name changes this record and the wire format area.
- A snapshot cut inside its `top` part, or mixed with another run's lines there, is lost as a whole and reported as
  skipped; one cut in its sections keeps what was read, with `complete` false where a list may be short.
- An old-format command (`comm`, cut by `top`) and a new-format command (taken from the command line) of the same
  program can differ (`sw-engi` and `sw-engine-fpm`); program sums are per name as written.
- A snapshot of the new format cut before its first section is read as the old format.
- Revisit when the agent's collectors define the same values differently, or when a sample shows a header, section or
  `lsof` shape the rules do not read.
