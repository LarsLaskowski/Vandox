# Forensics report

## Scope

The **forensics report** summarizes the whole stored period: every outage incident with its victims and hypothesis, whether outages
recur at a time of day or follow a backup, a boot or use of the Plesk panel, which programs use the most memory permanently and
transiently, and which new listeners turned up, across an agent start or found by the agent ([Listener changes](listener-changes.md)).
Operators read it to reconstruct outages; the web UI's report page shows it.

## Running it

- The report is computed on demand from the stored log lines, process snapshots, incidents and listener comparisons, stores nothing, and
  is written as Markdown to standard output by `vandoxd report` ([Backend host](backend-host.md), *Command line and exit codes*). It lists
  the incidents as they are stored, so `vandoxd incidents` is run before it.
- The web UI shows the same report on the page `/report` and offers it as a Markdown download ([Report page](web-ui-report-page.md)):
  both compose it from the same blocks as the command, so the texts, numbers and times are the same; the page lists the outages 50 at a
  time, the command and the download list all of them.
- **All times are UTC**, in the report and in its rules; `import.time_zone` only decides how an import reads local times.

## What it reads

- **Every stored log line** that the retention periods still count, in capture order in pages of 1,000 (by capture time, then record ID, as [Incidents](incidents.md),
  *Re-running*): the first and last capture time of all lines, the observed hours (below), and the **boots**: the events of the signature
  `boot` (copies merged as in [Log signatures](log-signatures.md), *Events and merging*).
- **Every stored process snapshot** that they still count, one at a time in capture order: the series for the activities and the memory consumers. Its
  programs are the *programs of a snapshot* of [Incidents](incidents.md), *Content* (names that start with `/` reduced to their last part).
- **Retention.** A line or snapshot of origin `agent` or `backend` captured before the current time minus `retention.logs` (lines) or
  `retention.snapshots` (snapshots) is skipped, also where a kept window of an OOM kill ([Retention and rollups](retention.md)) still
  holds it: it counts in no total, first or last time, observed hour, boot or snapshot series. Imported lines and snapshots count
  however old they are. With `unlimited` nothing is skipped.
- **Every stored incident**, one at a time in the order of its window, **twice**: the first pass feeds the rules, the second lists
  each outage. Stored incidents never overlap, so each page starts at the end of the previous incident's window; a page that does not
  lead past it is an error (`store: reading incidents: page did not advance`). When the second pass lists a different number of
  incidents than the first (a `vandoxd incidents` run in between), the notes say so and ask for a new run.
- **The stored listener comparisons**, after the first pass over the incidents ([Listener changes](listener-changes.md), *What the report
  reads*).

## Observed hours

