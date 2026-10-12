# Detection, alerts and reports

## Scope

How the backend turns stored records into findings: that detection is deterministic, which data may raise an alert, when the nightly
report is sent, and what the first release does and does not do. The log signatures and their detector are implemented (*Log
signatures*), the log templates with their statistics and queries (*Log templates*), and the reconstruction of outage incidents from
the stored data (*Incidents*); the rules and thresholds are **not implemented yet**, and this document fixes the principles they are
held to. How records get stored is in the storage area; how gaps are recorded is in the agent area.

## Principles

- **Detection, incident reconstruction and alerting are deterministic**: rules, thresholds and log signatures. Every alert can be
  explained from rules and values and is covered by unit tests. A new failure pattern needs a new rule or signature.
- **AI is optional and only writes the nightly report.** It never decides whether an alert fires. Vandox works fully without any AI
  service.
- **Analysis comes before alerting.** The first release (v0.1.0) is the forensics release: collection, log import, spool and backfill,
  storage and the historical views needed to reconstruct outages. Alerting, the nightly report and remote actions build on it in later
  releases, with thresholds based on what the analysis showed. The data model must serve later rules without migration surprises.

## Live data and backfill

- The backend classifies **every record as live or backfilled** from its capture time (corrected by the batch's clock offset), its
  receive time and gaps in the sequence numbers. It does not trust a flag from the agent; the batch header's live/backfill mode is only
  a hint.
- **Alert rules are evaluated on live data only.** Backfilled data is stored and analyzed, appears in incidents and in the report, and
  never alerts, so there is no alert storm after the backend host was off.
- Imported data (origin `import`) is historical and never alerts.
- The classification depends on the agent's clock being reasonably correct; the threshold between live and backfilled is a tested
  constant.
- A recorded gap is *unknown*, never *normal*, in analysis, reports and the web UI.

## Log signatures

A **signature** recognizes one known kind of event in stored `log_line` records of every source; the **detector** turns a sequence of
records into **events** that list the records they stand for. The catalog of signatures is data embedded in the backend, not code:
adding a signature is one entry in the catalog plus a positive and a negative test, and needs no change to the detector. Nothing is
stored and no record is changed by the detector; the incident reconstruction (*Incidents*) is the one caller.

### Signatures

A signature is checked against one line (record `source`, line `program`, `event` and `message`). Every given condition must hold:

- **Origin**: the signature names programs, sources, or both; the condition holds when the line's program is listed **or** the record's
  source is listed. A signature that names neither has no origin condition.
- **Event**: the line's `event` is one of the listed events; no list means no condition.
- **Message**: the pattern matches the message (first match only); no pattern means no condition.

Comparisons are ordinal and case-sensitive. A signature needs an event or a message, so none matches every line. Patterns run on the
non-backtracking regular expression engine (time linear in the message length) with the options multiline, culture-invariant and
explicit capture; constructs that engine cannot run (lookahead, lookbehind, atomic groups, backreferences, conditionals) are refused
when the catalog is loaded, as is every other rule break of a catalog (unknown keys, anchors, aliases and tags, a field that is no named
group, a named group that is no field, limits on lengths and counts), with an error that names the catalog and the line but never the
document text. Patterns write `[0-9]`, never the Unicode digit class, and exclude the line feed from character classes. A signature
that has no event condition and reads a message that can hold text of a remote party (web server lines, mail addresses, user names)
carries an origin condition and anchors its pattern with `\A` at text the server itself writes before any external text, never with `^`
(which matches after every line feed of a multi-line message).

A line may match several signatures; it then yields one event each, in catalog order.

| Signature | Recognizes | Conditions | Fields |
| --------- | ---------- | ---------- | ------ |
| `oom_kill` | the kernel's OOM killer killed a process (the kill line, with or without the kernel time stamp, also as the last line of a grouped report) | program `kernel` | `victim_pid`, `victim_command` (text), `anon_rss_bytes` (bytes, converted from the kernel's kB), `oom_score_adj` |
| `mariadb_crash` | MariaDB stopped on a fatal signal | event `mariadb.abort` | `signal` |
| `mariadb_recovery_start` | MariaDB started a crash recovery | event `mariadb.recovery_start` | none |
| `mariadb_recovery_end` | MariaDB finished a crash recovery | event `mariadb.recovery_end` | none |
| `mariadb_too_many_connections` | Postfix or Dovecot could not connect to MariaDB because of *Too many connections* | programs `postfix/proxymap`, `postfix/cleanup`, `postfix/smtpd`, `postfix/trivial-rewrite`, `postfix/local`, `postfix/virtual`, `dovecot`, or source `dovecot`; message anchored with `\A` | none |
| `service_start` | systemd started a unit | program `systemd`; message `Started ...` | `unit` (when named), `description` |
| `service_stop` | systemd stopped a unit | program `systemd`; message `Stopped ...` | `unit` (when named), `description` |
| `service_failed` | a systemd service failed | program `systemd`; message `<unit>.service: Failed with result '<result>'.` | `unit`, `result` |
| `disk_full` | MariaDB could not write because a file system was full | programs `mariadbd`, `mysqld` or source `mariadb`; message anchored with `\A` | none |
| `postfix_fatal` | Postfix logged a fatal error or a panic | events `postfix.fatal`, `postfix.panic` | none |
| `dovecot_fatal` | Dovecot logged a fatal error or a panic | events `dovecot.fatal`, `dovecot.panic` | none |
| `boot` | the kernel started (the first kernel line of a boot) | program `kernel` | `kernel_version` |

