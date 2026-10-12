# Detection, alerts and reports

## Scope

How the backend turns stored records into findings: that detection is deterministic, which data may raise an alert, when the nightly
report is sent, and what the first release does and does not do. The log signatures and their detector are implemented (*Log
signatures*), and so are the log templates with their statistics and queries (*Log templates*); the rules and thresholds are **not
implemented yet**, and this document fixes the principles they are held to. How records get stored is in the storage area; how gaps are
recorded is in the agent area.

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
stored and no record is changed; no command or view shows events yet (incidents are built on them later).

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
   control character.
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
   most **1,024 UTF-8 bytes** (counted on the finished template); an empty message has the empty template.

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
- Per source and hour the number of stored lines is kept as well; an hour in which a source has at least one line is **covered** for
  that source.
- The statistics are history: they are not changed when log lines are deleted (retention, when it exists, keeps them as hourly
  rollups). Lines stored before the statistics existed (a database of schema version 7) are not counted.

### Queries

Every query takes a time range `from` to `to` (both required, within the storable range, `from` before `to`) and a limit of 1 to
10,000 results; a broken rule is an error that names the rule.

- **Template of a line:** given a source, a program and a message, the template the message normalizes to, with its first and last
  seen time and count, or none.
- **Hourly counts** of one template: the hours that start in the range (from the hour that holds `from`, before `to`) in which the
  template has lines, with their counts, in time order. Hours without a line are not returned.
- **New templates:** the templates whose first-seen time lies in the range and whose source has a template first seen before `from`,
  optionally of one source, ordered by first-seen time, then by identity. A template of a source without earlier lines is not new but
  unknown, and is not returned: the first import of a source makes nothing "new".
- **Templates above their usual level:** for every template and hour that starts in the range (at most **744 hours**, 31 days,
  from `from` to `to`), optionally of one source:
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
- Templates are **external text**: the normalizer removes control characters, but whoever shows a template escapes it like any log line
  (security area 12 of `.squad/project.md`).
- A change of these rules changes only the templates of lines stored afterwards: the old templates keep their statistics, and the
  changed ones start anew. Rebuilding the statistics from the stored lines is not offered yet.
- The cost of writing log lines grows by the normalization and one statement per template and hour a batch touches; the measurement for
  one million lines is in `docs/BENCHMARKS.md`. The usual-level query reads up to `baseline hours` counts per reported candidate, which
  its range limit bounds; callers that serve a user pass a deadline.

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

## Not here

- Telegram delivery and who may receive messages: the notification area.
- How gaps are recorded and how batches are identified: the agent area and the wire format.
- Where records are stored, and the tables of the template statistics: the storage area.
- Incidents built from events, storing events, and any command or view that shows them: later changes (incident reconstruction,
  issue #23).

## Implementation

`Vandox.Core.Signatures` (`BuiltInSignatures`, `SignatureCatalog`, `SignatureCatalogReader`, `Signature`, `SignatureField`,
`SignatureFieldType`, `SignatureException`, `SignatureDetector`, `DetectedEvent`, `OpenEvent`, `SignatureKey`, `FieldValue`,
`NumberGuard`, and the catalog `signatures.yaml` embedded in the assembly).
`Vandox.Core.Templates` (`LogTemplateNormalizer`) for the normalization; `Vandox.Storage` (`BatchWriter`, `TemplateTally`,
`TemplateQueries`, `SqliteStore` as `ILogTemplateReader`) for the statistics and the queries.
