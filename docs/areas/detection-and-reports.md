# Detection, alerts and reports

## Scope

How the backend turns stored records into findings: that detection is deterministic, which data may raise an alert, when the nightly
report is sent, and what the first release does and does not do. The log signatures and their detector are implemented (*Log
signatures*), the log templates with their statistics and queries (*Log templates*), the reconstruction of outage incidents from
the stored data (*Incidents*) and the forensics report over the whole stored period (*Forensics report*); the classification of records as live or backfilled, with the gaps and the clock offset it rests on, is written when a batch is stored (*Live data and backfill*); the alert rules and
thresholds, the nightly report and the service inventory are **not implemented yet** (they
need the agent and the ingest API of v0.2.0), and this document fixes the principles they are held to. How records get stored is in the storage area; how gaps are recorded is in the agent area.

## Principles

- **Detection, incident reconstruction and alerting are deterministic**: rules, thresholds and log signatures. Every alert can be
  explained from rules and values and is covered by unit tests. A new failure pattern needs a new rule or signature.
- **AI is optional and only writes the nightly report.** It never decides whether an alert fires. Vandox works fully without any AI
  service.
- **Analysis comes before alerting.** The first release (v0.1.0) is the forensics release: log import, storage, outage incidents, the
  forensics report and the web views needed to reconstruct outages from imported logs. The agent with its spool and backfill follows in
  v0.2.0; alerting, the nightly report and remote actions build on them in later releases, with thresholds based on what the analysis showed. The data model must serve later rules without migration surprises.

## Live data and backfill

The backend classifies **every agent record as live or backfilled** when it stores it (record 0130, amending 0022). The decision is
final: a stored record is never updated, so a record that was backfilled never becomes live and can never alert later. No alert exists yet
that reads the classification; the rule engine (#56, #61) and the gap views (#54) are its readers, through the store ([Storage](storage.md),
*Reading*). The header's `mode` and `clock_offset_ns` are not used; the agent sends no clock offset.

- **The rule.** A record is **live** exactly when its *lateness* lies within the allowance of its kind and no open sequence gap of its
  agent starts below its number; otherwise it is backfilled. Lateness is the batch's receive time minus the record's capture time
  corrected by the agent's measured offset (agent clock minus backend clock). The allowance is 10 minutes, early or late, for `log_line` and
  `kernel_event` (their time comes from a log, read in budgeted runs) and 90 seconds for every other kind. The open gaps are those that remain after the whole
  batch has been applied, so the batch that brings the last missing number is live by lateness alone. Fresh records in a batch marked `backfill` are live, old records in a batch marked `live` are not.
- **Alert rules are evaluated on live data only.** Backfilled data is stored and analyzed, appears in incidents and in the report, and
  never alerts, so there is no alert storm after the backend host was off. Imported data (origin `import`) and records of the backend are
  stored backfilled and take no part in gaps or the offset; so are the agent records stored before the upgrade to schema 12.
- **Fail-safe.** Every case the data cannot settle is backfilled: an open gap below, a capture time outside the allowance, an offset that is not yet measured
  where the clock is behind. The classification is no trust boundary: an agent that holds the token chooses its capture times and numbers.
- **Sequence gaps.** A *sequence gap* is a run of an agent's numbers the backend has not stored. A number above the highest stored one plus 1 opens a gap
  (`sequence_missing`, state `open`) for the numbers between; numbers below the first number the backend received from the agent are never missing (the agent's first
  number is large, and an agent whose numbers jumped after a fresh start or an unclean stop leaves a gap that way). A record arriving into a gap removes its number:
  the gap shrinks, splits in two (the rest has the same cause and state) or is `closed` when nothing is left. A gap is **open** (its data may still arrive),
  **closed** (all of it arrived) or **final** (it will not arrive). A record that arrives into a final gap is stored backfilled and shrinks it like an open one. An agent `gap` record
  with `first_seq` and `last_seq` makes the numbers it names **below its own number** final at once, with its cause (`spool_dropped`, `unknown`, ...), so
  they never hold back later records; numbers at or above its own number are ignored.
- **The final wait.** When no record has arrived into any open sequence gap of an agent for **5 minutes** (counted from the last such record, the opening of a gap,
  or the first batch stored after the backend started, whichever is latest), the next batch stored from that agent makes all its open sequence gaps final, with
  that batch's commit as `changed_at`, and its records and later ones are live again. The agent's backfill sends oldest first and does not skip an unacknowledged record,
  so a pass that fills nothing for that long has nothing left for the gaps. After an unclean stop, current data stays backfilled for the backfill's duration plus 5 minutes. While
  the agent sends nothing, nothing waits for the classification.
- **Time gaps.** A *time gap* is a span of more than **2 minutes**, in the agent's raw capture times, in which the backend holds no `metric` record of the agent (cause
  `no_data`, the backend's own: the data cannot tell a stopped agent from one that ran without recording a metric). It is detected also below the first number and beside the
  agent's own gaps; readers see both, the agent's cause being the more specific. Metrics that arrive inside it narrow, split or close it. A time gap reads `final` when no open sequence gap lies between the
  numbers of its neighbouring samples and `open` while one does; the state is derived when it is read, so no batch re-evaluates the time gaps. Open time gaps never affect the classification. A clock
  step of more than 2 minutes shows as a time gap forward and hides one backward (residual, because raw times are used).