`disk_full` reads only MariaDB's own error messages, and `mariadb_too_many_connections` only the MySQL client errors of Postfix and
Dovecot. Lines of other writers (PHP, nginx, Apache, Plesk) are not recognized: their messages carry external text before the error.
On systemd 249 (Ubuntu 22.04) `Started` and `Stopped` name a unit by its description, and the journal reader does not keep the unit
name, so `unit` is present only where systemd prints `<unit> - <description>`.

### Fields

A field is the text of a named group of the pattern, and the group name is the field name. A field has one of three types:

- `text`: the captured text, cut at a character boundary to at most 1,024 UTF-8 bytes.
- `integer`: an optional minus sign and 1 to 19 ASCII digits, in the range of a 64-bit signed integer.
- `kibibytes`: 1 to 19 ASCII digits without a sign that give a number of bytes (the value times 1,024) within a 64-bit signed integer.

A number is accepted only when the **whole** captured text is of that form (an ordinal scan, no parsing API sees other text): a plus
sign, spaces, a line feed or NUL, a thousands separator, a fraction, an exponent, a hexadecimal prefix, a minus sign or a digit of
another script all make the field absent. A field whose group did not take part, matched the empty string, or did not convert is
**absent** (it is in neither `Texts` nor `Numbers`); the event is still detected.

### Events and merging

An event has the signature, the capture time of its first line, the host, its fields, the record IDs of its lines (at most the first
256) and the number of lines. The same event often appears several times in the stored records (a kernel line is written to `syslog`,
`kern.log` and the journal), so lines are merged: a line joins an open event of the **same signature** with **equal fields**
(compared field by field; absent differs from present), a **compatible host** (equal, or either one empty; the event's host is the
first non-empty host of its lines) and a capture time at most **60 seconds** after the event's first line. If several events qualify,
the oldest takes the line. Otherwise the line opens an event.

The detector takes the records in capture order (the order the store returns them: capture time, then ID). A log line earlier than the
previous one is an error; records that hold no log line are ignored. An event is emitted when a later line is more than 60 seconds
after its first line, oldest first, and the rest are emitted in opening order when the input is complete; the events one line opens
are in catalog order.

At most **1,024 events** are open at a time: opening another emits the oldest early. An open event keeps only its converted fields, its
host, at most 256 record IDs, the first capture time and the line count, never the message, the match or the record, so memory is
bounded by those limits and not by the input.

### Limitations

