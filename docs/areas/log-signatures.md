# Log signatures

## Scope

A **signature** recognizes one known kind of event in stored `log_line` records of every source; the **detector** turns a sequence of
records into **events** that list the records they stand for. The catalog of signatures is data embedded in the backend, not code:
adding a signature is one entry in the catalog plus a positive and a negative test, and needs no change to the detector. Nothing is
stored and no record is changed by the detector; the incident reconstruction ([Incidents](incidents.md)) and the forensics report's
boots ([Forensics report](forensics-report.md)) are its callers.

## Signatures

A signature is checked against one line (record `source`, line `program`, `event` and `message`). Every given condition must hold:

- **Origin**: the signature names programs, sources, or both; the condition holds when the line's program is listed **or** the record's
  source is listed. A signature that names neither has no origin condition.
- **Event**: the line's `event` is one of the listed events; no list means no condition.
- **Message**: the pattern matches the message (first match only); no pattern means no condition.

Comparisons are ordinal and case-sensitive. A signature needs an event or a message, so none matches every line.

### Patterns and the catalog

- Patterns run on a non-backtracking regular expression engine (time linear in the message length) with the options multiline,
  culture-invariant and explicit capture (only named groups capture). Constructs such an engine cannot run (lookahead, lookbehind, atomic
  groups, backreferences, conditionals) are refused when the catalog is loaded, as is every other rule break of a catalog: unknown keys,
  anchors, aliases and tags, a field that is no named group, a named group that is no field, and the limits on lengths and counts. The error
  names the catalog and the line but never the document text.
- Patterns write `[0-9]`, never the Unicode digit class, and exclude the line feed from character classes.
- A signature that has no event condition and reads a message that can hold text of a remote party (web server lines, mail addresses, user
  names) — `disk_full`, `mariadb_too_many_connections`, `php_too_many_connections`, `php_disk_full` and `nginx_disk_full` — carries an origin
  condition and anchors its pattern with `\A` at text the server itself writes before any external text, never with `^` (which matches after
  every line feed of a multi-line message).

A line may match several signatures; it then yields one event each, in catalog order.

### Built-in signatures

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

- `disk_full` reads only MariaDB's own error messages, and `mariadb_too_many_connections` only the MySQL client errors of Postfix and
  Dovecot. The web server and PHP have a signature each: `nginx_disk_full` reads nginx's failed writes (`pwrite()`, `write()`, `pwritev()`,
  `writev()` at `crit` or `alert`), `php_disk_full` PHP's `Notice` about a failed write and `php_too_many_connections` the `mysqli` and PDO
  errors above, as nginx, `sw-cp-server` (nginx's format) and Apache (`mod_proxy_fcgi`, `mod_fcgid`, PHP's Apache module) log them. The
  formats were checked against the sources of nginx 1.18.0, Apache httpd 2.4.52, `mod_fcgid` and PHP 7.4 and 8.3, not against logs of the
  server.
- The signatures read the agent's live records of these sources (`nginx-error`, `sw-cp-server-error`, `apache-error`) as they read the
  imported ones: the agent's mapping gives the same source, priority and message ([Agent log shipping from files](agent-log-shipping-files.md),
  *Error log lines*), and the golden records of the import pin it.
- Only the **first PHP message** after the server's prefix is read, so an error that follows an earlier PHP message of the same request is
  missed. An Apache line without a module name (`[:warn]`) is not read, and PHP's write error is read only when ` in /<script path>` follows
  the error text, so a write error without a script (origins `Unknown`, `PHP Request Shutdown`, `in Unknown on line 0`, e.g. a session or
  output flush at shutdown) is not recognized.
- Not read at all: the Plesk panel log (its format is unverified), Apache's own status texts (`(28)No space left on device:` is also printed
  for exhausted semaphores), Apache 2.2, a custom `ErrorLogFormat`, nginx's `open()` and `mkdir()` (they report requested paths) and PHP's
  `Only 0 of N bytes written`. One full disk that MariaDB and nginx both log gives one event per signature.