An **observed hour** is a UTC hour (it starts at a full hour of Unix time, also before 1970) in which at least one log line of the
source `journal` or `syslog` is stored: the system log, which holds the kernel's kill and boot lines. An hour without such a line is
**unknown**: it takes no part in any base rate, and an outage cannot be judged on it. Hours in which only other sources (web server,
mail, MariaDB's own error log) logged are not observed. A line that retention removed, or that the report skips for being past
`retention.logs`, makes no hour observed, so an agent hour older than the log period is unknown even where a kept window still holds
lines; imported hours count as before.

## Activities before a moment

The **lookback** of a moment `t` is `[t − lookback, t)`, where the lookback is `incidents.window_before` (default `1h`, [Configuration
options](configuration-options.md)). For a moment the report judges three activities, each **yes**, **no** or **unknown**:

| Activity | Unknown when | Yes when |
| -------- | ------------ | -------- |
| Backup running | no process snapshot lies in the lookback | a snapshot in the lookback holds a backup program (`pzstd`, as in [Incidents](incidents.md)) |
| Boot | an hour that overlaps the lookback is not observed | a boot lies in the lookback (the report gives the time of the last one) |
| Panel memory high | no process snapshot lies in the lookback, or there is no panel threshold | a snapshot in the lookback has panel memory at or above the threshold |

Otherwise the activity is **no**. The **panel memory** of a snapshot is the summed RSS of its programs whose name starts with
`sw-engi` (ordinal, case-sensitive): `sw-engi` as `top` cuts it in the old legacy format, `sw-engine`, `sw-engine-fpm`. The **panel
threshold** is the 90th percentile (nearest rank: the value at position ⌈0.9 · n⌉ of the n snapshots' panel memory in ascending order,
a snapshot without a panel program counting 0); there is none when it is 0.

An **outage's activities** are those of the moment of its first kill. They are the report's own: judged over the whole lookback, not
cut at the last boot like the incident's stored hypothesis, so an outage whose backup ran before a reboot in its lookback reads "yes"
here while its stored `backup_running` is false.

## Rules

Each rule compares the **share of outages** that have a property with the **base rate**, the share of observed hours that have it:

- **Time of day**, for each of the 24 hours of the day (UTC): the outages whose first kill lies in that hour of the day, of all
  outages, against the observed hours that are that hour of the day, of all observed hours. Every outage counts as known.
- **An outage in an unobserved hour before the log period.** An outage whose first kill lies before the start of the log period (now
  minus `retention.logs`) in an hour that is not observed is counted and listed, in the totals of the head, but takes part in no rule: it
  counts in no hour of the day and in no activity rule. Outages after that start, and outages in observed hours, are judged as before.
- **Backup running**, **Boot**, **Panel memory high**: the outages whose activity is yes, of those where it is known, against the
  observed hours whose **end** (the moment the next hour starts) has the activity yes, of those where it is known. With the default
  lookback of one hour, an hour's activity is the activity within that hour.

A rule's result is one of three:

- **Unknown** — in this order: a limit of the report was hit for its data (*Limits and errors*); for panel memory there is no panel
  threshold; fewer than **3** outages are known; no observed hour is known. Unknown is never shown as "not detected", and the reason is
  given.
- **Detected** — at least **2** known outages have the property, their share is above the base rate, and the probability of at least
  that many among the known outages when each has the property with the base rate (the binomial tail) is at most **1 %**; for the time
  of day at most **1 % / 24** per hour, because 24 hours are tested. A base rate of 0 makes the probability 0, a base rate of 1 makes
  a higher share impossible.
- **Not detected** — otherwise.

## Memory consumers

Per program, over all stored process snapshots: the number of snapshots that hold it, its mean RSS over those snapshots (the sum
divided by their number, rounded down), and its peak RSS with the capture time of the first snapshot that reached it. RSS is the
summed RSS of the program's processes, which counts shared memory more than once.

- **Permanent consumers**: programs in at least **90 %** of all process snapshots, by mean RSS (highest first), then name; the first
  **5**.
- **Transient consumers**: all other programs, by peak RSS (highest first), then name; the first **5**.

At most **4,096** programs are tracked: a program first seen after that is not, and the notes give the number of its snapshot entries.
Without a process snapshot both lists say unknown.

## Content and format

The report has these sections, in this order. Headings and fixed words:

| Part | English | German |
| ---- | ------- | ------ |
| title | `# Vandox forensics report` | `# Vandox-Forensikbericht` |
| summary | `## Summary` | `## Übersicht` |
| patterns | `## Recurring patterns` | `## Wiederkehrende Muster` |
| time of day | `### Time of day (UTC)` | `### Tageszeit (UTC)` |
| activities | `### Activities before the outages` | `### Aktivitäten vor den Ausfällen` |
| memory | `## Memory consumers` | `## Speicherverbraucher` |
| permanent | `### Permanent consumers` | `### Dauerhafte Verbraucher` |
| transient | `### Transient consumers` | `### Vorübergehende Verbraucher` |
| new listeners | `## New listeners` | `## Neue Listener` |
| outages | `## Outages` | `## Ausfälle` |
| one outage | `### Outage #<number> at <time>` | `### Ausfall #<number> am <time>` |
| notes | `## Notes` | `## Hinweise` |
| rule results | `detected`, `not detected`, `unknown` | `erkannt`, `nicht erkannt`, `unbekannt` |
| activities | `yes`, `no`, `unknown` | `ja`, `nein`, `unbekannt` |

- **Head** (a list below the title): when the report was made; the number of log lines with the first and last capture time; the
  number of observed hours; the number of process snapshots with the first and last capture time; the lookback in minutes; that all
  times are UTC.
- **Summary**: the number of outages and of OOM kills, the first kill of the first and of the last outage; one line per detected
  pattern (the hour or the activity, the outages with it of the known ones, both shares); a line naming the rules whose result is
  unknown. "No recurring pattern detected" is written only when every rule was decided, otherwise "No recurring pattern detected among
  the decided rules". (German: "Kein wiederkehrendes Muster erkannt" and "Kein wiederkehrendes Muster unter den entschiedenen
  Regeln erkannt".)
- **Recurring patterns**: a table of the hours of the day that have at least one outage (hour as `HH:00–HH+1:00`, outages, share of
  outages, share of observed hours, result), or the result unknown with its reason for the whole table; a table of the three
  activities (outages with it, outages known, share of outages, share of observed hours, result and, when unknown, the reason); the
  panel threshold in MiB.
- **Memory consumers**: one list item per consumer: the program, its share of the snapshots, mean and peak RSS (permanent), peak RSS
  and its time (transient).
- **New listeners** (after the memory consumers, before the outages): the head per agent, the findings and their closing sentence, as
  [Listener changes](listener-changes.md), *Report section*, states them.
- **Outages**: one section per stored incident, in the order of its window, with its number and first kill: the window; the kill
  count and the **victims** (the distinct victim commands of the kept kills in first-seen order with their count; a kill without a
  victim counts as unknown; the number of kills not kept, if any); the **hypothesis**: the growth candidates with their growth
  (unknown when the incident's growth is not known, "none" when known and empty), the largest consumers with their RSS (unknown
  when the incident had no snapshot), the three activities of the outage (the boot with its time), and the number of service events
  in the lookback as stored.
- **Notes**: the rules in one sentence each, the limits that were hit, the number of program entries not tracked, the caps of the new
  listeners that were hit ([Listener changes](listener-changes.md), *Report section*) and a different number of incidents between the
  two passes.

Formats, the same in both languages except the decimal separator:

- Times: `YYYY-MM-DD HH:MM UTC` (24-hour clock, cut to the minute).
- Memory: MiB (1,048,576 bytes), rounded to a whole number, a half away from zero; growth with a leading `+`.
- Shares: percent with one decimal, a half away from zero (`57.1 %` in English, `57,1 %` in German).
- Counts and numbers: ASCII digits, no thousands separator.
- Lines end with a line feed; the text is UTF-8.

## Language

`en` (English, the default) or `de` (German), chosen by `vandoxd report -lang` ([Backend host](backend-host.md), *Command line and exit
codes*; the report page takes it from its address, [Report page](web-ui-report-page.md)). The language changes only the labels and the
decimal separator, never the rules, the numbers or the external text.

## External text in the report

Victim commands and program names (also of backup and panel programs), and the owner commands, socket user names and agent IDs of the
new listeners section ([Listener changes](listener-changes.md), *External text*), are external text (security area 12 of
`.squad/project.md`). The report writes them **only as Markdown code spans**, so no character of them is read as Markdown, HTML, an
entity, a link or an autolink, and never in a heading or a table cell:

- a backslash becomes two backslashes; a line feed, carriage return and tab become `\n`, `\r`, `\t`; every other character of the
  categories control (Cc), format (Cf, such as U+202E), line and paragraph separator, private use and unassigned, and U+FFFD, becomes
  `\u` and four lower-case hex digits (`\U` and eight above U+FFFF) — the escapes of the terminal output of `vandoxd import` and
  `vandoxd incidents`, without the quotes;
- the code span's fence is one backtick longer than the longest run of backticks in the escaped text, and the text is padded with
  one space on each side (which Markdown removes again); a text of spaces only has its spaces written as `\u0020`;
- an empty text is written as `—`, not as a code span.

A command that holds Markdown, HTML, a backtick run or U+202E is therefore literal text on the page and in the download. The report's own
words, numbers and times are not external. Backslash escapes do not apply inside a code span, so a literal backslash sequence in a name
(the six characters `\u202e`) is rendered with its backslash doubled (`\\u202e`) and stays distinguishable from the escape of a real
U+202E (`\u202e`).

## Limits and errors

- **Memory** does not grow with the number of lines, snapshots or incidents beyond fixed caps: one page of up to 1,000 log lines and
  one signature detector (as [Incidents](incidents.md), *Limits and errors*), one process snapshot at a time (the largest single read is
  the same as there), one incident at a time (up to 8 MiB of JSON), and kept across the run: at most **1,000,000 snapshot points** (time,
  panel memory, count of snapshots with a backup program: 24 bytes each, so about 24 MB kept and about 36 MB while the list grows by
  doubling, plus a sorted copy of the panel memory of 8 MB for the threshold and, while the rules are evaluated, 4 MB of counts),
  **10,000 boots** (80 KB), **100,000 runs** of consecutive observed hours (about 1.6 MB), **4,096 programs** (their names, short
  texts of at most 1,024 bytes, about 8 MiB at most, plus about 80 bytes of counters each), and 24 hour counts. The caps count decoded,
  stored items, never bytes. The incidents add only counters. With all caps full the report keeps about 60 MB across the run, plus the
  at most about 20 MB of the new listeners section ([Listener changes](listener-changes.md), *Limits and errors*), plus the one log page,
  process snapshot or incident it is reading.
- **Over a cap** the statements that need the data are unknown: more snapshots make backup and panel unknown, more boots the boot,
  more runs of observed hours backup, boot and panel (their base rate). The time of day counts every observed hour and has no cap.
  The notes name the cap that was hit. An outage's own activity follows the same rule: past a cap of the data it needs, a yes stays
  a yes and a no becomes unknown.
- **Time** grows with all stored lines, snapshots and incidents (the incidents are read twice, the base rate evaluates every observed
  hour). The command runs off any request path.
- A run reads every stored log line (one query per page of 1,000 lines) and every stored process snapshot (one query per snapshot), so
  on a large archive it can take minutes. Measured on the development host (4 vCPU Intel Xeon 2.10 GHz, .NET 10) over a database of
  5,000,000 log lines (1.6 GB), 43,200 process snapshots and 64 incidents: 56 to 60 seconds in a Release build (two runs through the
  readers of the web UI) and 67 seconds for `vandoxd report` from a Debug build. The parts that cannot be interrupted are short: after the
  last record the analysis seals its series and then summarizes, 14 ms and 13 ms there and, at the caps (1,000,000 snapshots over
  1,000,000 observed hours, fed directly), 0.5 s and 15 ms. The reference host writes about four times slower than the development host
  ([BENCHMARKS.md](../BENCHMARKS.md)), so the same database may take several minutes there; the web UI stops a run after 2 minutes,
  `vandoxd report` has no limit.
- **Forged data shifts the result**: forged kills make forged outages ([Incidents](incidents.md), *Limits and errors*), and forged boot
  lines, `pzstd` or `sw-engi*` processes shift the activities and their rates; forged process names can take the 4,096 program places
  first.
- A cluster of outages that straddles an hour boundary is split between two hours of the day.
- An activity of an outage near the start of the snapshots, or under memory pressure when the legacy log missed its samples, is
  unknown rather than no.
- The incidents are those `vandoxd incidents` last stored. The report cannot tell that log lines were imported after that run
  (the incidents carry the time they were saved, the lines the time they were logged), so `vandoxd incidents` is run again after every
  import.
- Standard output is UTF-8 without a byte order mark whatever the locale is.
- On exit 1 (interrupted, store failure) standard output holds an incomplete report, which is to be discarded.

## Related decisions

- [0100](../decisions/0100-forensics-report-as-markdown-from-vandoxd-report.md) — why the forensics report is Markdown from a
  sub-command, computed on demand in bounded passes, with external text in code spans, and why the web page comes separately.
- [0108](../decisions/0108-report-page-one-block-model-outage-pages-by-rerun-language-in-the-address.md) — why the Markdown writer and the
  web page share one block model, and how the page lists the outages and limits a run.
- [0101](../decisions/0101-forensics-rules-share-of-outages-against-observed-hours-binomial-tail.md) — why the report's rules compare
  outages with observed system-log hours, judge activities over the lookback, decide by a binomial tail, and how panel use and
  permanent and transient consumers are defined.
- [0210](../decisions/0210-data-in-the-windows-of-the-agents-kernel-oom-kills-is-kept-as-long-as-their-incident.md) — why kept windows do not weigh the base rates.
- [0102](../decisions/0102-report-language-from-a-flag-english-default-text-tables-in-code.md) — why the report's language is a
  flag with English as default and its texts are tables in code.

## Not here

- The new listeners section, its comparisons, texts, caps and limits: [Listener changes](listener-changes.md).
- The outages themselves and how they are rebuilt: [Incidents](incidents.md); kills and boots: [Log signatures](log-signatures.md).
- The command line, flags and exit codes of `vandoxd report`: [Backend host](backend-host.md).
- The report page, its outage pages, its address and the download: [Report page](web-ui-report-page.md).
- `incidents.window_before`: [Configuration options](configuration-options.md).

## Implementation

`Vandox.Core.Reports` (`ForensicsAnalysis` with `Seal` and `Summarize`, `ObservedHours`, `SnapshotSeries`, `ConsumerTracker`,
`RecurrenceRules`, `PanelPrograms`, `ReportLimits`, `ProgramStatistics` and the result types) for the analysis; `Vandox.Backend`
(`Reports/ForensicsReporter`, `Reports/IReportWriter`, `Reports/ReportComposer` with the blocks `ReportBlock`, `ReportBlockKind` and
`ReportPiece`, `Reports/MarkdownReportWriter`, `Reports/MarkdownText`, `Reports/ReportText`, `Cli/ReportCommand`, and `Program`, which
opens standard output as UTF-8 without a byte order mark) for reading the store and writing the Markdown; the web UI adds
`Reports/ReportViewBuilder` and `ReportDocument` (the blocks of one page of outages), `ReportService` (one run at a time) and
`GatedIncidentStore`, described in [Report page](web-ui-report-page.md).