- An event is a **classification, not proof of origin**. Any local process can log as `kernel`, `systemd`, `mariadbd`, `postfix/*` or
  `dovecot` (the journal's `_TRANSPORT` is not kept), and a client of MariaDB can forge a whole MariaDB error log entry through a
  failed login's user name (see *Log import*, *MariaDB error log*), which reaches `disk_full` and the `mariadb_*` signatures. The
  events list their record IDs so a consumer can weigh them.
- `postfix_fatal` and `dovecot_fatal` cover both the fatal and the panic event and have no field, so a fatal error and a panic of one
  daemon within 60 seconds are **one event**; nothing in the event tells which of the two it was, the linked lines and their `event`
  do.
- Merging assumes the capture times of the copies are comparable. A syslog file read with a wrong `import.time_zone` (or across a
  daylight saving change the zone does not describe) lies hours away from its journal copy, so the two are more than 60 seconds apart
  and are **counted as two events**.
- A flood of 1,024 or more distinct matching lines within 60 seconds (which any local process, and for some signatures a client of
  MariaDB, can write) **closes events early**, so the later copy of a real event can become an event of its own.
- The **writers are narrowed**: disk-full and *Too many connections* lines of PHP, nginx, Apache and Plesk are not recognized, because
  those messages carry text of remote HTTP clients (requested paths, referrers, application output).
- Real repeats with equal fields within 60 seconds are one event, whose lines are all linked and counted. On systemd 249 start and stop
  name the description, so scopes and timers match too. The kill line of `oom_kill_allocating_task` is recognized, but its report is
  not ended by the report grouper (issue #202).
- The **text fields are external text**: they may hold control characters (the built-in patterns exclude the line feed from them) and
  showing them falls under security area 12 (*Logging and display of external data*) of `.squad/project.md`.

## Log templates

A **template** is the message of a log line with its variable parts replaced by placeholders, so that lines which differ only in
numbers, IDs, addresses or paths share one template. The backend keeps, per template, when it was first and last seen and how many
lines it had in each hour, and answers two questions from that: which templates are **new** in a time range, and which hours of a
template are **above its usual level**. No command or view shows them yet.

### Normalization

The template of a message is built in one pass over the message, the same way for every source; the same message always gives the
same template, whatever was seen before.

1. **Separators:** every white-space character and every other control character (Unicode category Cc) is a separator. A run of
   separators becomes one ASCII space; separators at the start and the end are dropped. A template therefore holds no line feed and no
   control character of category Cc. Format characters (category Cf, such as U+202E and the other bidirectional controls, or U+200B)
   are no separators and stay inside their token.
2. **Delimiters:** the ASCII characters `"` `'` `(` `)` `[` `]` `{` `}` `<` `>` `,` `;` `=` `#` `!` `?` `*` `&` `|` `^` `$` `\` and
   the backtick are kept as they are and end a token.
3. **Tokens:** a token is a run of characters that are neither separators nor delimiters. Trailing `.` and `:` characters are split off
   a token and kept as they are after its replacement; a token that is empty then stays empty.
4. **Rules:** each token is replaced by the first rule that applies; a token no rule applies to is kept as it is. "Digit" means an
   ASCII digit `0`–`9` throughout (a digit of another script is a letter here), "hex digit" `0`–`9`, `a`–`f`, `A`–`F`.

| # | Token | Becomes | Examples |
| - | ----- | ------- | -------- |
| a | a path: starts with `/` and is at least two characters long, or starts with `./`, `../` or `~/` | `<path>` | `/var/lib/mysql/ibdata1`, `./run.sh`, `~/x` |
| b | an e-mail address: exactly one `@`; before it one or more ASCII letters, digits or `.` `_` `%` `+` `-`; after it one or more labels of ASCII letters, digits and `-`, separated by single dots | `<email>` | `info@example.com`, `root@localhost` |
| c | an IPv4 address: four groups of one to three digits separated by dots, each 0 to 255, optionally followed by `/` and a prefix length of 0 to 32, or by `:` and a port of one to five digits | `<ip>` | `10.0.0.1`, `192.168.1.20:25`, `10.0.0.0/8` |
| d | an IPv6 address: only hex digits, `:` and `.`; at most seven `:` in total, and either `::` exactly once (never three colons in a row) or exactly seven `:`; every group between colons one to four hex digits (the groups at a `::` may be empty); the last group may instead be an IPv4 address of rule c without prefix or port | `<ip>` | `::1`, `fe80::1`, `2001:db8:0:0:0:0:0:1`, `::ffff:10.0.0.1` |
| e | a hex number with prefix: `0x` or `0X` followed by one or more hex digits | `<hex>` | `0x7f3a`, `0X1F` |
| f | a UUID: hex digits in groups of 8, 4, 4, 4 and 12, separated by `-` | `<id>` | `0123abcd-0123-4567-89ab-0123456789ab` |
| g | a number: at least one digit, and only digits and `.` `:` `/` `-` `+` `%` | `<num>` | `42`, `-5`, `2.0.0`, `10:15:32`, `2026-10-12`, `50%`, `256.1.1.1` |
| h | a number with a unit: one or more digits, optionally `.` and one or more digits, then one to four ASCII letters up to the end | `<num>` and the letters | `123kB`, `5s`, `1.5ms` |
| i | any other token that holds a `:`: it is split at every `:`, each part is replaced by rules a to h and j (an empty part stays empty), and the parts are joined with `:` again | the parts | `total-vm:123kB` → `total-vm:<num>kB`, `mail.example.com:25` → `mail.example.com:<num>`, `http://x/y` → `http:<path>` |
| j | any other token that holds a digit (hex strings such as queue IDs and hashes included) | `<id>` | `eth0`, `sda1`, `4F2A31C0B2`, `4Xb2Kq1Zz0z9`, `v2` |

5. **Text:** a lone surrogate (UTF-16 text that is not valid Unicode) becomes U+FFFD. The template is cut at a character boundary to at
   most **1,024 UTF-8 bytes** (counted on the finished template; the cut is the last step, so a cut template may end in a space); an
   empty message has the empty template.

For example `connect from unknown[203.0.113.7]` becomes `connect from unknown[<ip>]`, `4F2A31C0B2: from=<info@example.com>, size=1234`
becomes `<id>: from=<<email>>, size=<num>`, and `Out of memory: Killed process 1234 (mariadbd) total-vm:1234567kB, anon-rss:654321kB`
becomes `Out of memory: Killed process <num> (mariadbd) total-vm:<num>kB, anon-rss:<num>kB`. A literal `<num>` in a message cannot be
told from a placeholder; a template is a grouping key, not a reversible form of the message.

### Statistics

- A template is identified by the record's **source**, the line's **program** and the template text. The same text from two programs,
  or from the journal and `syslog`, is two templates, so copies of one line in several sources are counted per source.
- Every log line that is **stored** is counted once, in the transaction that stores it: its template's count, its first-seen time (the
  earliest capture time of its lines), its last-seen time (the latest) and the count of the UTC hour its capture time falls in (the hour
  starts at a full hour of Unix time; a capture time in the first, partial hour of the storable range belongs to the hour that starts at
  the beginning of that range). A record that is not stored (a resent agent record, a batch that fails or is rolled back) counts
  nothing. Lines arrive in any order: an older log imported later moves a template's first-seen time earlier.
- The times come from the records' capture times, which an agent or a log file supplies: they are **not trusted**. Any time within
  the storable range is counted as given, so a wrong or forged capture time puts a line into a wrong hour and can move a template's
  first or last seen time.
- Per source and hour the number of stored lines is kept as well; an hour in which a source has at least one line is **covered** for
  that source.
- The statistics are history: they are not changed when log lines are deleted (retention, when it exists, keeps them as hourly
  rollups). Lines stored before the statistics existed (a database of schema version 7) are not counted.

### Queries

Every query except the template of a line takes a time range `from` to `to` (both required, within the storable range, `from` before
`to`) and a limit of 1 to 10,000 results; a broken rule is an error that names the rule.

- **Template of a line:** given a source, a program and a message of at most 16,384 UTF-8 bytes (the longest message a log line
  can have; a longer one is an error), the template the message normalizes to, with its first and last seen time and count, or none.
- **Hourly counts** of one template: the hours that start in the range (from the hour that holds `from`, before `to`) in which the
  template has lines, with their counts, in time order. Hours without a line are not returned.
- **New templates:** the templates whose first-seen time lies in the range and whose source has a template first seen before `from`,
  optionally of one source, ordered by first-seen time, then by identity. A template of a source without earlier lines is not new but
  unknown, and is not returned: the first import of a source makes nothing "new".
- **Templates above their usual level:** for every template and hour that starts in the range (at most **744 hours**, 31 days,
  from `from` to `to`; the first hour is the one that holds `from`, so the hours counted can reach 745), optionally of one source:
  - the **baseline window** is the `baseline hours` before the hour (default **168**, one week; 1 to 2,160), clipped to the storable
    range; the hour itself is not part of it;
  - the **covered hours** are the hours of the window that are covered for the template's source; the template's **usual level** is its
    total count in the covered hours divided by their number (an hour without the template counts as zero);
  - the hour is reported when the source has at least `minimum baseline hours` covered hours (default **24**; 1 to `baseline hours`),
    the hour's count is at least `minimum count` (default **10**; at least 1), and the count is more than `factor` (default **3**; 1 to
    1,000) times the usual level. A template absent from every covered hour has the usual level 0 and is reported once it reaches the
    minimum count.
  - Each result has the template, the hour, the count, the usual level and the number of covered hours; results are ordered by hour,
    then by count (highest first), then by template identity, and the limit applies after these rules.

