# 0086: System log parsers: journal export by content, a generic weak syslog parser, kernel reports grouped with head and tail

- **Status:** Proposed
- **Date:** 2026-10-09
- **Area:** Log import
- **Source:** Issue #16
- **Supersedes:** —

## Context

Issue #16 adds the first parsers to the import framework ([0079](0079-log-parsing-and-import-in-the-backend-without-following-links.md)):
`journalctl -o export` and the syslog files `syslog` and `kern.log`. A saved `/var/log` holds many more files in the same
rsyslog format (`auth.log`, `daemon.log`, `mail.log`), and a mail parser (#18) follows. The OOM report that explains the
known outage is printed by the kernel as dozens to hundreds of lines (call trace, memory summary, task table, kill line),
each its own syslog line or journal entry, and the issue requires it to stay one record, while a message is limited to
16 KiB. Signature detection of the OOM kill (#21) works on the stored records.

## Options considered

Which files the syslog parser claims:

1. **Only `syslog` and `kern.log` by name** — no overlap with later parsers; but every other rsyslog file of a saved
   `/var/log` stays "not recognized", and misnamed copies too.
2. **Every file whose first line is a syslog header, weakly** (chosen) — the lowest confidence (name match) for a
   matching name or first line, so any specific parser (content signature, or name match registered earlier) wins; `auth.log` and `daemon.log` are
   imported now. A `mail.log` imported before #18 stays stored as `syslog` (the content counts as imported).

Which journal time stamp:

3. **`_SOURCE_REALTIME_TIMESTAMP`** — closer to when the program logged; set by the client, missing on many entries.
4. **`__REALTIME_TIMESTAMP`** (chosen) — always present, assigned by journald, the order of the journal.

How a kernel report stays together:

5. **One record per line** — simple; the report is scattered and #21 has to reassemble it.
6. **One record per report, cut at 16 KiB** — loses the kill line at the end, the line that matters most.
7. **Split into consecutive part records** — keeps everything but breaks "one record".
8. **One record with head and tail** (chosen) — the first 8 KiB (trigger, call trace, memory summary) and the last lines
   that fit (end of the task table, constraint and kill line), with a line naming how many lines were left out in between.
9. **Raise the message limit** — the 16 KiB text bound is part of the wire model and storage (0044); not for one record.

## Decision

Options 2, 4 and 8: the journal export is recognized by its content, the syslog parser claims rsyslog files weakly, the
journal record time is `__REALTIME_TIMESTAMP`, and OOM and `cut here` reports become one `log_line` with head and tail,
bounded by time span, line count and end of input. The parsers emit log lines only, no kernel events (#21). The rules
are in [Log import](../areas/log-import.md) (*System log parsers*).

## Consequences

- The built-in list is journal, then syslog; a new specific parser for an rsyslog file (#18) goes before syslog.
- A very large report loses the middle of its task table; the record says so and is marked truncated.
- Importing both a journal export and the syslog files of the same period stores those messages twice (sources
  `journal` and `syslog`); records are not de-duplicated across sources.
- Binary journal files and RFC 5424 files are not read; each would be its own parser decision.
