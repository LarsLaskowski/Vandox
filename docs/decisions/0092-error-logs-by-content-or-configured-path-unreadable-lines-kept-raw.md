# 0092: Plesk panel, sw-cp-server, nginx and Apache error logs are claimed by a configured path or by content; lines they cannot read are kept as raw records with a warning

- **Status:** Accepted
- **Date:** 2026-10-11
- **Area:** Log import
- **Source:** Issue #19
- **Supersedes:** —

## Context

Issue #19 asks for parsers of the Plesk panel log, the `sw-cp-server` error log and the nginx and Apache error logs,
each yielding time, severity and message, with all paths configurable ("Plesk versions move them") and with unknown
formats imported as raw lines with a warning instead of being dropped. On the monitored server these logs hold the
errors of the panel and the web servers during an outage, for example `sw-cp-server`'s failed connections to
`sw-engine.sock` and PHP-FPM errors behind nginx and Apache.

What the formats are: nginx (1.18.0, `ngx_log_error_core`) writes `YYYY/MM/DD HH:MM:SS [level] pid#tid: ...` in local
time and copies FastCGI standard error unescaped, so PHP output with line feeds leaves lines without a time stamp in
the log. Apache (2.4.52, `do_errorlog_default`; 2.2.34) writes `[Www Mmm DD HH:MM:SS.uuuuuu YYYY] [module:level] ...`
(2.2 without fraction and module) in local time, and startup messages without time or level. `sw-cp-server` is an
nginx build with nginx's format, and the panel log is written by Plesk itself; both are closed source, so their
formats, their time zone and Plesk's default paths are taken from documentation and were not checked.

The import reads copies and archives under any root ([0079](0079-log-parsing-and-import-in-the-backend-without-following-links.md)),
and the parsers so far find their files by content and have no path option
([0088](0088-mariadb-error-log-entries-by-content-and-lifecycle-events-in-log-line.md),
[0091](0091-mail-lines-classified-in-the-syslog-and-journal-parsers.md)): content was enough for them. Here it is not:
the `sw-cp-server` log and nginx's have the same format, and a file in a format the parser does not know has no
content signature at all. Every existing parser skips a line it cannot read, and the registry knew two levels, a
content signature above a name match.

## Options considered

How a file is tied to its source:

1. **By content only, no option (as 0088 and 0091)** — one rule for every parser; but the issue's acceptance
   criterion is dropped, the `sw-cp-server` log is indistinguishable from nginx's, and a log in a changed format is
   "not recognized" as a whole.
2. **The configured absolute path compared with the listed name** — exact, but a copy or archive has any root
   (`import/server-1/nginx/error.log`), so it would rarely match.
3. **Every component of the shorter of the two paths** — matches `var/log/nginx/error.log` and `nginx/error.log`, but
   not a copy root such as `server-2026-10/nginx/error.log`.
4. **The parent directory and the base name, with a rotation suffix `.N` or `-YYYYMMDD`** (chosen) — survives any copy
   root and logrotate's names, and is specific enough for the server's layout (`nginx/error.log`,
   `apache2/error.log`, `sw-cp-server/error_log`, `plesk/panel.log`); a lone `error.log` (a file imported on its own)
   is left to content.
5. **Glob patterns or several paths per source** — would also cover the per-domain logs under
   `/var/www/vhosts/system/<domain>/logs/`, but the strict configuration decoder knows no sequences
   ([0049](0049-strict-configuration-file-schema-and-errors.md)), and those logs are found by content.

How a path ranks against content:

6. **A path match at the level of a name match** — loses to nginx's content match for the `sw-cp-server` log.
7. **Parsers decline content at another parser's path** — every parser would need every other's paths.
8. **A third level above a content signature** (chosen) — the operator's configured path is the strongest statement;
   no existing parser returns it, so their detection does not change.

What happens to a line the parser cannot read:

