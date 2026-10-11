# Spec: Parsers for Plesk and web server error logs

Status: Draft

Source: Issue #19 (no comments; dependency #15, the import framework, is merged).

## Problem / motivation

Release v0.1.0 is the forensics release: `vandoxd import` reads the saved logs of the production server and
explains past outages (memory exhaustion, the OOM killer, then MariaDB, Plesk and mail going down). The import
reads the journal, syslog, the MariaDB error log and Dovecot's log file, but not the logs where Plesk and the
web servers report their own failures: the Plesk panel log, the error log of `sw-cp-server` (Plesk's own web
server for the panel, an nginx build), and the nginx and Apache error logs. A saved `/var/log` of the server
holds these files, and today they are listed as "no parser recognized the file". Typical evidence of an
outage lives there: `connect() to unix:/var/run/sw-engine.sock failed` in the `sw-cp-server` error log, PHP-FPM
and proxy errors in the nginx and Apache error logs, database errors in the panel log.

## Behavior

- `vandoxd import` reads four more sources and stores every line of them as a `log_line` record with time
  (UTC), severity (priority 0 to 7) and message: the Plesk panel log (source type `plesk-panel`), the
  `sw-cp-server` error log (`sw-cp-server-error`), the nginx error log (`nginx-error`) and the Apache error log
  (`apache-error`). Times are the server's local time and are read in `import.time_zone`, as for the MariaDB
  error log.
- **Paths come from the configuration.** Four new backend options name the server path of each source:
  `import.plesk_panel_log` (default `/var/log/plesk/panel.log`), `import.sw_cp_server_error_log`
  (`/var/log/sw-cp-server/error_log`), `import.nginx_error_log` (`/var/log/nginx/error.log`) and
  `import.apache_error_log` (`/var/log/apache2/error.log`). An operator whose Plesk version writes a log
  elsewhere sets the option. Because the import reads copies and archives under any root, a file is at a
  configured path when its parent directory and its base name (a rotation suffix `.N` or `-YYYYMMDD` allowed)
  equal those of the configured path, so `nginx/error.log.1` of a copied `/var/log` is at
  `/var/log/nginx/error.log`. A file at a configured path is read by that source's parser whatever its
  content. The paths are never opened on the backend host; they are compared as text.
- The Plesk panel log, the nginx error log and the Apache error log are also recognized by their content (the
  first line) under any other name, so per-domain logs (`proxy_error_log`, `error_log` under
  `/var/www/vhosts/system/<domain>/logs/`) and single copied files are read too. The `sw-cp-server` error log
  has the nginx format and is told apart only by its path; a copy under another name is read as an nginx error
  log.
- **Unknown formats are kept as raw lines with a warning.** A non-empty line of these sources that is not in
  the expected format (a continuation line of a multi-line message, a startup line without a time stamp, a
  format a newer version writes, a whole file in an unknown format at a configured path) is stored as a raw
  record: the whole line as message, no priority, the time of the record before it in the file (the file's
  modification time for lines before the first one). The import reports it: the summary counts the lines kept
  raw and lists, per file, their number and the first reasons, and a warning log line names every file that
  has raw lines. Nothing non-empty is dropped, except a raw line for which no time exists at all (no record
  before it and no modification time), which is skipped with a reason.
- Nothing changes for the existing parsers and sources, and imported records stay origin `import` (never
  live, never alerting).

## Acceptance criteria

- [ ] AC1: Each source yields time, severity and message (Plesk panel log, `sw-cp-server` error log, nginx
  error log, Apache error log).
- [ ] AC2: Paths come from configuration (four options with defaults, validated, in the example file and the
  README; a file at a configured path goes to that source's parser).
- [ ] AC3: Unknown formats are kept as raw lines with a warning (stored records, counted and listed in the
  summary, a warning log line per file).

The plan breaks these into testable criteria with test names (`plan.md`).

## Out of scope

- Access logs (issue #90), other Plesk logs (installer, `httpsd_access_log`, mail and DNS logs), PHP-FPM's own
  logs.
- Joining a raw continuation line to the entry before it (one record per line; a raw line keeps its text and the
  time of its entry).
- Process ID, module or channel as record fields (they stay in the message), and events (`event` stays empty;
  signature detection is #21).
- Live shipping of these logs by the agent (#37).

## Open questions

None for the Product Manager. The Plesk panel log's line format and its time zone, and the Plesk default paths,
could not be checked against Plesk (closed source, documentation not reachable from the session); the plan marks
them unverified, and the raw-line fallback keeps every line of a differing format.
