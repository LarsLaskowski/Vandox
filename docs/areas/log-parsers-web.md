# Plesk and web server error log parsers

## Scope

How the import reads the error logs of Plesk and the web servers: the Plesk panel log, the error log of
`sw-cp-server` and the nginx and Apache error logs, claimed by a configured path or by content, with unreadable lines
kept as raw records. The rules shared by all parsers are in the [Log import](log-import.md) parser contract; the log
signatures of PHP and nginx read these records.

## Sources

Four parsers read these logs. They are one parser with a source type, a path option and a line format each,
registered in this order:

| Source type | Log | Path option (default) | Format | Claimed by content |
| ----------- | --- | --------------------- | ------ | ------------------ |
| `plesk-panel` | Plesk panel log | `import.plesk_panel_log` (`/var/log/plesk/panel.log`) | Plesk panel | yes |
| `sw-cp-server-error` | error log of `sw-cp-server`, Plesk's web server of the panel | `import.sw_cp_server_error_log` (`/var/log/sw-cp-server/error_log`) | nginx | no |
| `nginx-error` | nginx error log | `import.nginx_error_log` (`/var/log/nginx/error.log`) | nginx | yes |
| `apache-error` | Apache error log | `import.apache_error_log` (`/var/log/apache2/error.log`) | Apache | yes |

The defaults are the paths of a current Plesk on Ubuntu as documented by Plesk; they were not checked on the server,
and older Plesk versions wrote the panel log elsewhere (for example `/usr/local/psa/admin/logs/panel.log`), which the
operator sets in the option. The Apache default is the Debian and Ubuntu path; on a RHEL-family server (AlmaLinux,
Rocky Linux, CentOS) Apache writes `/var/log/httpd/error_log`, which the operator sets in `import.apache_error_log`.
How the options are validated is in [Configuration options](configuration-options.md).

## Configured path

- A file is at a configured path when the path has at least a directory and a file name, the file's parent directory
  (the element before the base name in the name the import lists) equals the parent directory of the configured path,
  and the file's base name equals the configured base name, alone or followed by `.` and one or more ASCII digits
  (`error.log.1`) or by `-` and exactly eight ASCII digits (`error.log-20261010`); the `.gz` of a decompressed file is
  removed before. The comparison is ordinal and case-sensitive.
- Only these two elements count, because a copy or an archive has any root: `nginx/error.log`, `var/log/nginx/error.log`
  and `server-1/nginx/error.log` are all at `/var/log/nginx/error.log`, while a file imported on its own (`error.log`,
  no directory) is never at a configured path.
- The configured paths are compared as text and never opened or resolved on the backend host.
- A file at a configured path is claimed with a path match, above every content signature, so it is read by that
  parser whatever it holds; content of another format becomes raw lines (below): an Apache error log at the nginx path
  is read as `nginx-error` raw lines, not as `apache-error`.
- When the paths of two of these parsers match the same file (for example `import.sw_cp_server_error_log` and
  `import.nginx_error_log` both set to `/var/log/nginx/error.log`), the one registered first in the table above wins
  and the other never sees the file; the configuration does not refuse such paths.

## Content

- `plesk-panel`, `nginx-error` and `apache-error` also claim a file by its first line ([Log import](log-import.md),
  *Claim by the first line*): its name is no syslog name and the first line of the head is a line of their format; a
  first line cut by the end of the head counts when its beginning is complete.
- `sw-cp-server-error` has nginx's format and claims a file only by its path: a copy of that log under another parent
  directory is read as `nginx-error`, with `log` keeping its path.
- The per-domain logs of Plesk (`proxy_error_log` in nginx's format and `error_log` in Apache's, under
  `/var/www/vhosts/system/<domain>/logs/`) are found by content.
- The beginnings of the three formats differ from each other and from the first lines the other parsers claim (nginx
  starts with `DDDD/`, the Plesk panel log with `[DDDD-`, Apache with `[` and a weekday), so no first line is claimed by
  content twice. Neither a source type nor a priority proves which program wrote a file ([Log import](log-import.md),
  *The shape is no proof*).

## Lines

A **line of the format** begins with these fields, checked on the decoded line with ordinal comparisons of ASCII
characters (`D` an ASCII digit). The digits of month, day, hour, minute and second are not range-checked here (see
*Time*), and the weekday is not compared with the date.

| Format | Beginning of the line | Levels and priority |
| ------ | --------------------- | ------------------- |
| Plesk panel | `[DDDD-DD-DD DD:DD:DD`, optionally `.` and 1 to 6 digits, `] `, optionally 1 to 10 digits, `:`, 1 to 64 ASCII letters or digits and a space, then the level | `EMERG` 0, `ALERT` 1, `CRIT` 2, `ERR` 3, `WARN` 4, `NOTICE` 5, `INFO` 6, `DEBUG` 7 |
| nginx | `DDDD/DD/DD DD:DD:DD [`, the level, `]` | `emerg` 0, `alert` 1, `crit` 2, `error` 3, `warn` 4, `notice` 5, `info` 6, `debug` 7 |
| Apache | `[`, a weekday `Mon` to `Sun`, a space, a month `Jan` to `Dec`, a space, `DD DD:DD:DD`, optionally `.` and exactly 6 digits, a space, `DDDD`, `] [`, optionally a module of 0 to 64 ASCII letters, digits or `_` followed by `:`, the level, `]` | as nginx, and `trace1` to `trace8` 7 |

