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

Where the lines written inside a report go:

10. **Hold them until the report closes and emit them after it** — file order of the report's first line; needs a second
    buffer of up to 2,000 records.
11. **Emit them as they come, before the report record** (chosen) — no extra buffer; the emitted order is deterministic
    but not file order, so the parser contract says "deterministic order" instead of "file order" (nothing depends on
    file order: the importer resumes by count).

What a syslog record's `log` holds:

12. **A normalized path** (rotation suffix and import-root prefix stripped, so it matches the agent's `/var/log/syslog`)
    — would guess: the import root can be any copy, and the server's absolute path is not in the input.
13. **The path as the import lists it** (chosen) — relative to the import root or archive, rotation suffix included
    (`backup/var/log/syslog.1`); the wire format defines `log` for both producers.

## Decision

Options 2, 4, 8, 11 and 13: the journal export is recognized by its content, the syslog parser claims rsyslog files weakly, the
journal record time is `__REALTIME_TIMESTAMP`, and OOM and `cut here` reports become one `log_line` with head and tail,
bounded by time span, line count and end of input; lines of others inside a report are emitted before it; `log` is the
listed path. The parsers emit log lines only, no kernel events (#21). The rules
are in [Log import](../areas/log-import.md) (*System log parsers*).

## Consequences

- The built-in list is journal, then syslog; a new specific parser for an rsyslog file (#18) goes before syslog.
- A very large report loses the middle of its task table; the record says so and is marked truncated.
- Importing both a journal export and the syslog files of the same period stores those messages twice (sources
  `journal` and `syslog`); records are not de-duplicated across sources.
- Ubuntu's packaged rsyslog configuration (not checked on the server) writes `kern.*` to `kern.log` and every facility
  to `syslog`, so importing both stores each kernel line and each OOM report twice under source `syslog`, with different
  `log` values. Signature detection (#21) must not count such a kill twice.
- The `log` of an imported record differs from the one the agent (#37) will send for the same file (listed path versus
  absolute path), so records of both cannot be matched by `log`.
- Binary journal files and RFC 5424 files are not read; each would be its own parser decision.
