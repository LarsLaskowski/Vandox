# Detection, alerts and reports

## Scope

How the backend turns stored records into findings: that detection is deterministic, which data may raise an alert, when the nightly
report is sent, and what the first release does and does not do. The log signatures and their detector are implemented (*Log
signatures*); the rules and thresholds are **not implemented yet**, and this document fixes the principles they are held to. How records get stored is in the storage area; how gaps are
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

## Not here

- Telegram delivery and who may receive messages: the notification area.
- How gaps are recorded and how batches are identified: the agent area and the wire format.
- Where records are stored: the storage area.
- Incidents built from events, storing events, and any command or view that shows them: later changes (incident reconstruction,
  issue #23).

## Implementation

`Vandox.Core.Signatures` (`BuiltInSignatures`, `SignatureCatalog`, `SignatureCatalogReader`, `Signature`, `SignatureField`,
`SignatureFieldType`, `SignatureException`, `SignatureDetector`, `DetectedEvent`, `OpenEvent`, `SignatureKey`, `FieldValue`,
`NumberGuard`, and the catalog `signatures.yaml` embedded in the assembly).
