# Spec: Ship the Plesk and web server error logs from the agent

Source: Issue #289
Status: Approved by Product Manager

## Problem

The agent ships the journal and `syslog` files live (#37, [Agent](../../docs/areas/agent.md), *Log shipping*); the logs that exist
only as files — the Plesk panel log and the `sw-cp-server`, nginx and Apache error logs — reach the backend only by `vandoxd import`. The signatures
`php_too_many_connections`, `php_disk_full` and `nginx_disk_full` ([Detection, alerts and reports](../../docs/areas/detection-and-reports.md))
read only the sources `nginx-error`, `sw-cp-server-error` and `apache-error`, which no live record carries. The import parses these
logs (`ErrorLogParser`, [Log import](../../docs/areas/log-import.md), *Plesk and web server error logs*, record
[0092](../../docs/decisions/0092-error-logs-by-content-or-configured-path-unreadable-lines-kept-raw.md)); the agent does not reuse
the .NET parsers (record 0073) and needs the same mapping of its own.

## Scenarios

- S1: Given `log_shipping.files` holds `{path: /var/log/nginx/error.log, format: nginx-error}`, when nginx appends
  `2026/10/09 03:12:14 [error] 1021#1021: *15 FastCGI sent in stderr: ...`, then within a collector run the backend stores a
  `log_line` record with source `nginx-error`, `log` `/var/log/nginx/error.log`, empty `host` and `program`, `pid` 0, priority 3,
  no event, `message` `[error] 1021#1021: *15 FastCGI sent in stderr: ...` (the text after the time stamp and one space) and the
  capture time 03:12:14 in the zone of `/etc/localtime`, as UTC — the import's record for the line, apart from `log`.
- S2: Given the formats `plesk-panel` (`/var/log/plesk/panel.log`), `sw-cp-server-error` (`/var/log/sw-cp-server/error_log`) and
  `apache-error` (`/var/log/apache2/error.log`), when a line of the format is appended, then the record has that source and the
  import's priority, message and time, the fraction of a Plesk or Apache time stamp included. `sw-cp-server-error` reads nginx's
  line format and differs from `nginx-error` only in its source.
- S3: Given a line that is not of the configured format — PHP output nginx copied from FastCGI's standard error after a line feed, a
  stack-trace line of the panel log, an Apache start-up message — when it is read, then it is shipped **raw**: the whole line as
  `message`, no priority, empty `host` and `program`, `pid` 0, the capture time of the line read from the file before it.
- S4: Given a line of the format whose time does not exist (`2026/02/30 ...`, hour 24) or lies outside the storable range, then it is
  shipped raw as in S3.
- S5: Given nginx logs `[crit] ... pwrite() "..." failed (28: No space left on device)`, or PHP's *Too many connections* or
  `errno=28` message reaches the nginx or Apache error log, when the line is shipped live, then the backend finds the same
  `nginx_disk_full`, `php_too_many_connections` or `php_disk_full` event on the live record as on the imported one.
- S6: Given a rotation, a truncation or an agent restart, then positions, rotation and gaps work as for a `syslog` file.
- S7: Given `log_shipping.min_priority` (default `info`) and drop rules, then they apply to the mapped record as for a `syslog` file:
  `debug` (`DEBUG`, nginx `debug`, Apache `trace1` to `trace8`) is not shipped at the default, a raw line always passes the priority
  filter, and a rule with `program` set never matches these lines (their program is empty).
- S8: Given `log file not readable` (`permission denied`) for an error log, when the operator follows `deploy/agent/README.md`, then
  they check the path for links and for elements that deny search, grant read by hand with commands that never follow a link, and
  learn when a default ACL on the directory is needed, that it widens what every future file there grants, that the returning
  warning is the sign of a lost grant, which client data these logs send, and which statements no run has shown.

## Errors and edge cases

- E1: A `format` that is none of the five names is refused at start-up with the line of the key and the reason `must be one of
  syslog, plesk-panel, sw-cp-server-error, nginx-error, apache-error` (exit code 1), replacing today's `must be syslog`.
- E2: A configured error log the agent's user and groups (`adm`, `systemd-journal`) cannot read is not shipped and logged `log file
  not readable` with `permission denied`, no gap — the existing rule; the agent gets no new right (record 0122).
- E3: A file in another format (an Apache log configured as `nginx-error`) has every line shipped raw, without a warning per line.
- E4: A raw line whose predecessor the agent did not read — the first line after a first start at the end of the file, of a file that
  appeared later, after a truncation or a lost rotation — gets the file's last time (the run's start when the position was taken
  without a line; E14 when a line dated after its run came before), where the import takes the file's modification time. After a rotation whose renamed file was found, the first raw
  line of the new file gets the time of the renamed file's last line. Either record differs from the import's in `captured_at` only.
- E5: Long lines, a byte order mark, `\r\n` and empty lines are handled as for `syslog` files (cut at 16,384 bytes as the import).
- E6: A time in the skipped hour at the start of daylight saving time is shifted forward; one in the repeated hour takes the earlier
  offset unless that lies more than ten minutes before the previous line of the format (the import's rule). The previous instant is
  kept per configured path across runs and rotations, not across a restart; the import starts each file afresh, so the first lines of
  a new file in the repeated hour can get another offset than on import.
- E7: While `/etc/localtime` cannot be read, no configured file is read (positions stay, no gap) — the existing rule.
- E8: Client text with a line feed can forge a line of the format, shipped as such and matched by signatures, as on import.
- E9: The agent reads local times in the zone of `/etc/localtime`, the import in `import.time_zone`; capture times differ by any
  difference, and by the panel log's own zone should it differ (unverified, record 0092).
- E10: A flood of raw lines stays within the run budgets and memory bound.
- E11: A record whose capture time lies more than 10 minutes from its receive time is stored **backfilled** and never alerts
  (*Live data and backfill*, record 0130): a raw line that takes an hours-old predecessor's time (an Apache start-up message after a
  quiet night) and every panel line under E9's zone difference. Accepted; named in the Agent area.
- E12: Invalid UTF-8: the agent replaces each invalid byte with U+FFFD, the import each maximal invalid sequence, so `message`, and
  near the 16,384-byte cut `truncated`, can differ for such a line — an existing difference of every agent file format, named in the
  Agent area.
- E14: A line dated after the start of the run that reads it (a forged year such as `2262/04/11`, a clock stepped back) keeps its
  own `captured_at`, but leaves the file's last time and the run's latest time at their previous value (an earlier line's time or the
  start of the run that took the position); a raw line after it takes that time. So the gaps of a truncation, a lost rotation and a
  given-up span start at or before every lost line, also after a run stopped on its budget, and end at or before the run's start.
  This fixes `syslog` files too (RFC 3339 years, #37); a future time saved earlier stays until a line dated before its run.
- E13: When a rotation leaves a new file the agent cannot read, the rest of the renamed file waits with it under the standing
  `log file not readable` warning and is read once the path opens again; a restart in between loses the renamed file's descriptor, and
  a renamed file no longer found then becomes a gap — the existing rule of #37.

## Non-functional requirements

- Configuration: no new key. `log_shipping.files[].format` accepts `plesk-panel`, `sw-cp-server-error`, `nginx-error` and
  `apache-error` besides `syslog`; the default stays `[]`. Existing configurations and saved positions stay valid (matched by path).
- Wire format unchanged.
- Rights and dependencies: no new capability, group, ACL, installer step, dependency or program (Product Manager); files are opened
  through the existing link-free walk.
- Formats: built and tested from the samples in `testdata/logs/`; the Plesk panel format, its zone and `sw-cp-server`'s use of
  nginx's format stay unverified against the server (record 0092); a line in another form is shipped raw.
- Target host (Product Manager: no access to the server, no real lines): the owner, group and mode of the four files and their
  directories, how each is rotated (logrotate `create` with which mode, `copytruncate`, Plesk's own rotation), whether `setfacl`
  (package `acl`) is installed and whether `plesk repair fs` resets permissions are unknown; the README marks each as unverified.
- Hand-written grammars, linear in the line length; memory bound, run budgets and agent log lines unchanged.
- Parity: for a line of valid UTF-8 read after its predecessor in the same file, the agent's record equals the import's in every
  field but `log` (the configured absolute path) given the same zone; E4, E6, E9, E12 and E14 name the exceptions.

## Acceptance criteria

- [ ] AC1: For every line of `testdata/logs/plesk-panel.log`, `nginx-error.log` and `apache-error.log`, read from the first line with
  `min_priority: debug` and the import's zone, the agent's record equals the import's except `log`, compared with golden records the
  C# import writes and the Go tests read; the nginx sample as `sw-cp-server-error` gives the same records with that source (S1, S2,
  S3, E3).
- [ ] AC2: For nginx and Apache lines that match `nginx_disk_full`, `php_too_many_connections` and `php_disk_full` on import, kept in
  a fixture under `testdata/logs/` that both languages read, the agent's records equal the import's golden records except `log`, and
  the signature catalog finds the same events on them (S5).
- [ ] AC3: A line of the format with a time that does not exist (30 February, hour 24) or outside the storable range, and a
  non-empty line not of the format, are shipped raw — whole line, no priority, the previous line's capture time — never skipped or a
  gap (S3, S4).
- [ ] AC4: `syslog`, `plesk-panel`, `sw-cp-server-error`, `nginx-error` and `apache-error` are accepted; any other value (`nginx`,
  `NGINX-ERROR`, empty) is refused with the key and the reason of E1 (E1).
- [ ] AC5: For at least one error log format the existing file behaviors hold: rotation by rename, rotation while stopped,
  truncation, first start at the end, a file appearing later, a restart resuming at the saved position, the run budgets, `log file
  not readable` for an unreadable file, and an unreadable new file after a rotation (S6, E2, E10, E13).
- [ ] AC6: `min_priority` drops a line of a less severe level and never a raw line; a drop rule's `log`, `priority` and `message`
  conditions apply to the mapped record (S7).
- [ ] AC7: A raw line read before any line of its file gets the file's last time, and the first raw line of a new file after a found
  rotation the renamed file's last time (E4).
- [ ] AC8: A line over 16,384 bytes gives the import's message and `truncated`; a byte order mark at offset 0 and a `\r` before the
  line feed are removed and an empty line is not shipped (E5).
- [ ] AC9: Lines of the format in the repeated hour at the end of daylight saving time, in one run and across two runs of one file,
  and a line in the skipped hour get the import's instants (golden records); across a rotation the instant carries over (E6).
- [ ] AC10: While `/etc/localtime` cannot be read, a file of an error log format is not read and keeps its position, without a gap
  (E7).
- [ ] AC11: `deploy/agent/README.md` holds the checks and grant commands of S8 as the plan fixes them after the dry run, the default
  ACL's reach, the returning warning, the data sent, and the unverified marks of *Target host*; the Agent area names E11 and E12 (S8,
  E11, E12).
- [ ] AC12: After a line dated in 2262, the gaps of a truncation, a lost rotation and a given-up span start at or before the time of
  the first line they lose, also when the run that read it stopped on its budget right after it, and end at or before the run's
  start, for an error log and for an RFC 3339 `syslog` line; the line keeps its own `captured_at` and a raw line after it gets the
  file's time from before it (E14).

## Out of scope

- Finding files by pattern or discovering Plesk's per-domain logs (`/var/www/vhosts/system/<domain>/logs/proxy_error_log`,
  `error_log`): each can be listed with `nginx-error` or `apache-error`, within the limit of 16 files.
- A new right, group, installer step or automatic grant (E2); reading the renamed file's rest while the new file is unreadable (E13).
- Preventing forged lines (E8); live classification (`event`, #290); the MariaDB error log and Dovecot's log file as live formats.
- Aligning the agent's zone with `import.time_zone` (E9); duplicates of a line shipped live and imported later (*Duplicates*).
- Warnings or counts of raw lines in the agent (E3); any change to `ErrorLogParser` or to the signatures.

## Open questions

- Do the samples in `testdata/logs/` match the web signatures, as the issue assumes?
  Answered: no (`BuiltInSignaturesErrorLogFixturesYieldNothing`); AC2 uses the lines of
  `BuiltInSignaturesWebServerErrorLogsYieldPhpAndNginxEvents`, moved to a shared fixture.
- Are the samples real server logs, as the issue says?
  Answered: no, written from documentation; enough for parity with the import. No real lines exist (Product Manager).
- Which zone, and does a raw line get an event or warning?
  Answered: `/etc/localtime`; no event, no warning.
- Are the four files readable by the agent on the server?
  Answered: Product Manager, option A: no new right; the README documents the by-hand grant; target-host facts stay unverified.

## Area documents

- [Agent](../../docs/areas/agent.md), *Log shipping*: the error log formats in *Configured files* and a part on their lines (mapping
  by reference to the import, raw lines, zone, repeated hour, no warnings); E4, E6, E11 and E12 named; the residual "the error log
  formats follow in #289" removed.
- [Configuration and secrets](../../docs/areas/configuration-and-secrets.md), `log_shipping.files`: the five formats and E1's reason.
- [Wire format](../../docs/areas/wire-format.md), `log_line` producers: the four error log sources.
- [Log import](../../docs/areas/log-import.md), *Duplicates*: live error log records and an import of the same period are stored twice.
- Besides: `deploy/agent/agent.yaml` (example entries, per-domain logs), `deploy/agent/README.md` (S8, AC11) and the
  `log_shipping.files` row of `README.md`.

## Challenge

1. Major, target-host facts: accepted in part — unknown to the Product Manager, listed under *Target host*, marked unverified in the
   README (S8, AC11); a stand-in dry run replaces the server run; E13 stays the rule of #37.
2. Minor, golden records: accepted (AC1, AC2, AC9; the signature lines move to `testdata/logs/`).
3. Minor, invalid UTF-8: accepted (E12). 4. Minor, old raw-line times: accepted (E11). 5. Minor, rotation differences: accepted (E4, E6).