- On systemd 249 (Ubuntu 22.04) `Started` and `Stopped` name a unit by its description, and the journal reader does not keep the unit
  name, so `unit` is present only where systemd prints `<unit> - <description>`.

## Fields

A field is the text of a named group of the pattern, and the group name is the field name. A field has one of three types:

- `text`: the captured text, cut at a character boundary to at most 1,024 UTF-8 bytes.
- `integer`: an optional minus sign and 1 to 19 ASCII digits, in the range of a 64-bit signed integer.
- `kibibytes`: 1 to 19 ASCII digits without a sign that give a number of bytes (the value times 1,024) within a 64-bit signed integer.

A number is accepted only when the **whole** captured text is of that form, checked by an ordinal scan and range-checked before any
conversion (no parsing routine sees other text): a plus sign, spaces, a line feed or NUL, a thousands separator, a fraction, an exponent, a
hexadecimal prefix, a minus sign (for `kibibytes`) or a digit of another script all make the field absent. A field whose group did not take
part, matched the empty string, or did not convert is **absent** (it is among neither the event's texts nor its numbers); the event is still
detected.

## Events and merging

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

## Limits and errors

- An event is a **classification, not proof of origin**. Any local process can log as `kernel`, `systemd`, `mariadbd`, `postfix/*` or
  `dovecot` (the journal's `_TRANSPORT` is not kept), and a client of MariaDB can forge a whole MariaDB error log entry through a
  failed login's user name ([MariaDB error log parser](log-parsers-mariadb.md)), which reaches `disk_full` and the `mariadb_*` signatures.
  The events list their record IDs so a consumer can weigh them. Live lines are classified by the agent ([Agent log
  shipping](agent-log-shipping.md), *Classification*), so forged `postfix/*`, `dovecot`, and `mariadbd` and `kernel` lines of the system
  files make live events too, which the alert rules (#56, #61) must weigh. A line of a user journal file gets the mail classification only
  and never joins a server entry.
- `postfix_fatal` and `dovecot_fatal` cover both the fatal and the panic event and have no field, so a fatal error and a panic of one
  daemon within 60 seconds are **one event**; nothing in the event tells which of the two it was, the linked lines and their `event` do.
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
- The **text fields are external text** (the victim command, unit, description, result and kernel version are captures of log text):
  they may hold control characters (the built-in patterns exclude the line feed from them), and whoever shows them escapes them like any
  log line (security area 12, *Logging and display of external data*, of `.squad/project.md`).

## Related decisions

- [0094](../decisions/0094-log-signatures-embedded-yaml-linear-patterns-on-stored-lines.md) — why signatures are an embedded YAML catalog of
  linear-time patterns on stored lines, how copies are counted and how remote text is kept out.

## Not here

- The `event` values and the grouping of multi-line reports the signatures read: [Log import](log-import.md) and its parsers
  ([System and mail log parsers](log-parsers-system.md), [MariaDB error log parser](log-parsers-mariadb.md), [Plesk and web server error log
  parsers](log-parsers-web.md)); the agent's live classification: [Agent log shipping](agent-log-shipping.md).
- What is built from the events: [Incidents](incidents.md), [Forensics report](forensics-report.md).
- Grouping lines by their message shape: [Log templates](log-templates.md).
- Live-only alerts: [Detection, alerts and reports](detection-and-reports.md).

## Implementation

`Vandox.Core.Signatures` (`BuiltInSignatures`, `SignatureCatalog`, `SignatureCatalogReader`, `Signature`, `SignatureField`,
`SignatureFieldType`, `SignatureException`, `SignatureDetector`, `DetectedEvent` with its `Texts` and `Numbers`, `OpenEvent`,
`SignatureKey`, `FieldValue`, `NumberGuard`, and the catalog `signatures.yaml` embedded in the assembly).