9. **Skip it with a reason, as the other parsers do** — the issue forbids dropping it.
10. **Join it to the entry before it, as the MariaDB error log does** — nginx's multi-line FastCGI output would stay
    together, but every format would need the bounded-entry logic, and the issue asks for raw lines.
11. **One raw record per line: the whole line, no priority, the time of the record before it, else the file's
    modification time** (chosen) — nothing non-empty is lost, the time stays deterministic for resume and lies in
    the right place in the file's order, and a later change can still group the stored lines.
12. **The time of the import run** — not deterministic, so resume by count would break.

How the operator is warned:

13. **Report the line through `Skip`** — the line is stored, so counting it as skipped would be false.
14. **Mark the record with an `event`** — nobody running the import sees it, and an event names a recognized
    occurrence, not a parse failure.
15. **A separate emitter call counted per file, a summary line and section, and a warning log line** (chosen) — the
    same shape as skipped lines, so the operator sees how many lines of which file were kept raw and why.

What "unknown formats" covers:

16. **Every file no parser claims, imported as raw lines** — a saved `/var/log` holds `wtmp`, `lastlog` and other
    binary files, which would be stored as garbage, and the import's outcome "not recognized" would disappear.
17. **Lines and files of the four sources** (chosen) — a file in an unknown format reaches a parser through its
    configured path.

The message and the record: the text after the time stamp is kept as the message (chosen over the text after the
level, which would drop pid, request ID and module, and over the whole line, which repeats the time); pid, module and
channel stay in the message, and no event is set. The type names carry `-error` (`nginx-error`, `apache-error`,
`sw-cp-server-error`; `plesk-panel`) so the access log parsers of #90 can take `-access`. One parser class serves the
four sources with three line grammars.

## Decision

Options 4, 8, 11, 15 and 17: four parsers `plesk-panel`, `sw-cp-server-error`, `nginx-error` and `apache-error` claim a
file at their configured path (`import.plesk_panel_log`, `import.sw_cp_server_error_log`, `import.nginx_error_log`,
`import.apache_error_log`; parent directory and base name) above any content signature, and the three with a
distinct format also claim a file by its first line. A line they cannot read is stored as a raw record and reported
with a warning. The rules are in [Log import](../areas/log-import.md), *Plesk and web server error logs*, and the
options in [Configuration and secrets](../areas/configuration-and-secrets.md).

## Consequences

- Unlike 0088 and 0091, these parsers have path options; the reason is the shared nginx format and the unknown-format
  case, which content cannot decide. The MariaDB and Dovecot parsers keep their content-only rule.
- A file at a configured path is read by that parser whatever it holds; a misconfigured path turns another log into
  raw lines of the wrong source type. A copy of the `sw-cp-server` log under another parent directory is read as an
  nginx error log.
- Changing a path option between an interrupted import and its resume changes the detection of the file, which then
  fails, as for any changed detection.
- nginx's multi-line FastCGI output produces many raw lines and warnings in a normal log; the summary lists counts
  and the first reasons per file, not every line.
- A raw line before the first readable line, and the first raw lines of the new part of a continued file, get the
  file's modification time, which can lie after the time of the line. The modification time comes from the file system
  or a tar header and can be forged, so it is used only inside the storable range (as the syslog year anchor does);
  otherwise such a line is skipped as having no time, so it is never both reported as kept raw and refused by the
  importer.
- Two path options that match the same file are not refused: the parser registered first wins the file, and the other
  never sees it. Refusing them would need a rule for overlapping rotation suffixes, and the result of the tie is
  deterministic and documented.
- The formats, time zone and paths of the Plesk logs are unverified; a different format is kept as raw lines, and a
  different path is set by the operator.
- These logs carry client and application text: a forged line in the log's format becomes an entry with any time
  and priority, as for the other parsers ([0086](0086-system-log-parsers-generic-syslog-claim-and-grouped-kernel-reports.md)
  option 19, 0088, 0091).
