# Spec: Ship the Plesk and web server error logs from the agent

Source: Issue #289
Status: Draft (escalation on file access and formats answered by the Product Manager)

## Problem

`vandox-agent` ships the journal and files in `syslog` format live (#37, [Agent](../../docs/areas/agent.md), *Log shipping*).
On the monitored server (Ubuntu 22.04, Plesk) the rsyslog files are copies of the journal, so the logs that exist only as files
reach the backend only through a later `vandoxd import`: the Plesk panel log and the error logs of `sw-cp-server`, nginx and
Apache. During an outage these logs hold the panel's and the web servers' errors, and the signatures `php_too_many_connections`,
`php_disk_full` and `nginx_disk_full` ([Detection, alerts and reports](../../docs/areas/detection-and-reports.md), *Log
signatures*) read them only by their source types (`nginx-error`, `sw-cp-server-error`, `apache-error`), which no live record
carries today. The import already parses these logs (`ErrorLogParser`, [Log import](../../docs/areas/log-import.md), *Plesk and
web server error logs*, record [0092](../../docs/decisions/0092-error-logs-by-content-or-configured-path-unreadable-lines-kept-raw.md));
the agent does not reuse the .NET parsers (record 0073) and needs the same mapping of its own.

## Scenarios

- S1: Given `log_shipping.files` holds `{path: /var/log/nginx/error.log, format: nginx-error}`, when nginx appends
  `2026/10/09 03:12:14 [error] 1021#1021: *15 FastCGI sent in stderr: ...`, then within a collector run the backend stores a
  `log_line` record with source `nginx-error`, `log` `/var/log/nginx/error.log`, empty `host` and `program`, `pid` 0, priority 3,
  no event, `message` `[error] 1021#1021: *15 FastCGI sent in stderr: ...` (the text after the time stamp and one space) and the
  capture time 03:12:14 local time in the zone of `/etc/localtime`, as UTC — the same record the import gives for the line,
  apart from `log`.
- S2: Given the formats `plesk-panel` (`/var/log/plesk/panel.log`), `sw-cp-server-error` (`/var/log/sw-cp-server/error_log`) and
  `apache-error` (`/var/log/apache2/error.log`), when a line of the format is appended, then the record has that source and the
  import's level, priority, message and time, the fraction of a Plesk or Apache time stamp included. `sw-cp-server-error` reads
  nginx's line format and differs from `nginx-error` only in its source.
- S3: Given a line that is not of the configured format — a line of PHP output that nginx copied from FastCGI's standard error
  after a line feed, a stack-trace line of the Plesk panel log, an Apache start-up message without a time stamp — when it is
  read, then it is shipped **raw**: the whole line as `message`, no priority, empty `host` and `program`, `pid` 0 and the capture
  time of the line read from the file before it, as the import keeps such a line.
- S4: Given a line of the format whose time does not exist (`2026/02/30 ...`, hour 24) or lies outside the storable range, when it
  is read, then it is shipped raw as in S3.
- S5: Given nginx logs `[crit] ... pwrite() "..." failed (28: No space left on device)`, or PHP's *Too many connections* or
  `errno=28` message reaches the nginx or Apache error log, when the line is shipped live, then the backend finds the same
  `nginx_disk_full`, `php_too_many_connections` or `php_disk_full` event on the live record as on the imported one.
- S6: Given an error log is rotated by rename (`error.log` to `error.log.1`, then the server reopens its log) or truncated in
  place, or the agent restarts, then reading follows the rotation, resumes at the saved position and records the same gaps as for
  a `syslog` file (*Rotation*, *Rotation while the agent was stopped*, *Truncation*, *First start and changed files*).
- S7: Given `log_shipping.min_priority` (default `info`) and drop rules, when an error log line is mapped, then the filter applies to
  the mapped record as for a `syslog` file: `debug` (`DEBUG`, nginx `debug`, Apache `trace1` to `trace8`) is not shipped at the
  default, a raw line (no priority) always passes the priority filter, and a drop rule with `program` set never matches these lines
  (their program is empty).

## Errors and edge cases

- E1: A `format` that is none of the five names is refused at start-up with the line of the key and the reason `must be one of
  syslog, plesk-panel, sw-cp-server-error, nginx-error, apache-error` (exit code 1), replacing today's `must be syslog`.
- E2: A configured error log the agent's user and groups (`adm`, `systemd-journal`) cannot read is not shipped and logged `log file
  not readable` with `permission denied`, no gap — the existing rule; the agent gets no new right (record 0122, Product Manager's
  answer below). `deploy/agent/README.md` tells the operator how to grant read access by hand.
- E3: A file in another format at a configured path (an Apache error log configured as `nginx-error`) has every non-empty line
  shipped raw under the configured source, as the import does at a configured path. The agent logs nothing per line: no warning for
  a raw line, unlike the import's summary.
- E4: A raw line whose predecessor the agent did not read — the first line after a first start at the end of the file, of a file that
  appeared later, after a truncation or a lost rotation — gets the file's last time (*A file's last time*: the run's start when the
  position was taken without a line), where the import takes the file's modification time. Its record then differs from the import's
  in `captured_at` only.
- E5: A line longer than 16,384 bytes is cut before it is parsed, as the import's line reader cuts it, with `truncated` set; a UTF-8
  byte order mark at offset 0 and a `\r` before the line feed are removed and an empty line is not shipped, as for `syslog` files.
- E6: A time in the hour skipped at the start of daylight saving time is shifted forward; one in the repeated hour takes the earlier
  offset unless that lies more than ten minutes before the previous line of the format of the file (the import's rule). The previous
  instant is kept per file across runs, not across a restart, as for `syslog` files.
- E7: While `/etc/localtime` cannot be read or is no valid zone file, no configured file of any format is read: positions stay, no
  gap, `time zone not readable` once — the existing rule, now for the error log formats too.
- E8: These logs hold client and application text; a line feed in it (FastCGI standard error) can be followed by a forged line of the
  format with any time and level, which is shipped as such and can match a signature, as on import (*Forged lines*).
- E9: The agent reads local times in the zone of `/etc/localtime`, the import in `import.time_zone`; when the two differ, the capture
  times of the same line differ.
- E10: A flood of lines — nginx's multi-line FastCGI output gives many raw lines — is read within the collector's budgets of a run
  (1,000 records, 4 MiB of text, one second) and memory bound, as for `syslog` files.

## Non-functional requirements

- Configuration: no new key. `log_shipping.files[].format` accepts `plesk-panel`, `sw-cp-server-error`, `nginx-error` and
  `apache-error` besides `syslog`; the default of `log_shipping.files` stays `[]`. Existing configurations and the saved state
  (positions) stay valid; a file whose `format` changes keeps its position (positions are matched by path).
- Wire format: version unchanged. The new sources fit the name pattern of `source`; the backend stores and searches them like the
  journal and `syslog` sources.
- Rights and dependencies: no new capability, group, ACL, installer step, dependency or program (Product Manager's answer); files are
  opened through the existing link-free walk.
- Formats: built and tested from the samples in `testdata/logs/`; the Plesk panel format and `sw-cp-server`'s use of nginx's format
  stay unverified against the server (record 0092), and a line in another form is shipped raw.
- Time and memory: the line grammars are hand-written, without regular expressions, linear in the length of a line; the collector's
  24 MiB memory bound and its run budgets are unchanged.
- Logs: no new agent log line; as before, fixed texts only, never a path or a message.
- Parity: for a line read after its predecessor, the agent's record equals the import's record in every field but `log` (the
  configured absolute path) — `source`, `captured_at`, `host`, `program`, `pid`, `priority`, `message`, `truncated`, `event` (empty
  on both sides) — given the same zone.

## Acceptance criteria

- [ ] AC1: For every line of `testdata/logs/plesk-panel.log`, `nginx-error.log` and `apache-error.log`, read by the agent from the
  first line with `min_priority: debug` and the zone the import is given, the agent's record equals the import's record of the line
  except `log`; the nginx sample configured as `sw-cp-server-error` gives the same records with source `sw-cp-server-error` (S1, S2,
  S3, E3).
- [ ] AC2: For the nginx and Apache lines that match `nginx_disk_full`, `php_too_many_connections` and `php_disk_full` on import (the
  three samples match none, see *Open questions*), the agent's records equal the import's except `log`, and the signature catalog
  finds the same events with the same records on them (S5).
- [ ] AC3: A line of the format with a time that does not exist (30 February, hour 24) or outside the storable range is shipped raw
  — whole line, no priority, the previous line's capture time — and so is a non-empty line not of the format; neither is skipped or
  a gap (S3, S4).
- [ ] AC4: Each of `syslog`, `plesk-panel`, `sw-cp-server-error`, `nginx-error` and `apache-error` is accepted; any other value
  (`nginx`, `NGINX-ERROR`, empty) is refused with the key and the reason of E1 (E1).
- [ ] AC5: For at least one error log format the agent's existing file behaviors hold unchanged: rotation by rename, rotation while
  stopped, truncation, first start at the end, a file appearing later, a restart resuming at the saved position, the run budgets, and
  `log file not readable` for an unreadable file (S6, E2, E10).
- [ ] AC6: `min_priority` drops a line of a less severe level and never a raw line, and a drop rule's `log`, `priority` and
  `message` conditions apply to the mapped record of an error log line (S7).
- [ ] AC7: A raw line read before any line of its file (E4's cases) gets the file's last time (S3, E4).
- [ ] AC8: A line over 16,384 bytes gives the import's message and `truncated`; a byte order mark at offset 0 and a `\r` before the
  line feed are removed and an empty line is not shipped (E5).
- [ ] AC9: Two lines of the format in the repeated hour at the end of daylight saving time, in one run and across two runs, get the
  offsets the import gives them; a line in the skipped hour is shifted forward as on import (E6).
- [ ] AC10: While `/etc/localtime` cannot be read, a file of an error log format is not read and keeps its position, without a gap
  (E7).

## Out of scope

- Finding files by pattern or discovering the per-domain logs of Plesk (`/var/www/vhosts/system/<domain>/logs/proxy_error_log`,
  `error_log`): each can be listed as a file with `nginx-error` or `apache-error`, within the limit of 16 files.
- A new right, group membership, ACL or installer step to make a file readable (E2); a file the agent cannot read stays unshipped.
- Preventing forged lines (E8); classification of live lines (`event`, #290); the MariaDB error log and Dovecot's log file as live
  formats (named in the issue as later work).
- Aligning the agent's zone with `import.time_zone` (E9), and removing the duplicates of a line shipped live and imported later
  (*Duplicates*).
- Warnings or counts of raw lines in the agent (E3); the agent ships them and the import's summary is unchanged.
- Any change to the import's `ErrorLogParser` or to the signatures.

## Open questions

- Do the sample lines in `testdata/logs/` match the web signatures, as the issue's second criterion assumes?
  Answered: no. `BuiltInSignaturesErrorLogFixturesYieldNothing` (`tests/Vandox.Core.Tests/BuiltInSignaturesTests.cs`) asserts that
  the three samples give no event; the issue's criterion is empty on them. AC2 uses the signature lines of
  `BuiltInSignaturesWebServerErrorLogsYieldPhpAndNginxEvents` instead; where they live as a shared fixture is for the plan.
- Are the samples real logs of the server, as the issue says?
  Answered: no. They are written from documentation (example.org, documentation addresses 198.51.100.x); record 0092 states that
  the Plesk panel format, its time zone, `sw-cp-server`'s use of nginx's format and Plesk's paths are not verified. For parity with
  the import (the issue's goal) they suffice; real server lines are not available (Product Manager, below), so the formats stay
  unverified.
- Which zone does the agent read the local times in?
  Answered: the zone of `/etc/localtime`, as for `syslog` files (Agent, *Time of a year-less line*); the error log formats carry their
  year, so no year rule applies.
- Does a live record carry an event or a warning for a raw line?
  Answered: no event — the import sets none for these sources (Log import, *Records*). No warning — the agent logs fixed texts per
  change only (Agent, *Logging*).
- How does the agent tell `sw-cp-server-error` from `nginx-error`, when the import decides by path?
  Answered: the operator names the format per file; the agent detects nothing.
- Are the four files readable by the agent's user and groups on the server, at the default paths, and in the documented formats?
  Answered: by the Product Manager (escalation, option A): the existing rights stay — no new right, group or installer step, so the
  change is not tier `security` on this account. A file the agent cannot read is logged `permission denied` and not shipped (E2), and
  `deploy/agent/README.md` documents how the operator grants read access by hand. No real server lines are available: the spec is
  built on the samples in `testdata/logs/`, and the formats of the Plesk panel log and of `sw-cp-server` stay unverified, as record
  0092 states.

## Area documents

- [Agent](../../docs/areas/agent.md), *Log shipping*: the error log formats in *Configured files* and a new part on their lines
  (grammar and mapping by reference to the import, raw lines with the previous line's time, the zone and the repeated-hour rule, no
  warnings); the residual "the error log formats follow in #289" removed; E4's difference from the import named.
- [Configuration and secrets](../../docs/areas/configuration-and-secrets.md), `log_shipping.files`: the five formats and the new
  reason of E1.
- [Wire format](../../docs/areas/wire-format.md), `log_line` producers: the agent's sources include the four error log types.
- [Log import](../../docs/areas/log-import.md), *Duplicates*: live error log records and an import of the same period are stored twice.
- Besides the area documents: `deploy/agent/agent.yaml` (example entries for the four logs and the per-domain logs),
  `deploy/agent/README.md` (*What the log collector reads*: the four logs, that the group `adm` may not read every one of them, and
  how the operator grants read access by hand) and the `log_shipping.files` row of `README.md`.