The level is followed by a space or the end of the line; levels, weekdays and months are case-sensitive. nginx's
format was checked in nginx 1.18.0 and Apache's in httpd 2.4.52 (`[module:level]`, `[:level]` without a module name)
and 2.2.34 (no fraction, no module); the Plesk panel format is taken from Plesk's documentation (Obsidian writes the
process ID and a request ID before the level, older versions only the level) and is not verified, nor is it verified
that `sw-cp-server` writes nginx's format.

## Records

Every line of the format is one record: source the source type, `host` and `program` empty, `pid` 0, no event,
`priority` from the level, and `message` the text after the time stamp and the one space after it (so the level, a
process or request ID, the module and a client address stay in it). No lines are joined.

## Time

- The time stamp is the server's local time with its year. It is read in `import.time_zone` with the rules of
  [Log import](log-import.md), *Local time*; the instant that decides the repeated hour is that of the previous line of
  the format whose time was resolved (raw lines do not count). The fraction is added to the resolved instant; an
  instant that leaves the storable range only by the fraction is outside it.
- With the option unset, the first line of the format fails the parse ([Log import](log-import.md), *Missing time
  zone*); raw lines before it stay stored.
- That Plesk writes the panel log in the server's local time is not verified.

## Raw lines

- A non-empty line that is not a line of the format, or whose time does not exist (month 13, `2026-02-30`, hour 24) or
  lies outside the storable range, is kept as a **raw record** ([Log import](log-import.md), *Parsing*): the fields of
  *Records*, but no `priority` and the whole line as `message`, cut and marked like a message.
- Its time is the capture time of the record emitted before it in the same parse, else the file's modification time
  when it lies inside the storable range (for the first lines of a file and of the new part of a continued file, so
  such a line can be stored with a time later than it was written). With neither time, the line is skipped as "raw line
  without a time" and gets no warning.
- The parser reports every other raw line with a warning and one of the fixed reasons "not a Plesk panel log line",
  "not an nginx error log line" (also for `sw-cp-server-error`), "not an Apache error log line", "invalid date" and
  "time outside the storable range", and then emits its record.
- The parser's checks keep a raw record's time and message within the rules the importer applies to a record; should
  the importer refuse a raw record for another rule, its line appears among the skipped lines (`record refused: …`) as
  well as in the count of lines kept raw, because the warning comes first, and nothing of it is stored.
- An empty line is skipped as "empty line".
- Raw lines occur in normal logs: nginx copies the standard error of a FastCGI application unescaped, so a PHP message
  with line feeds continues on lines without a time stamp, and Apache writes startup messages and the standard error of
  child processes without one. A file of an unknown format at a configured path becomes raw records only.
- Memory is independent of the input: a parse keeps the time of the last record and of the last line of the format.

## Forged lines (limitation)

These logs hold text of clients and web applications (request lines, host names, the standard error of FastCGI and
CGI programs). Where such text reaches the file raw with a line feed (nginx's FastCGI standard error, the standard
error of an Apache child process), the line after it can have the log's format with any time and level; a PHP message
that echoes request data lets a remote client do this. Any local process that may write to the files can do the same.
A priority is a classification of text, not proof that the web server or Plesk wrote the line. Forged lines reach the
signatures of PHP and nginx ([Log signatures](log-signatures.md)).

## Duplicates

A line the agent ships live from a configured `plesk-panel`, `nginx-error`, `sw-cp-server-error` or `apache-error`
file is stored again when the same period is imported ([Log import](log-import.md), *Per file content*).

## Related decisions

- [0092](../decisions/0092-error-logs-by-content-or-configured-path-unreadable-lines-kept-raw.md) — why the Plesk and web server error logs have path options that rank above content, how a configured path is matched, and why unreadable lines are kept as raw records with a warning.
- [0085](../decisions/0085-syslog-time-zone-from-import-time-zone-with-embedded-tzdb.md) — why local times are read in `import.time_zone`.

## Not here

- The rules shared by every parser (claims, syslog names, record fields, line reader, text limits, storable range,
  local time, missing time zone, raw records): [Log import](log-import.md), *Parser contract*.
- The validation of the four path options: [Configuration options](configuration-options.md).
- The agent's live shipping of these logs: [Agent log shipping](agent-log-shipping.md).
- The other parsers: [System and mail log parsers](log-parsers-system.md),
  [MariaDB error log parser](log-parsers-mariadb.md), [Legacy monitoring log parser](log-parsers-legacy-monitoring.md).

## Implementation

`Vandox.Core.LogParsing`: `ErrorLogParser`, `ErrorLogParseSession`, `ErrorLogLine`, `ErrorLogFormat`, `ErrorLogPaths`,
`LogPath`, `SyslogClock.ResolveLocal`, `Utf8Text`; the options are loaded by `BackendConfigLoader`. A missing time zone
surfaces as `InvalidOperationException` from `ParseAsync`.
