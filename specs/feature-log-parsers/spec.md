# Spec: Parsers for journal export, syslog and kern.log

Status: Draft

Source: Issue #16 (depends on #15, merged). Plan: [plan.md](plan.md), tasks: [tasks.md](tasks.md).

## Problem / motivation

v0.1.0 is the forensics release: Vandox imports the saved logs of the production server and explains past outages.
The known failure is memory exhaustion that ends with the OOM killer terminating MariaDB. The import framework
(`vandoxd import`, #15) exists but ships **no parser**: every file is listed as "no parser recognized the file". The
system logs (the journal and the classic syslog files `syslog` and `kern.log`) are the first logs that must become
structured records, above all the kernel's OOM report.

## Behavior

### What `vandoxd import` recognizes

- **Journal export** (`journalctl -o export > journal.export`, optionally gzip-compressed): recognized by its content,
  whatever the file is called. Text fields (`NAME=value`) and binary fields (name, 64-bit little-endian length, raw
  bytes) are both read, so multi-line and non-UTF-8 values are kept. Binary journal files (`*.journal` from
  `/var/log/journal`) are not this format; they stay "no parser recognized the file", and the README tells the operator
  how to export them (`journalctl -o export --directory=<dir>` or `--file=<file>`).
- **Syslog files**: `syslog`, `kern.log` and every other file written by rsyslog in one of its two file formats, with
  any rotation suffix (`syslog.1`, `syslog.2.gz`, `kern.log-20260301`). The parser for them is generic: it claims a file
  weakly, by name or by the shape of its first line, so a specific parser registered later (mail.log, #18) can take
  over its files.

### What a record holds

Every recognized line (or journal entry) becomes one `log_line` record of origin `import` with:

| Field | Journal export | Syslog file |
| ----- | -------------- | ----------- |
| time (UTC) | `__REALTIME_TIMESTAMP` | the line's time stamp, see below |
| host | `_HOSTNAME` | the host field of the line |
| program | `SYSLOG_IDENTIFIER`, else `_COMM` | the tag before `[pid]:` / `:` |
| PID | `_PID`, else `SYSLOG_PID` | the number in `[pid]` |
| severity | `PRIORITY` (0-7) | the `<PRI>` prefix when the file has one, else none |
| message | `MESSAGE` | the text after the tag |
| log | `journal` | the file's name as the import lists it |

`host` is a new optional field of the `log_line` record, in the backend and in the agent's wire format alike, so that
the agent (#37) and the import agree on the record's fields (golden wire fixture, record 0075).

### Time stamps without a year, and time zones

- Lines in the RFC 3339 form (`2026-03-01T12:00:00.123456+01:00`) carry year and offset; they are converted to UTC.
- Lines in the traditional form (`Mar  1 12:00:00`) have neither. They are read in the **time zone of the monitored
  server**, set by the new backend option `import.time_zone` (an IANA name such as `Europe/Berlin`; default `UTC`).
  Daylight saving time is applied, including the repeated and the skipped hour.
- The year comes from the file's modification time (for a file without one, from a `-YYYYMMDD` rotation date in its
  name): the first line gets the latest year that does not put it more than a day after the file's last change, and
  the year advances when the dates run over New Year. A file without either anchor has its year-less lines skipped
  with a reason, never guessed from the current date.

### Kernel reports stay together

A multi-line kernel report is one record: the OOM report (from "... invoked oom-killer" through "Out of memory: Killed
process ...", including the memory table and the task list) and the kernel warning/BUG report (from
`------------[ cut here ]------------` through `---[ end trace ... ]---`). The record has the time of the report's
first line and the lines joined by line breaks. A report longer than the 16 KiB message limit keeps its beginning and
its end (with the kill line) and states how many lines in between were left out. Lines of other programs written in
between stay separate records.

### Unreadable input

A line or entry that cannot be read is skipped and counted with a fixed reason (never quoting the input); malformed or
hostile input never crashes the import, never makes it allocate without bound and never makes it hang.

## Acceptance criteria

- [ ] AC1: Table-driven tests (`[DataRow]`) with real, shortened sample lines for both parsers (journal export entries of
  sshd, systemd, CRON and the kernel; syslog and kern.log lines in both time formats).
- [ ] AC2: Year boundary and time zones handled (December to January within one file, a file changed shortly after
  New Year, Europe/Berlin summer and winter time, the repeated and the skipped hour, RFC 3339 offsets).
- [ ] AC3: Multi-line OOM reports kept together, from a syslog file and from a journal export.
- [ ] AC4: `vandoxd import` uses both parsers without any test hook, with the configured time zone.
- [ ] AC5: The `log_line` record carries `host` in both languages and in storage; the golden wire fixture pins it.

The detailed, testable criteria are in [plan.md](plan.md).

## Out of scope

- Parsers for MariaDB, mail, Plesk, web server and the legacy `top`/`lsof` log (#17-#20).
- Recognizing events (OOM kill victim, anon-RSS, `oom_score_adj`) from the records: signature detection (#21). The
  parsers produce log lines only, no `kernel_event` records.
- Reading binary journal files; RFC 5424 files (`<PRI>1 ...` with structured data) and other templates than rsyslog's
  traditional and RFC 3339 file formats.
- De-duplicating the same message imported once from the journal export and once from `syslog`.
- The agent's own line parsing (#37); this feature only adds `host` to the shared record.

## Open questions

None. The way the operator states the server's time zone (a backend option with default `UTC`) is a Lead decision,
recorded in [0085](../../docs/decisions/0085-syslog-time-zone-from-import-time-zone-with-embedded-tzdb.md).
