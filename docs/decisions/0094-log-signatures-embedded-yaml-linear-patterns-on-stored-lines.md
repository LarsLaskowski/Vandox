# 0094: Log signatures are data: an embedded YAML catalog of linear-time patterns, matched on stored log lines on demand, with the copies of one event merged

- **Status:** Accepted
- **Date:** 2026-10-11
- **Area:** Detection, alerts and reports
- **Source:** Issue #21, Issue #203
- **Supersedes:** —

## Context

Issue #21 asks that known events be recognized across all log sources: OOM kills with victim, anon-RSS and
`oom_score_adj`, MariaDB crash and recovery, `Too many connections`, systemd service start, stop and failure, a full
disk, Postfix and Dovecot fatal errors and the server's boot. Signatures are to be data, not code branches, so that a
new one needs no engine change, and each detected event links to its source lines. Detection is deterministic
([0008](0008-deterministic-detection-and-alerting.md)); outage reconstruction (#23) builds incidents from the events.

What the input is: the parsers of #16 to #20 store `log_line` records; some lines already carry an `event` that a parser
recognized (MariaDB lifecycle, [0088](0088-mariadb-error-log-entries-by-content-and-lifecycle-events-in-log-line.md);
Postfix and Dovecot fatal and panic, [0091](0091-mail-lines-classified-in-the-syslog-and-journal-parsers.md)), and a
kernel OOM report is one record with head and tail
([0086](0086-system-log-parsers-generic-syslog-claim-and-grouped-kernel-reports.md)). Ubuntu's packaged rsyslog
configuration writes every kernel line to `syslog` and `kern.log` (not checked on the server), and a journal export of
the same period holds the same lines again, so one kill can be stored up to three times; 0086 requires that it is not
counted twice. 0086 also leaves to this issue whether the journal reader keeps `_TRANSPORT`, because a program name
`kernel` is no proof of origin. Stored records are never updated (storage, *Writing*), and imports are idempotent per
file content, so a content imported once is never parsed again.

## Options considered

Where detection runs:

1. **In the parsers, at import time, as a further `event`** — no new component; but a content imported before a
   signature existed is never read again, copies in different files are parsed separately and cannot be merged, and
   every parser would carry signature code.
2. **On stored log lines, on demand, the events returned and not stored** (chosen) — every stored line is seen by the
   current catalog, the copies of one event can be merged across files and sources, and records stay untouched; each
   consumer pays a scan of the lines it asks about.
3. **On stored log lines, the events kept in a table of their own** — fast to read; but a schema step, and stored
   events go stale when the catalog or the data changes. #23 stores incidents and decides what it keeps.
4. **As `kernel_event` records of origin `backend`** — reuses the model for OOM kills and boots; but the other event
   types have no record kind, and a record cannot reference other records.

How a signature is written:

5. **Code per signature** — excluded by the issue.
6. **YAML with regular expressions on .NET's non-backtracking engine** (chosen) — the established pattern language,
   with named groups for the fields, alternation and optional parts for kernel and systemd versions; the engine runs
   in time linear in the input, so a hostile line cannot make a match hang, and constructs it cannot run in linear
   time (lookarounds, backreferences, atomic groups, conditionals) are refused when the catalog loads. The parsers stay
   hand-written; the patterns here are trusted data compiled into the binary.
7. **YAML with a hand-written template language** — no regular expression engine; but alternation, optional parts
   and the choice between several occurrences of a delimiter would be new engine code to write and secure.
8. **Signature files the operator configures** — extensible without a release; but a new configuration option, input
   that is no longer trusted and patterns nobody tested. The catalog stays embedded; adding a signature is a change
   to the YAML file and its tests.

How copies of one event are counted:

9. **Every matching line is an event** — counts a kill stored in `syslog`, `kern.log` and the journal three times.
10. **Lines with an identical message are one event** — fails between the journal (no printk time stamp) and syslog,
    and between a MariaDB entry of the error log and the same entry from the journal.
11. **Same signature, compatible host, equal fields, within 60 seconds of the event's first line** (chosen) — the
    fields of an OOM kill (pid, command, anon-RSS, `oom_score_adj`) cannot repeat within a minute, and 60 seconds
    covers the delay between journald's and rsyslog's time stamps of one line, also at boot and under memory
    pressure. The price: a signature without distinguishing fields folds real repeats within a minute into one event,
    whose lines are all still linked and counted.

How far a kernel line is trusted:

12. **Keep `_TRANSPORT` in the journal reader and require the kernel transport** — closes forging in the journal; but
    a new `log_line` field in the wire format, the model and storage, and the syslog files carry no such field, so
    both sources would still be read differently.
13. **Program `kernel` as a classification, the limit documented** (chosen) — one rule for every source, as in 0086,
    0088 and 0091; an event names its source lines, so a reader can see where it came from.

How far message text a remote party controls is trusted (a signature without a parser event, such as a full disk or
`Too many connections`, reads the message alone). The error log parsers keep the whole line from the level on as the
message and set no program, so a web server error line carries the client's referrer, requested path and host, and PHP
output over several lines; mail lines carry addresses:

14. **Match the error text anywhere in any message** — finds every writer of a known text; but any HTTP client
    creates the event with a referrer or a path that quotes it, and ordinary traffic (a search or a referrer naming
    the error) does so by accident, which turns outage reconstruction into a reader of client text. 0091 already keeps
    remote text from the mail events.
15. **The same, accepted and documented, pinned by a test** — no schema change; but the false events above stay.
16. **An origin condition and a pattern anchored at server-written text** (chosen) — the catalog gains a `source`
    condition that, with `program`, forms one origin condition (a listed program or a listed record source suffices:
    MariaDB's lines come with source `mariadb` and no program from the error log, but with program `mariadbd` and
    source `journal` or `syslog` from the journal, so requiring both would split one writer); a signature without a
    parser event that reads a message able to hold remote text names the writers it reads and anchors its pattern
    with `\A` (not `^`, which matches after every line feed) at text the writer prints before any external text.
    Signatures on messages whose text the named program writes without any remote text (`oom_kill` and `boot` from `kernel`, the `service_*`
    signatures from `systemd`) keep the origin condition and may use `^`. Only writers whose format allows that are read: the full
    disk from MariaDB's own messages, `Too many connections` from Postfix's and Dovecot's MySQL client errors. PHP,
    nginx, Apache and Plesk put external text before the error (a requested path, FastCGI output) and need a pattern
    per format checked against their sources, which issue #203 added (options 17 to 23).

How the web server and PHP lines are read (issue #203: PHP's `mysqli` and PDO errors 1040, PHP's write errors with
`errno=28`, nginx's `(28: No space left on device)`). The formats were checked against the programs' sources, not against
logs of the server: nginx 1.18.0 (`ngx_log_error_core`, `ngx_log_errno`, the FastCGI module's stderr line, the file write
functions), Apache httpd 2.4.52 (the default error log format, the escaping of the message, `mod_proxy_fcgi`'s `AH01071`,
`mod_remoteip`), `mod_fcgid` (one log entry per line of the application's standard error) and PHP 7.4 and 8.3 (the log
line `PHP <type>:  <origin>: <text>`, FPM's `PHP message: ` prefix and `; ` separator, `mysqlnd`'s SQL state of error
1040, the stream layer's write error, the uncaught exception text, the module name of the Apache handler). The message of
these lines starts with the level (*Log import*, *Plesk and web server error logs*), which the server writes:

17. **Extend `disk_full` and `mariadb_too_many_connections`** with the new sources and alternatives — one event per
    condition across writers; but a signature has one origin list and one pattern, so every alternative runs on every
    listed writer: MariaDB's `\A(?:[^ \n]+: )?Disk full \(` would read Apache's raw lines, among them the standard error
    of programs PHP starts (`prog: <name the client chose>: …`), a route to the event without any line feed that the
    MariaDB lines alone do not open. Giving a signature several pairs of origin and pattern would fix that, but is a
    change of the catalog format and the detector, which a new writer should not need.
18. **One signature per writer and condition** (chosen): `php_too_many_connections` and `php_disk_full` on the nginx,
    `sw-cp-server` and Apache error logs, `nginx_disk_full` on the nginx and `sw-cp-server` error logs, each pattern
    anchored at the prefix its writers print. Each origin list meets only the patterns written for it; the price is that
    one full disk seen by MariaDB and nginx is two events, which the incident timeline shows side by side.
19. **Anchor the PHP error anywhere after the server's prefix, or after an earlier PHP message** (`; PHP message: ` in
    FPM's output, a line of `mod_fcgid`) — finds the error when the page logged a notice first; but an earlier PHP message
    can quote request data (an undefined array key, an exception message), so the client would control the text before
    the error again. Rejected: only the **first** PHP message after the server's prefix is read (the event is missed when
    another message comes first).
20. **Every nginx call that reports a path with `(28: …)`**, including `open()` and `mkdir()` — finds a full disk when
    a temporary file cannot even be created; but `open()` reports the requested path, and a request for
    `/x" failed (28: No space left on device)` puts the trigger text after a quote that closes the path. Rejected: only the
    write calls (`pwrite()`, `write()`, `pwritev()`, `writev()`, also `write() to "…"` of the access log) at the levels
    nginx gives them (`crit`, `alert`) are read; a request path reaches them only through WebDAV `COPY` or an access log
    path built from variables, and then only when the write fails with another error.
21. **Apache's own `(28)No space left on device:` status** — the same text; but Apache prints it also when a semaphore
    set cannot be created (the system's semaphore limit, not the disk), so it would report a full disk that is none.
    Not read.
22. **The Plesk panel log** — Plesk's own PHP errors; but its format is not verified (record 0092) and its messages hold
    login names and request paths. Not read; the panel's FastCGI errors reach the `sw-cp-server` error log in nginx's
    format, which is read.
23. **Any PHP origin before the error text** (`<function>(): `) — covers frameworks and future wrappers; but PHP prints
    the origin as `function(<params>)`, and the stream functions put the path or URL the script passed into the params
    (`getimagesize(<client text>): Failed to open stream`), so a client could supply `): (HY000/1040): Too many
    connections` in one line. Rejected for the 1040 warning: only the functions that raise it (`mysqli_connect`,
    `mysqli_real_connect`, `mysqli::__construct`, `mysqli::connect`, `mysqli::real_connect`, `mysqli::mysqli`) are read.
    The write error keeps any origin without space or parenthesis, because it is raised by many stream functions; but
    notices can carry params too (exif puts the base name of the image's file, which a client may have chosen, behind
    its origin also outside debug builds), so the write error is read only when ` in /` follows the error text directly:
    a real line continues there with the script's absolute path, text in params is followed by `): …`, and a base name
    holds no `/`. A write error without a script (`in Unknown on line 0`, origins `Unknown` and `PHP Request Shutdown`)
    is therefore not read, since a file name could supply that text. An Apache line without a module name is not read
    either, since any module without a declared name logs that way.

## Decision

Options 2, 6, 11, 13, 16 and 18, with the narrowing that options 19, 20 and 23 state: signatures are an embedded YAML catalog whose message patterns run on
.NET's non-backtracking regular expression engine; a detector matches them on stored `log_line` records in capture
order and returns events that list their record IDs, merging the copies of one event; nothing is stored and records
are never changed. Every signature without a parser event is bound to the writers it names; one whose message can
hold remote text (`disk_full`, `mariadb_too_many_connections`, `php_too_many_connections`, `php_disk_full`,
`nginx_disk_full`) is also anchored with `\A` at their server-written text, so text a remote party puts into the fields
a server adds or into PHP's own messages cannot trigger it; an application that logs request data at the start of a
message, or output a writer splits, still can (*Consequences*). The rules are in [Detection, alerts and reports](../areas/detection-and-reports.md) (*Log
signatures*).

## Consequences

- Adding a signature is an entry in the catalog plus its positive and negative tests; the detector does not change.
- A detected event is a classification of text, not proof that the program it names wrote the line: any local process
  can log as `kernel`, `systemd`, `mariadbd` or a mail daemon. Events carry their record IDs so that consumers can show
  and weigh the lines.
- Nothing calls the detector yet; outage reconstruction (#23) reads the stored lines in capture order and calls it.
  Reading more than one page of lines that share a capture time needs a keyset on the record ID, which the record
  reader does not offer yet (#23's decision).
- On systemd 249 (Ubuntu 22.04) a started or stopped unit is named by its description, not its unit name, and the
  journal reader keeps no `UNIT` field, so `Started`/`Stopped` lines of scopes and timers match the service signatures
  too; newer systemd versions print the unit name, which the signatures read when present.
- The OOM kill line of `vm.oom_kill_allocating_task` is recognized by the signature, and the kernel report grouper ends a
  report at it like at the other kill lines (#202).
- A full disk is recognized in MariaDB's messages, nginx's file writes and PHP's write errors, and `Too many connections`
  in Postfix's and Dovecot's MySQL client errors and PHP's `mysqli` and PDO errors (#203); the Plesk panel log, Apache's
  own status texts, a custom Apache `ErrorLogFormat`, Apache 2.2, a PHP error after an earlier PHP message of the same
  request and a PHP write error without a script file are not read. A client of MariaDB can still forge a whole
  MariaDB entry through a failed login's user name ([0088](0088-mariadb-error-log-entries-by-content-and-lifecycle-events-in-log-line.md)), which reaches the MariaDB
  signatures and the full disk; this stays documented, not prevented.
- The anchor holds for one line, not for output that a writer splits. nginx copies a PHP message with its line feeds
  (forged lines, [0092](0092-error-logs-by-content-or-configured-path-unreadable-lines-kept-raw.md)), `mod_fcgid` logs every
  line of PHP's standard error as an entry of its own, and nginx and `mod_proxy_fcgi` log FastCGI standard error in
  pieces of their read buffer, each behind the server's prefix. An HTTP client that makes an application log request data
  in a long or multi-line PHP message can so place the trigger text at the start of an entry and create a PHP event.
- The anchor also does not hold where the application writes the head of the message itself: `trigger_error` logs
  `PHP Warning:  <text>` without an origin, `error_log` without a log file writes its text verbatim behind FPM's
  `PHP message: `, `mod_fcgid` or PHP's Apache module, and programs started from PHP or a CGI write into Apache's error
  log directly, as raw lines whose message is the whole line and needs only the prefix. Such a line is byte-identical to
  one PHP raises, so an application that logs request data at the start of a message lets the client create a PHP
  event. The same holds for a WebDAV `COPY` destination or a variable access log path in a failed nginx write, and for an Apache
  client address that a reverse-proxy module copies unchecked from a forwarded header (`mod_remoteip` stores a parsed
  address). This stays documented, not prevented: the events list their record IDs.
- To revisit: storing events (option 3) once a consumer needs them faster than a scan, or operator-defined signatures
  (option 8), each a decision of its own.