An hour of the window in which the source has no line at all is **unknown** and is left out, never counted as a quiet hour; with fewer
covered hours than the minimum no usual level is known and nothing is reported.

### Limits of templates

- Names without a digit (host, user, database and unit names, quoted values) stay literal, so lines that differ only in such a name
  are separate templates; random IDs without a digit are not recognized.
- A random ID is `<id>` only when it holds a digit; one that happens to have none stays literal, so the same message shape can give a
  second template for such lines.
- A hex value split by colons that is no IPv6 address (a MAC address) is replaced part by part, and a part such as `1a` keeps its
  letters (rule h), so such values are not one placeholder.
- Rule a takes any token that starts with `/`, rule g any digit-only shape (dates and times, versions, out-of-range addresses such as
  `256.1.1.1`); the placeholders are a grouping, not a validation of the value.
- Text without digits that varies per line (random words, as any local process or remote client can write into some logs) creates one
  template per line. The statistics grow by at most one template and one hourly count per stored line.
- Templates are **external text**: the normalizer removes control characters of category Cc, but keeps format characters (Cf, such as
  U+202E, which reverses the display direction, and U+200B) and any markup; whoever shows a template escapes it like any log line and
  handles format characters (security area 12 of `.squad/project.md`).
- The template statistics are kept as hourly rollups (3 years) and outlive the log lines (90 days): after the lines have expired, a
  template's text is the only trace of them.