- **The offset.** The agent's clock offset is *measured* by the backend. A **bound** is a record's capture time minus its receive time, a lower bound of agent clock minus backend
  clock (the delay is never negative); only records timed by the agent's run give bounds, so a future-stamped `log_line` or `kernel_event` never raises it. A **counted sample** is
  a `self` metric `agent_send_lag_seconds` of at most **60 seconds** (one `system` interval plus the send period and timeout), which is the highest number stored from the agent and has no open sequence gap
  below it; so a sample sent late by a backfill or a restart, or taken while a gap is open, is never counted and the offset stays unchanged during a backfill. A counted sample **measures** when its bound is at most 90 seconds below the
  offset in effect (0 before the first measurement), so a raised bound measures at once; a bound further below measures only when the agent's **previous counted sample**, captured and received **30 seconds to 5 minutes** earlier,
  lies within 90 seconds of it (the confirmation spacing; its lower bound is half the 60-second self interval, so timer jitter and delivery delay never stop a pair from confirming). Every counted sample becomes the previous one, measuring or not; a sample that does not count never replaces it. So a late counted sample that a full-holes live stream brings as the head, whose bound is far below
  the offset and which no earlier sample confirms, never measures, and a clock that is behind, or stepped back, first measures with its **second** counted sample, about a minute after the first (a stepped-back clock's offset follows once the older bounds have left the window, below). From the first measuring
  sample on, the offset is the largest bound of the measuring samples in the 5 minutes before the newest of them; a measuring sample counts for the records of its own batch. Before the first one it is the largest bound
  of the run-timed records of **earlier batches** within the last 5 minutes before the newest of them, and 0 when that is below 0 or there is none (a batch's own bounds are added after its
  records are classified, so a record cannot prove itself current). The windows are kept as bounded rows of 10-second buckets, so an expired bound falls back to the true maximum of the window. Consequences: an agent clock that is behind by more than the allowance yields only backfilled records until its second counted sample;
  an old record is never made live by a clock that is ahead; after a clock step back by more than the allowance, current records are backfilled until the bounds from before the step have left the 5-minute window, about 6 minutes after the step (the second counted sample after the step measures, but the window's maximum keeps the larger bound until then), and a step forward
  makes fresh records backfilled until the next counted sample (before the first one, at most 5 minutes). The offset can be short by the delivery delay of the freshest counted sample,
  up to the agent's 30-second request timeout, so a record can be up to that much older than its allowance and still be live. The offset exceeding 90 seconds is logged (*Backend host*, *Ingest endpoint*).
- **Residuals.** Two late counted samples that agree (an agent started twice during an outage), 30 seconds to 5 minutes apart in capture and in receive time, confirm each other and can move the offset to a value that is too low for fresh records; they are backfilled until a later sample raises it. Log lines of a flood that the agent reads more than 10 minutes after they were written, and a backup start seen more than 90 seconds after it began, are backfilled. The time gaps use raw capture times. A stored record adds at most three
  gap rows and every lookup is an index search, so a hostile agent cannot make the work per record grow.
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
| `php_too_many_connections` | PHP's `mysqli` (warning of `mysqli_connect`, `mysqli_real_connect`, `mysqli::__construct`, `mysqli::connect`, `mysqli::real_connect`, `mysqli::mysqli`, or the uncaught `mysqli_sql_exception`) or PDO (uncaught `PDOException`) could not connect to MariaDB because of *Too many connections* (error 1040) | sources `nginx-error`, `sw-cp-server-error`, `apache-error`; message anchored with `\A` at the first PHP message after nginx's FastCGI prefix, Apache's `mod_proxy_fcgi` (`AH01071`) or `mod_fcgid` prefix, or a message of PHP's Apache module (`php`, `php5`, `php7`, `php8`) | none |
| `service_start` | systemd started a unit | program `systemd`; message `Started ...` | `unit` (when named), `description` |
| `service_stop` | systemd stopped a unit | program `systemd`; message `Stopped ...` | `unit` (when named), `description` |
| `service_failed` | a systemd service failed | program `systemd`; message `<unit>.service: Failed with result '<result>'.` | `unit`, `result` |
| `disk_full` | MariaDB could not write because a file system was full | programs `mariadbd`, `mysqld` or source `mariadb`; message anchored with `\A` | none |
| `php_disk_full` | PHP could not write a file because a file system was full (`errno=28`) | as `php_too_many_connections` | none |
| `nginx_disk_full` | nginx could not write a file because a file system was full (`(28: No space left on device)` of `pwrite()`, `write()`, `pwritev()`, `writev()`) | sources `nginx-error`, `sw-cp-server-error`; message anchored with `\A` | none |
| `postfix_fatal` | Postfix logged a fatal error or a panic | events `postfix.fatal`, `postfix.panic` | none |
| `dovecot_fatal` | Dovecot logged a fatal error or a panic | events `dovecot.fatal`, `dovecot.panic` | none |
| `boot` | the kernel started (the first kernel line of a boot) | program `kernel` | `kernel_version` |

`disk_full` reads only MariaDB's own error messages, and `mariadb_too_many_connections` only the MySQL client errors of Postfix and
Dovecot. The web server and PHP have a signature each: `nginx_disk_full` reads nginx's failed writes (`pwrite()`, `write()`,
`pwritev()`, `writev()` at `crit` or `alert`), `php_disk_full` PHP's `Notice` about a failed write and `php_too_many_connections`
the `mysqli` and PDO errors above, as nginx, `sw-cp-server` (nginx's format) and Apache (`mod_proxy_fcgi`, `mod_fcgid`, PHP's Apache
module) log them. The formats were checked against the sources of nginx 1.18.0, Apache httpd 2.4.52, `mod_fcgid` and PHP 7.4 and
8.3, not against logs of the server. The signatures read the agent's live records of these sources (`nginx-error`, `sw-cp-server-error`, `apache-error`) as they read the imported ones: the agent's mapping gives the same source, priority and message ([Agent](agent.md), *Error log lines*), and the golden records of the import pin it. Only the **first PHP message** after the server's prefix is read, so an error that follows an
earlier PHP message of the same request is missed. An Apache line without a module name (`[:warn]`) is not read, and PHP's write
error is read only when ` in /<script path>` follows the error text, so a write error without a script (origins `Unknown`,
`PHP Request Shutdown`, `in Unknown on line 0`, e.g. a session or output flush at shutdown) is not recognized. Not read at all: the
Plesk panel log (its format is unverified), Apache's own status texts (`(28)No space left on device:` is also printed for
exhausted semaphores), Apache 2.2, a custom `ErrorLogFormat`, nginx's `open()` and `mkdir()` (they report requested paths) and
PHP's `Only 0 of N bytes written`. One full disk that MariaDB and nginx both log gives one event per signature.
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
  events list their record IDs so a consumer can weigh them. Live lines are classified by the agent now ([Agent](agent.md),
  *Classification*), so forged `postfix/*`, `dovecot`, and `mariadbd` and `kernel` lines of the system files make live events too, which
  the alert rules (#56, #61) must weigh. A line of a user journal file gets the mail classification only and never joins a server entry.
- `postfix_fatal` and `dovecot_fatal` cover both the fatal and the panic event and have no field, so a fatal error and a panic of one
  daemon within 60 seconds are **one event**; nothing in the event tells which of the two it was, the linked lines and their `event`
  do.
- Merging assumes the capture times of the copies are comparable. A syslog file read with a wrong `import.time_zone` (or across a
  daylight saving change the zone does not describe) lies hours away from its journal copy, so the two are more than 60 seconds apart
  and are **counted as two events**.
- A flood of 1,024 or more distinct matching lines within 60 seconds (which any local process, and for some signatures a client of
  MariaDB, can write) **closes events early**, so the later copy of a real event can become an event of its own.
- The **anchor has residuals**. It keeps out client text in the fields the servers add (request line, host, referrer, upstream,
  paths) and in PHP's own messages (the 1040 warning only from the mysqli connect functions, whose origin carries no parameters),
  but not (1) output the writer splits (nginx keeps the line feeds of PHP output; `mod_fcgid` logs each line of standard error as
  an entry; nginx and `mod_proxy_fcgi` log FastCGI standard error in pieces of their read buffer), nor (2) a message whose head the
  application writes (`trigger_error`, which logs no origin; `error_log` without a log file, written verbatim to FastCGI or Apache;
  programs started from PHP or a CGI that write into Apache's error log directly, whose raw lines need only the prefix). An HTTP
  client whose request data an application puts into such a message can create a PHP event. Likewise a WebDAV `COPY` destination
  or a variable access log path in a failed nginx write, and an Apache client address that a reverse-proxy module copies
  unchecked from a forwarded header (`mod_remoteip` stores a parsed address; Plesk's module is not verified). Record 0094 has
  the reasoning.
- Real repeats with equal fields within 60 seconds are one event, whose lines are all linked and counted. On systemd 249 start and stop
  name the description, so scopes and timers match too. The kill line of `oom_kill_allocating_task` is recognized and ends its kernel
  report like the other kill lines.
- The **text fields are external text**: they may hold control characters (the built-in patterns exclude the line feed from them) and
  showing them falls under security area 12 (*Logging and display of external data*) of `.squad/project.md`.

## Log templates

A **template** is the message of a log line with its variable parts replaced by placeholders, so that lines which differ only in
numbers, IDs, addresses or paths share one template. The backend keeps, per template, when it was first and last seen and how many
lines it had in each hour, and answers two questions from that: which templates are **new** in a time range, and which hours of a
template are **above its usual level**. The logs view shows them (*Templates of a range*, *Lines of a template*); no command shows
the new templates or the usual-level hours yet.

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
  rollups). Lines stored before the statistics existed (a database of schema version 7) are not counted until `vandoxd templates` rebuilds the statistics.

### Queries

Every query except the template of a line takes a time range `from` to `to` (both required, within the storable range, `from` before
`to`) and a limit of 1 to 10,000 results (1 to 1,000 for the templates of a range and the lines of a template); a broken rule is an
error that names the rule.

- **Template of a line:** given a source, a program and a message of at most 16,384 UTF-8 bytes (the longest message a log line
  can have; a longer one is an error), the template the message normalizes to, with its first and last seen time and count, or none.
- **Hourly counts** of one template: the hours that start in the range (from the hour that holds `from`, before `to`) in which the
  template has lines, with their counts, in time order. Hours without a line are not returned.
- **New templates:** the templates whose first-seen time lies in the range and whose source has a template first seen before `from`,
  optionally of one source, ordered by first-seen time, then by identity. A template of a source without earlier lines is not new but
  unknown, and is not returned: the first import of a source makes nothing "new".
- **Templates of a range:** the templates with at least one line in the hours that start in the range, optionally of one source and one
  program, each with its number of lines in those hours, most lines first, then by identity, at most 1,000 (the logs view asks for 200).
  The count is kept per UTC hour, so a range that starts or ends inside an hour counts that **whole hour**. A template is fetched by its
  number. The rules and error texts are in the storage area (*Log exploration*); the logs view is in the web UI area (*Logs view*).
- **Lines of a template:** the stored log lines of the template's source and program whose message normalizes to the template's text,
  found by normalizing candidates that the template's literal words select (storage area, *Log exploration*), so the result is exactly the lines the statistics counted as long as the normalization rules have not changed since they were counted; after a change of the rules without a rebuild, the
  lines of an old template are those that normalize to its text under the **current** rules, and may differ from its count.
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
- With the planned retention (storage, *Retention*), the template statistics are kept as hourly rollups (3 years) and outlive the log
  lines (90 days): after the lines have expired, a template's text is the only trace of them. Until retention exists nothing expires.
- A change of these rules changes only the templates of lines stored afterwards until the statistics are rebuilt: after a rule change
  `vandoxd templates` recounts every stored line under the current rules, then `vandoxd incidents` is run again.
- While a rebuild runs and after an interrupted one (the `template_rebuild` row in `meta`, *Template rebuild* in the storage area), hours
  may hold **partial counts**: they count as covered with counts too low, so new-template and usual-level results are wrong, not
  unknown, until a run completes (the "unknown" rule of *Queries* covers only hours with no line of the source, as after the schema-7
  upgrade).
- The cost of writing log lines grows by the normalization and one statement per template and hour a batch touches; text without
  digits that varies per line costs three statements per line. The measurement for one million lines, and for such a hostile case, is
  in `docs/BENCHMARKS.md`.
- The **cost of the usual-level query is not bounded by its range or its limit.** It evaluates every template hour of the range whose
  count reaches the minimum count, all of them before the limit applies, and reads every template hour of the range and of the
  `baseline hours` before it once (the baseline sums are a window function per template); the covered hours are computed once per
  source and hour (the statement materializes them). Text without digits that varies per line makes one template per
  line, so ten copies of such a line in an hour make a candidate at the default minimum count. A deadline or cancellation
  stops a running query (storage, *Database file and connections*) with no partial result: a caller that serves a user bounds it
  with a deadline and narrows the range and the source so that it can finish within it. The measured times at the default and
  the widest parameters are in `docs/BENCHMARKS.md`.

## Incidents

An **incident** is one outage reconstructed from the stored data: the OOM kills whose windows overlap, a timeline of the events of the
window, a structured hypothesis about the cause, metric extremes, the connection peak and the log templates of the window. It is
stored (see the storage area, *Incidents*) and kept without limit. The web UI shows them: a list and a page per incident, with charts of the window,
every time linked to the logs of that moment, and a note per incident ([Web UI](web-ui.md), *Incidents view*).

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
  command). It is taken over the snapshots of **every source** in the window, regular and full alike (the state counts cover every
  socket in both). The agent's snapshots (source `connections`) count sockets without an owner, such as `TIME_WAIT`, which `lsof -ni`
  never listed, so when a window holds snapshots of the agent and of an imported legacy log, the agent's usually win the peak; the
  peak names its record, so its source can be read there. This is accepted, not filtered
  ([0115](../decisions/0115-connection-collector-proc-net-tables-owners-from-fd-regular-and-full-snapshots-bounded-listener-check.md)).
  The *Connections* chart of the incident page shows the source of the earliest snapshot only ([Web UI](web-ui.md)).
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
- **Notes stay with the incident.** The note a user wrote for an incident ([Web UI](web-ui.md), *Incidents view*) survives every re-run that
  updates or keeps the incident (the number is kept), and a note of an incident that a re-run merges away is moved to the incident that
  survives, in the same transaction, keeping the number it was written for (storage area, *Writing*, *Incident notes*). A re-run never
  deletes or rewrites a note.
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

## Forensics report

The **forensics report** summarizes the whole stored period: every outage incident with its victims and hypothesis, whether outages
recur at a time of day or follow a backup, a boot or use of the Plesk panel, which programs use the most memory permanently and
transiently, and which new listeners turned up, across an agent start or found by the agent. It is computed on demand from the stored log lines, process snapshots, incidents and listener comparisons, stores nothing, and is written as
Markdown to standard output by `vandoxd report` (backend host area, *Command line and exit codes*). It lists the incidents as they are
stored, so `vandoxd incidents` is run before it. The web UI shows the same report on the page `/report` and offers it as a Markdown download
(*Report page* of the [Web UI](web-ui.md) area): both compose it from the same blocks as the command, so the texts, numbers and times are the same; the page
lists the outages 50 at a time, the command and the download list all of them.

**All times are UTC**, in the report and in its rules; `import.time_zone` only decides how an import reads local times.

### What it reads

- **Every stored log line**, in capture order in pages of 1,000 (by capture time, then record ID, as *Re-running* of *Incidents*):
  the first and last capture time of all lines, the observed hours (below), and the **boots**: the events of the signature `boot`
  (copies merged as in *Events and merging*).
- **Every stored process snapshot**, one at a time in capture order: the series for the activities and the memory consumers. Its
  programs are the *programs of a snapshot* of *Incidents* (names that start with `/` reduced to their last part).
- **Every stored incident**, one at a time in the order of its window, **twice**: the first pass feeds the rules, the second lists
  each outage. Stored incidents never overlap, so each page starts at the end of the previous incident's window; a page that does not
  lead past it is an error (`store: reading incidents: page did not advance`). When the second pass lists a different number of
  incidents than the first (a `vandoxd incidents` run in between), the notes say so and ask for a new run.
- **The stored listener comparisons** (the derived tables `connection_marks` and `listener_comparisons`, [Storage](storage.md)), by three small
  reads after the first pass over the incidents: the counts of the lowest 64 agent IDs, the latest 20 findings after an agent start and the
  latest 20 findings of the agent. No connection snapshot is read or decoded by the report; the write path decodes the snapshots around an agent
  start when it stores them (*New listeners*).

### Observed hours

An **observed hour** is a UTC hour (it starts at a full hour of Unix time, also before 1970) in which at least one log line of the
source `journal` or `syslog` is stored: the system log, which holds the kernel's kill and boot lines. An hour without such a line is
**unknown**: it takes no part in any base rate, and an outage cannot be judged on it. Hours in which only other sources (web server,
mail, MariaDB's own error log) logged are not observed.

### Activities before a moment

The **lookback** of a moment `t` is `[t − lookback, t)`, where the lookback is `incidents.window_before` (default `1h`, configuration
area). For a moment the report judges three activities, each **yes**, **no** or **unknown**:

| Activity | Unknown when | Yes when |
| -------- | ------------ | -------- |
| Backup running | no process snapshot lies in the lookback | a snapshot in the lookback holds a backup program (`pzstd`, as in *Incidents*) |
| Boot | an hour that overlaps the lookback is not observed | a boot lies in the lookback (the report gives the time of the last one) |
| Panel memory high | no process snapshot lies in the lookback, or there is no panel threshold | a snapshot in the lookback has panel memory at or above the threshold |

Otherwise the activity is **no**. The **panel memory** of a snapshot is the summed RSS of its programs whose name starts with
`sw-engi` (ordinal, case-sensitive): `sw-engi` as `top` cuts it in the old legacy format, `sw-engine`, `sw-engine-fpm`. The **panel
threshold** is the 90th percentile (nearest rank: the value at position ⌈0.9 · n⌉ of the n snapshots' panel memory in ascending order,
a snapshot without a panel program counting 0); there is none when it is 0.

An **outage's activities** are those of the moment of its first kill. They are the report's own: judged over the whole lookback, not
cut at the last boot like the incident's stored hypothesis, so an outage whose backup ran before a reboot in its lookback reads "yes"
here while its stored `backup_running` is false.

### Rules

Each rule compares the **share of outages** that have a property with the **base rate**, the share of observed hours that have it:

- **Time of day**, for each of the 24 hours of the day (UTC): the outages whose first kill lies in that hour of the day, of all
  outages, against the observed hours that are that hour of the day, of all observed hours. Every outage counts as known.
- **Backup running**, **Boot**, **Panel memory high**: the outages whose activity is yes, of those where it is known, against the
  observed hours whose **end** (the moment the next hour starts) has the activity yes, of those where it is known. With the default
  lookback of one hour, an hour's activity is the activity within that hour.

A rule's result is one of three:

- **Unknown** — in this order: a limit of the report was hit for its data (below); for panel memory there is no panel threshold;
  fewer than **3** outages are known; no observed hour is known. Unknown is never shown as "not detected", and the reason is given.
- **Detected** — at least **2** known outages have the property, their share is above the base rate, and the probability of at least
  that many among the known outages when each has the property with the base rate (the binomial tail) is at most **1 %**; for the time
  of day at most **1 % / 24** per hour, because 24 hours are tested. A base rate of 0 makes the probability 0, a base rate of 1 makes
  a higher share impossible.
- **Not detected** — otherwise.

### Memory consumers

Per program, over all stored process snapshots: the number of snapshots that hold it, its mean RSS over those snapshots (the sum
divided by their number, rounded down), and its peak RSS with the capture time of the first snapshot that reached it. RSS is the
summed RSS of the program's processes, which counts shared memory more than once.

- **Permanent consumers**: programs in at least **90 %** of all process snapshots, by mean RSS (highest first), then name; the first
  **5**.
- **Transient consumers**: all other programs, by peak RSS (highest first), then name; the first **5**.

At most **4,096** programs are tracked: a program first seen after that is not, and the notes give the number of its snapshot entries.
Without a process snapshot both lists say unknown.

### New listeners

The agent reports a **new listener** by comparing a run's listener inventory with the previous run's, held in memory, so the first run after
an agent start is a baseline and a listener that opened while the agent was stopped (an update, a crash, a reboot with a service that starts
at boot) is never reported by the agent. The backend closes that gap when it stores the snapshots (record 0116) and shows the result, with
the agent's own findings, in the section **New listeners**.

- **Which snapshots are compared.** For each agent ID, the first connection snapshot after each agent start (`listener_check` `start`, the agent's
  first run after it started or the machine rebooted) with the last one of the same agent before it. Both are picked by the record's `seq`, the
  agent's persistent counter, which increases strictly across restarts and reboots, never by capture time, which is shown only (a clock can
  step back after a reboot). No other pair is compared. Only a **usable** snapshot takes part: origin `agent`, source `connections`, an agent ID, and the fields
  `listener_check`, `inventory` and `tables` of format 1.1 ([Wire format](wire-format.md)). Snapshots of another source, of a 1.0 agent and
  imported ones are never a side of a comparison, never a finding and not counted.
- **Outcomes** of a start (`ListenerComparer`, a pure function of the two snapshots with the agent's rules; the result is stored, *Storage*):
  - `not compared: no earlier inventory`: no usable snapshot of the agent has a lower `seq` (a first installation, or the earlier part is not stored);
  - `not compared: inventories not comparable`: a snapshot's `inventory` is `skipped` (a table line was skipped), the two read different
    tables, or the stored earlier snapshot is missing or does not decode;
  - `not compared: ports 1 to 1023 not complete`: either `inventory` is `cut`, so a listener on a port below 1024 may be missing from the list;
  - `all ports`: both inventories are `all`: every listener is compared;
  - `ports 1 to 1023 only`: either is `privileged` (the list was cut for size or at the inventory cap, but holds every listener on ports 1 to 1023):
    only the listeners on ports below 1024 are compared.
  A start that is not compared is always a finding and says why; it never reads as no new listener.
- **A new listener** is a listener of the later snapshot whose protocol and local address and port are not in the earlier one (`IPEndPoint`
  equality, the agent's key: `0.0.0.0` and `::ffff:0.0.0.0` are different addresses). A change of the owner is not new, an unconnected UDP socket on a
  port from 32768 to 60999 is not listed (the agent's rule), and a listener without a local endpoint is ignored. A compared start without a new
  listener writes no finding. A finding lists at most 64 new listeners in inventory order, each with protocol, local address and port and, when
  known, the owner's PID and command, then `and <n> more`.
- **The agent's own findings.** Every stored usable snapshot whose `listener_check` is `all` or `privileged` and that reports at least one new
  listener is a finding **found by the agent**, with the agent's own list (the first 64 stored) and its omitted count; a `none` or `start`
  snapshot reports none.
- **Order independence.** The comparison of a start depends on the records only: it is computed again when a snapshot is stored below a stored start
  (backfill sends the current data first), so the same records give the same section whatever order or batches they were stored in, and a
  resent batch changes nothing. The findings are each listed once.
- **Caps.** The head names at most the 64 lowest agent IDs; the findings after an agent start and the findings found by the agent are capped
  **separately** at the latest 20 each (by capture time, then agent ID, then `seq`), so a server whose passive FTP produces an agent finding every minute cannot
  push a restart finding out. Within the section the listed findings stand by agent ID, then `seq`. Notes name each cap hit and the number left out.

Residuals: a listener that opened and closed while the agent was stopped is never seen; a start whose first snapshot is lost (a gap, a record the
scheduler dropped) is not seen as a start, and its successor was compared by the agent with the lost inventory; findings are "latest" by capture
time, so a clock that stepped back may keep an older finding instead of a newer one. No alert is raised by a finding (the rules are not built;
backfilled data never alerts, *Live data and backfill*). A compromised agent can forge snapshots and so findings, like any record.

### Content and format

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
- **New listeners** (after the memory consumers, before the outages): without any stored usable agent connection snapshot one paragraph, `No agent
  connection snapshot stored: new listeners unknown.` (`Kein Verbindungs-Snapshot eines Agenten gespeichert: neue Listener unbekannt.`).
  Otherwise one list item per agent (the head): the agent ID, the number of its connection snapshots with the first and last capture time, and its agent
  starts: `<n> connection snapshots from <time> to <time>; <n> agent starts: <n> compared on all ports, <n> on ports 1 to 1023 only, <n> not compared`
  (German: `<n> Verbindungs-Snapshots von <time> bis <time>; <n> Agentenstarts: <n> auf allen Ports verglichen, <n> nur auf Ports 1 bis 1023, <n> nicht
  verglichen`). Then the findings after an agent start, then those found by the agent, each one list item with its listeners as items below it:
  - `<time>, agent <id>, after an agent start (compared with <time>), all ports: <n> new` or `…, ports 1 to 1023 only: <n> new`
    (`nach einem Agentenstart (verglichen mit <time>), alle Ports` / `nur Ports 1 bis 1023`, `<n> neu`);
  - `<time>, agent <id>, after an agent start, not compared: <reason>` with the reason `no earlier inventory`, `ports 1 to 1023 not complete` or
    `inventories not comparable` (`nicht verglichen: kein früheres Inventar`, `Ports 1 bis 1023 unvollständig`, `Inventare nicht vergleichbar`);
  - `<time>, agent <id>, found by the agent, all ports: <n> new` (`vom Agenten gefunden`);
  - a listener: `<proto> <address>:<port>, PID <pid> <command>` or `<proto> <address>:<port>, no owner` (`kein Besitzer`), the address in the
    backend's numeric form (an IPv4-mapped address is written as .NET writes it); `and <n> more` (`und <n> weitere`) for the listeners left out.
  When no finding holds a new listener the section ends with `No new listener found.` (`Kein neuer Listener gefunden.`), or, when a start was not
  compared, `No new listener found among the compared inventories.` (`Kein neuer Listener unter den verglichenen Inventaren gefunden.`).
- **Outages**: one section per stored incident, in the order of its window, with its number and first kill: the window; the kill
  count and the **victims** (the distinct victim commands of the kept kills in first-seen order with their count; a kill without a
  victim counts as unknown; the number of kills not kept, if any); the **hypothesis**: the growth candidates with their growth
  (unknown when the incident's growth is not known, "none" when known and empty), the largest consumers with their RSS (unknown
  when the incident had no snapshot), the three activities of the outage (the boot with its time), and the number of service events
  in the lookback as stored.
- **Notes**: the rules in one sentence each, the limits that were hit, the number of program entries not tracked, the caps of the new
  listeners (`Only the latest 20 findings after an agent start are listed; <n> earlier ones are left out.`, the same for the findings of the agent, and
  `Only the first 64 agent IDs are listed; <n> more are left out.`, with German texts) and a different number of incidents between the two passes.

Formats, the same in both languages except the decimal separator:

- Times: `YYYY-MM-DD HH:MM UTC` (24-hour clock, cut to the minute).
- Memory: MiB (1,048,576 bytes), rounded to a whole number, a half away from zero; growth with a leading `+`.
- Shares: percent with one decimal, a half away from zero (`57.1 %` in English, `57,1 %` in German).
- Counts and numbers: ASCII digits, no thousands separator.
- Lines end with a line feed; the text is UTF-8.

### Language

`en` (English, the default) or `de` (German), chosen by `vandoxd report -lang`. The language changes only the labels and the decimal
separator, never the rules, the numbers or the external text.

### External text in the report

Victim commands and program names (also of backup and panel programs), and the owner commands of the listeners and the agent IDs of the new
listeners section, are external text (security area 12 of `.squad/project.md`).
The report writes them **only as Markdown code spans**, so no character of them is read as Markdown, HTML, an entity, a link or an
autolink:

- a backslash becomes two backslashes; a line feed, carriage return and tab become `\n`, `\r`, `\t`; every other character of the
  categories control (Cc), format (Cf, such as U+202E), line and paragraph separator, private use and unassigned, and U+FFFD, becomes
  `\u` and four lower-case hex digits (`\U` and eight above U+FFFF) — the escapes of the terminal output of `vandoxd import` and
  `vandoxd incidents`, without the quotes;
- the code span's fence is one backtick longer than the longest run of backticks in the escaped text, and the text is padded with
  one space on each side (which Markdown removes again); a text of spaces only has its spaces written as `\u0020`;
- an empty text is written as `—`, not as a code span.

The agent ID stands inside a sentence of the listener section (and in the head item), never in a heading or a table cell; the composer splits the
text at the place of the agent ID and puts the code span between the parts. A command that holds Markdown, HTML, a backtick run or U+202E is
therefore literal text on the page and in the download.

The report's own words, numbers and times are not external. Backslash escapes do not apply inside a code span, so a literal
backslash sequence in a name (the six characters `\u202e`) is rendered with its backslash doubled (`\\u202e`) and stays
distinguishable from the escape of a real U+202E (`\u202e`).

### Limits of the report

- **Memory** does not grow with the number of lines, snapshots or incidents beyond fixed caps: one page of up to 1,000 log lines and
  one signature detector (as *Limits of incidents*), one process snapshot at a time (the largest single read is the same as there),
  one incident at a time (up to 8 MiB of JSON), and kept across the run: at most **1,000,000 snapshot points** (time, panel memory,
  count of snapshots with a backup program: 24 bytes each, so about 24 MB kept and about 36 MB while the list grows by doubling,
  plus a sorted copy of the panel memory of 8 MB for the threshold and, while the rules are evaluated, 4 MB of counts),
  **10,000 boots** (80 KB), **100,000 runs** of consecutive observed hours (about 1.6 MB), **4,096 programs** (their names, short
  texts of at most 1,024 bytes, about 8 MiB at most, plus about 80 bytes of counters each), and 24 hour counts.
  The incidents add only counters. The listener section keeps the head of at most 64 agent IDs (under 64 KB) and at most 20 findings after an agent
  start and 20 found by the agent, each with at most 64 listeners of a command of up to 1,024 UTF-8 bytes: a finding is at most about 140 KB, so the
  section is at most about 5.5 MB in the run (on the page, where every command also carries its runs, up to about 16 MB per circuit, [Web UI](web-ui.md), *Limits and errors*). With all caps full the report keeps about 60 MB across the run, plus the 5.5 MB of the listener section, plus the one log page, process snapshot or incident it is reading.
- **Over a cap** the statements that need the data are unknown: more snapshots make backup and panel unknown, more boots the boot,
  more runs of observed hours backup, boot and panel (their base rate). The time of day counts every observed hour and has no cap.
  The notes name the cap that was hit. An outage's own activity follows the same rule: past a cap of the data it needs, a yes stays
  a yes and a no becomes unknown.
- **Time** grows with all stored lines, snapshots and incidents (the incidents are read twice, the base rate evaluates every observed
  hour). The command runs off any request path. The listener section adds three reads of the narrow derived tables and decodes no payload: over
  a database with one more year of connection snapshots (one agent, 525,600 marks written through `SqliteStore.WriteBatchAsync` in batches of 1,440,
  100 agent starts, 10,000 snapshots with agent findings, 762 MiB) the counts take 101 to 155 ms, the findings after an agent start 9 ms
  (first read, then 1 to 2 ms) and the findings of the agent 3 ms (then 1 to 2 ms), and all three together 136 ms on their first run, on the
  development host in a Debug build, far below the 5 seconds the section may add ([BENCHMARKS.md](../BENCHMARKS.md), *Listener reads*).
- **Forged data shifts the result**: forged kills make forged outages (*Limits of incidents*), and forged boot lines, `pzstd` or
  `sw-engi*` processes shift the activities and their rates; forged process names can take the 4,096 program places first.
- A cluster of outages that straddles an hour boundary is split between two hours of the day.
- An activity of an outage near the start of the snapshots, or under memory pressure when the legacy log missed its samples, is
  unknown rather than no.
- The incidents are those `vandoxd incidents` last stored. The report cannot tell that log lines were imported after that run
  (the incidents carry the time they were saved, the lines the time they were logged), so `vandoxd incidents` is run again after every
  import.
- A run reads every stored log line (one query per page of 1,000 lines) and every stored process snapshot (one query per snapshot), so on a large archive it can
  take minutes. Measured on the development host (4 vCPU Intel Xeon 2.10 GHz, .NET 10) over a database of 5,000,000 log lines (1.6 GB), 43,200 process
  snapshots and 64 incidents: 56 to 60 seconds in a Release build (two runs through the gated readers of the web UI) and 67 seconds for `vandoxd report`
  from a Debug build. The parts that take no token and so cannot be interrupted are short: after the last record the analysis seals its series and then summarizes
  (`ForensicsAnalysis.Seal` and `Summarize`), 14 ms and 13 ms there and, at the caps (1,000,000 snapshots over 1,000,000 observed hours, fed directly), 0.5 s and 15 ms.
  The reference host writes about four times slower than the development host ([BENCHMARKS.md](../BENCHMARKS.md)), so the same database may take several minutes
  there; the web UI stops a run after 2 minutes, `vandoxd report` has no limit.
- Standard output is UTF-8 without a byte order mark whatever the locale is (`Program.cs` opens it that way).
- On exit 1 (interrupted, store failure) standard output holds an incomplete report, which is to be discarded.

## Nightly report

Planned, not implemented yet.

- The report is to be sent at **06:00**. If the backend host was off at that time, it is sent as soon as the backfill has completed, so it is
  always based on complete data. The backend therefore needs to know when a backfill is complete.
- The backend sends the report through the Telegram bot (see the notification area); writing it may use the optional AI.

## Service inventory

Planned, not implemented yet.

The inventory of running services on the monitored server may suggest services that are not needed. A service is only ever **disabled
in a reversible, documented way** (for example `systemctl disable` or `mask`; packages stay installed). Agents of the hosting provider
are **never** disabled or changed, and the inventory excludes them from every action, including remote actions. Mail is monitored at the
level of the mail services (process state, ports, logs), never individual mail accounts, so no mailbox credentials exist anywhere.

## Related decisions

- [0008](../decisions/0008-deterministic-detection-and-alerting.md) — why detection is deterministic and AI only writes the report.
- [0020](../decisions/0020-analysis-before-alerting-forensics-release.md) — why the first release is the forensics release.
- [0022](../decisions/0022-backfill-detection-and-live-only-alerts.md) — why the backend classifies live and backfilled data itself.
- [0130](../decisions/0130-live-or-backfilled-at-write-time-gaps-as-stateful-objects-offset-from-caught-up-self-samples.md) — why the classification is made at write time, gaps are stateful rows and the offset comes from caught-up self samples (amends 0022).
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
- [0107](../decisions/0107-incidents-view-summaries-notes-per-incident-number-time-links-to-the-logs.md) — why notes follow an incident through updates and merges and how the web UI shows incidents.
- [0100](../decisions/0100-forensics-report-as-markdown-from-vandoxd-report.md) — why the forensics report is Markdown from a
  sub-command, computed on demand in bounded passes, with external text in code spans, and why the web page comes separately.
- [0108](../decisions/0108-report-page-one-block-model-outage-pages-by-rerun-language-in-the-address.md) — why the Markdown writer and the web page share one block model,
  and how the page lists the outages and limits a run.
- [0101](../decisions/0101-forensics-rules-share-of-outages-against-observed-hours-binomial-tail.md) — why the report's rules compare
  outages with observed system-log hours, judge activities over the lookback, decide by a binomial tail, and how panel use and
  permanent and transient consumers are defined.
- [0102](../decisions/0102-report-language-from-a-flag-english-default-text-tables-in-code.md) — why the report's language is a
  flag with English as default and its texts are tables in code.
- [0115](../decisions/0115-connection-collector-proc-net-tables-owners-from-fd-regular-and-full-snapshots-bounded-listener-check.md) — why
  the agent's connection snapshots count sockets without an owner and why the connection peak is not filtered by source.
- [0116](../decisions/0116-listeners-across-agent-starts-compared-when-stored-and-shown-in-the-forensics-report.md) — why the agent marks its
  first snapshot after a start, the storage layer compares across the start when it stores the snapshots, and the report shows these findings with
  the agent's own under separate caps.

## Not here

- Telegram delivery and who may receive messages: the notification area.
- How gaps are recorded and how batches are identified: the agent area and the wire format.
- Where records are stored, and the tables of the template statistics: the storage area.
- Storing events: a later change.
- The report page and the download of the web UI: [Web UI](web-ui.md), *Report page*.
- Live incidents from the agent's records: issue #53.

## Implementation

`Vandox.Core.Signatures` (`BuiltInSignatures`, `SignatureCatalog`, `SignatureCatalogReader`, `Signature`, `SignatureField`,
`SignatureFieldType`, `SignatureException`, `SignatureDetector`, `DetectedEvent`, `OpenEvent`, `SignatureKey`, `FieldValue`,
`NumberGuard`, and the catalog `signatures.yaml` embedded in the assembly).
`Vandox.Core.Templates` (`LogTemplateNormalizer`) for the normalization; `Vandox.Storage` (`BatchWriter`, `TemplateTally`,
`TemplateQueries`, `SqliteStore` as `ILogTemplateReader`) for the statistics and the queries; `Vandox.Storage` (`TemplateRebuilder`, `SqliteStore.RebuildTemplatesAsync`) for the template rebuild.
`Vandox.Core.Incidents` (`Incident` and its parts, `KillClusterer`, `KillCluster`, `IncidentBuilder`, `MemoryAnalysis`,
`BackupPrograms`, `IncidentSignatures`, `IncidentLimits`, `IncidentJson`); `Vandox.Storage` (`IIncidentStore`, `IncidentRepository`,
`SqliteStore`); `Vandox.Backend` (`Incidents/IncidentReconstructor`, `Incidents/RecordPager`, `Cli/IncidentsCommand`, `Cli/IncidentSummaryWriter`).
`Vandox.Core.Reports` (`ListenerComparer`, `ListenerComparison`, `ListenerOutcome`, `ListenerFinding`, `ListenerFindingKind`, `ListenerTally`, `ListenerReport` for the new listeners; `ForensicsAnalysis`, `ObservedHours`, `SnapshotSeries`, `ConsumerTracker`, `RecurrenceRules`, `PanelPrograms`,
`ReportLimits`, `ProgramStatistics` and the result types) for the forensics report's analysis; `Vandox.Backend` (`Reports/ForensicsReporter`,
`Reports/IReportWriter`, `Reports/ReportComposer` with the blocks `ReportBlock`, `ReportBlockKind` and `ReportPiece`, `Reports/MarkdownReportWriter`,
`Reports/MarkdownText`, `Reports/ReportText`, `Cli/ReportCommand`) for reading the store and writing the Markdown; the web UI adds `Reports/ReportViewBuilder` and
`ReportDocument` (the blocks of one page of outages), `ReportService` (one run at a time), `GatedIncidentStore` and `GatedListenerHistoryReader` and the page and download described in the Web UI area.