- A change of these rules changes only the templates of lines stored afterwards: the old templates keep their statistics, and the
  changed ones start anew. Rebuilding the statistics from the stored lines is not offered yet.
- The cost of writing log lines grows by the normalization and one statement per template and hour a batch touches; text without
  digits that varies per line costs three statements per line. The measurement for one million lines, and for such a hostile case, is
  in `docs/BENCHMARKS.md`.
- The **cost of the usual-level query is not bounded by its range or its limit.** It evaluates every template hour of the range whose
  count reaches the minimum count, all of them before the limit applies, and reads per such hour up to `baseline hours` counts of the
  template; the covered hours are computed once per source and hour (the statement materializes them), the baseline sum once per such hour. Text without digits that varies per line makes one template per
  line, so ten copies of such a line in an hour make a candidate at the default minimum count. A deadline or cancellation does **not**
  stop a running query (the SQLite library used does not interrupt a statement): a caller that serves a user must not rely on a
  deadline alone, but narrows the range and the source and runs the query off the request path. The measured times at the default and
  the widest parameters are in `docs/BENCHMARKS.md`.

## Incidents

An **incident** is one outage reconstructed from the stored data: the OOM kills whose windows overlap, a timeline of the events of the
window, a structured hypothesis about the cause, metric extremes, the connection peak and the log templates of the window. It is
stored (see the storage area, *Incidents*) and kept without limit.

### When incidents are built

Only by the sub-command `vandoxd incidents` (backend host area, *Command line and exit codes*), which **rebuilds from everything
stored**; nothing runs it automatically, so it is run again after each import. Incidents of live and backfilled data alike are built
the same way; nothing alerts (*Live data and backfill*). The agent's `kernel_event` and `gap` records are not read yet (issue #53).

### Kills and windows

- A **kill** is an event of the signature `oom_kill` (the copies of one kill line in `syslog`, `kern.log` and the journal within 60
  seconds are one kill, *Events and merging*). The window of a kill at `t` is `[t − before, t + after)`, both ends clipped to the
  storable range; `before` is `incidents.window_before` (default `1h`) and `after` is `incidents.window_after` (default `30m`), each
  1 minute to 24 hours (configuration area).
- Kills are taken in time order. A kill whose window starts **before the end of the open incident** joins it, and the incident then
  ends where that kill's window ends; otherwise the open incident is closed and the kill opens the next. Windows that only touch are
  two incidents.
- An incident keeps its **first 16 kills** (time, host, victim process ID and command, anonymous RSS, `oom_score_adj`, the first 16
  record IDs, the number of lines) and counts all of them.
- A kill captured at exactly the last storable instant is ignored: no window can end at its own kill.

### Content

- The **lookback** is the window before the first kill. Boots and service events are judged on the lookback; the timeline covers the
  whole window.
- The **timeline** holds every detected event of the window (time of its first line, signature, host, texts, numbers, the first 16
  record IDs, line count), taken in arrival order up to **1,000 entries and 4 MiB** of serialized JSON (each entry measured as it is
  stored). The first event that would exceed either cap and every later one are counted in `timeline_omitted` and not kept. Two
  entries are added outside the budget: `backup_first_seen` and `backup_last_seen`, the first and last process snapshot of the window
  that holds a backup program (only the first when it is the same snapshot; the program name and the backup programs' summed RSS).
  The timeline is ordered by time, then type, then first record ID.
- The **programs of a snapshot** are its `programs` entries or, when it has none, its processes grouped by the first space-separated
  word of `command` (the command itself when that word is empty). A name that starts with `/` becomes the part after its last `/`
  (unless that is empty), so `/usr/sbin/mariadbd` and `mariadbd` are one program. RSS and counts are summed and saturate.
- The **snapshot period** is the lookback after its last boot (`boot` event): a snapshot captured at or before that boot describes
  processes the reboot ended and never serves as the baseline; with no boot the period is the whole lookback. A snapshot at or after
  the first kill never counts.
- The **hypothesis** comes from the first and the last process snapshot of the snapshot period and their number: the **growth
  candidates** are the programs of the last snapshot whose RSS is above their RSS in the first (0 when absent there), by growth
  descending, then name, **top three**; the **consumers** are the last snapshot's programs by RSS, **top three**; `backup_running` is
  true when a snapshot of the period holds a backup program, false when snapshots exist and none does; `recent_boot_at` is the time of
  the last boot in the lookback; `service_events` counts `service_start`, `service_stop` and `service_failed` events in the lookback.
  Backup programs are the built-in list, **`pzstd` only**, matched by the program name after the normalization above.
- **Metric extremes**, in this order: the minimum of `mem_available_bytes`, the maximum of `swap_used_bytes`, of `pressure_avg10`
  with the labels `resource=memory` and `kind=some`, of `pressure_avg10` with `resource=memory` and `kind=full`, and of `load1`.
  Labels must equal exactly; a tie keeps the earliest sample; a value that is not finite is not counted. Each extreme has its value,
  time, source and sample count.
- The **connection peak** is the connection snapshot of the window with the most sockets (the sum of its state counts; a tie keeps
  the earliest), with its time, record ID, the number of snapshots and its three processes with the most sockets (summed per
  command).
- **Templates**: the new templates of the window (source, program, template, first seen, count) and the template spikes of the
  window (source, program, template, hour, count, usual level), the first 20 of each. The spike query covers at most 744 hours from
  the start of the window.

### Unknown is not none

Missing data makes a statement **unknown**, never negative: fewer than two snapshots in the snapshot period give `growth_known` false
and no growth candidates; no snapshot there gives no consumers and `backup_running` null; a metric without samples is absent; no
connection snapshot gives no peak (`connections` null).

### Re-running

- A rebuilt incident is matched to the stored ones whose window overlaps it (storage area, *Writing*, *Incidents*): the oldest number
  keeps the incident and takes the new content, the other overlapped ones are merged into it, an incident without overlap is added,
  and an unchanged one is left untouched. A number that was merged away is never reused.
- A stored incident that no rebuilt one overlaps is **kept** as it is, because its lines may have expired.
- Every incident is saved in its own transaction: an interrupted run keeps what it saved, and the next run completes it.
- An incident the store refuses (a rule of the storage area, such as the 8 MiB bound) is **skipped**: it is logged as a warning with
  its first kill time and the store's text, counted as skipped, and the run goes on.
- The run reads every stored log line in capture order in pages of 1,000 (by capture time, then record ID), so it is never confused by
  lines that share a time. A page that does not lead past the previous one is an error (`store: reading records: page did not
  advance`). For each incident it then reads the log lines and metrics of its window in pages of 1,000 and the process and
  connection snapshots one at a time.

### Limits of incidents

- **Memory** does not grow with the number of lines, kills or incidents: one page of up to 1,000 log lines of the pass over all lines (held while an incident it closes is built, about 40 MiB at the model caps) and one page of the incident's window, two detectors, one open incident and its
  builder (at most 1,000 timeline entries within 4 MiB, two program maps of at most 4,096 entries each, one connection peak, 20 + 20
  templates). The largest single read is one process snapshot; one forged at every cap (a legacy monitoring log with 4,096 processes
  and programs, all texts such that the stored JSON escapes them to six bytes per character, about 100 MiB of stored JSON) peaks at
  about **240 MiB**, plus the page of log lines held by the pass over all lines, which leaves the service less than half of the container's 512 MiB while it is read. Only a database written by an earlier build can hold such a snapshot: this version refuses a payload of more than 16 MiB of JSON when it is written (*Storage*, *Writing*), so a snapshot stored by this version is read from at most 16 MiB of JSON.
- **Time** grows with all stored log lines and, per incident, with the data of its window, so forged kills spaced far enough apart
  to form many incidents multiply the window reads; the cost of the spike query is not bounded by its range (*Limits of templates*).
  The sub-command runs off any request path.
- Kills and boots come from **stored log lines**; a reboot without a stored boot line is not seen, and the snapshots before it then
  count as the growth baseline. Copies of one kill more than 60 seconds apart (a wrong `import.time_zone`) are two kills. The old and
  the new names of a legacy program are only partly joined by the program key. `rss_overcounted` program sums add shared memory
  more than once.
- Backup recognition covers `pzstd` only; other backup programs are not recognized.
- **Forged lines make forged incidents**: any local process can write a line that reads as an OOM kill. A flood of kills close
  together merges into one incident whose kept parts are capped; kills spaced more than `before + after` apart make many incidents,
  each up to 8 MiB and kept without limit, so the disk use from forged lines outlives their retention (issue #46).
- The victim command, hosts, units and templates of an incident are **external text**: whoever shows them escapes them, and the
  terminal output passes the victim through `Terminal.Quote` (security area 12 of `.squad/project.md`).
- The agent's `kernel_event` and `gap` records are not read yet (issue #53). Retention (issue #46) must keep a re-run from replacing
  a stored incident by a poorer one (storage area, *Retention*).

## Nightly report

- The report is sent at **06:00**. If the backend host was off at that time, it is sent as soon as the backfill has completed, so it is
  always based on complete data. The backend therefore needs to know when a backfill is complete.
- The backend sends the report through the Telegram bot (see the notification area); writing it may use the optional AI.

## Service inventory

The inventory of running services on the monitored server may suggest services that are not needed. A service is only ever **disabled
in a reversible, documented way** (for example `systemctl disable` or `mask`; packages stay installed). Agents of the hosting provider
are **never** disabled or changed, and the inventory excludes them from every action, including remote actions. Mail is monitored at the
level of the mail services (process state, ports, logs), never individual mail accounts, so no mailbox credentials exist anywhere.

## Related decisions

- [0008](../decisions/0008-deterministic-detection-and-alerting.md) — why detection is deterministic and AI only writes the report.
- [0020](../decisions/0020-analysis-before-alerting-forensics-release.md) — why the first release is the forensics release.
- [0022](../decisions/0022-backfill-detection-and-live-only-alerts.md) — why the backend classifies live and backfilled data itself.
- [0024](../decisions/0024-nightly-report-timing.md) — why 06:00 or after the backfill.
- [0026](../decisions/0026-services-disabled-reversibly-only.md) — why services are only disabled reversibly.
- [0015](../decisions/0015-mail-services-checked-not-mail-accounts.md) — why mail services are checked, not mail accounts.
- [0094](../decisions/0094-log-signatures-embedded-yaml-linear-patterns-on-stored-lines.md) — why signatures are an embedded YAML catalog of
  linear-time patterns on stored lines, how copies are counted and how remote text is kept out.
- [0095](../decisions/0095-log-templates-token-rules-statistics-in-the-write-path-usual-level-from-covered-hours.md) — why templates
  come from fixed token rules, why the write path keeps their statistics per source and program, and why the usual level uses only the
  hours the source logged.
- [0096](../decisions/0096-outage-incidents-rebuilt-from-stored-data-merged-by-overlapping-windows.md) — why incidents are rebuilt by a
  sub-command, matched by overlapping windows, given a structured hypothesis from fixed rules and bounded in memory and size.

## Not here

- Telegram delivery and who may receive messages: the notification area.
- How gaps are recorded and how batches are identified: the agent area and the wire format.
- Where records are stored, and the tables of the template statistics: the storage area.
- Storing events, and the incidents view (issue #27) and the forensics report (issue #28): later changes.
- Live incidents from the agent's records: issue #53.

## Implementation

`Vandox.Core.Signatures` (`BuiltInSignatures`, `SignatureCatalog`, `SignatureCatalogReader`, `Signature`, `SignatureField`,
`SignatureFieldType`, `SignatureException`, `SignatureDetector`, `DetectedEvent`, `OpenEvent`, `SignatureKey`, `FieldValue`,
`NumberGuard`, and the catalog `signatures.yaml` embedded in the assembly).
`Vandox.Core.Templates` (`LogTemplateNormalizer`) for the normalization; `Vandox.Storage` (`BatchWriter`, `TemplateTally`,
`TemplateQueries`, `SqliteStore` as `ILogTemplateReader`) for the statistics and the queries.
`Vandox.Core.Incidents` (`Incident` and its parts, `KillClusterer`, `KillCluster`, `IncidentBuilder`, `MemoryAnalysis`,
`BackupPrograms`, `IncidentSignatures`, `IncidentLimits`, `IncidentJson`); `Vandox.Storage` (`IIncidentStore`, `IncidentRepository`,
`SqliteStore`); `Vandox.Backend` (`Incidents/IncidentReconstructor`, `Cli/IncidentsCommand`, `Cli/IncidentSummaryWriter`).
