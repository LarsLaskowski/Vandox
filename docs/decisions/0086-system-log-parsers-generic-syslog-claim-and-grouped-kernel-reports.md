# 0086: System log parsers: journal export by content, a generic weak syslog parser, kernel reports grouped with head and tail

- **Status:** Accepted
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

When an open report (no end line yet) is emitted:

14. **At every end of the parse, also when it ends by an exception** — nothing that was read is lost from this run;
    but the importer resumes a failed file by dropping as many emitted records as it stored, so a report flushed at the
    failure (cancellation, an emitter error, "import.time_zone is not set") would make the stored records differ from
    the start of what the completing run emits, and the resume would drop or duplicate records.
15. **Only at the normal end of input** (chosen) — the records stored before a failure are always a prefix of a later
    complete parse; the open report is rebuilt by that parse.

How the 16 KiB bounds are measured:

16. **On the raw bytes of the input** — cheap; but each invalid byte decodes to U+FFFD (three UTF-8 bytes) and the record
    rules count UTF-8 bytes, so hostile input yields records that are refused instead of cut.
17. **On the UTF-8 bytes of the decoded text, cut at a character boundary** (chosen) — the parsers and the grouper keep the
    raw read bounds for memory and cut the decoded text to the record limits, so no record is refused for its length.

Which lines count as kernel lines for grouping:

18. **Journal entries only with the trusted `_TRANSPORT=kernel`** — closes the spoofing in the journal (an unprivileged
    local process can set `SYSLOG_IDENTIFIER=kernel`, but not the underscore fields journald assigns); but the syslog
    files carry no such field (`logger -t kernel` writes a line that looks exactly like a kernel line), so the same
    spoofing stays open for `syslog` and `kern.log`, and the two parsers would group the same messages differently.
19. **Program `kernel` in both parsers, the limit documented** (chosen) — one rule for both sources. A local process
    that logs as `kernel` can open a fake report that takes in the real kernel lines of the same host for up to 60 seconds
    or 2,000 lines; those lines are not emitted as their own records but go into the fake record, and past 16 KiB its
    middle is cut (marked truncated), so some of them survive only in the omitted-lines count. A real start line closes
    the fake report. Forging needs local access to the monitored
    server, and the forged text could just as well imitate a whole OOM report; the import never treated a program name
    as proof of origin.

## Decision

Options 2, 4, 8, 11, 13, 15, 17 and 19: the journal export is recognized by its content, the syslog parser claims rsyslog files weakly, the
journal record time is `__REALTIME_TIMESTAMP`, and OOM and `cut here` reports become one `log_line` with head and tail,
bounded by time span, line count and the normal end of input (never flushed when a parse fails), with every byte bound
counted in UTF-8 bytes of the decoded text; lines of others inside a report are emitted before it; `log` is the
listed path. The parsers emit log lines only, no kernel events (#21). The rules
are in [Log import](../areas/log-import.md) (*System log parsers*). Kernel lines are recognized by the program `kernel` in both
parsers; the transport is not checked.

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
- A local process logging as `kernel` can forge kernel lines and open a report that absorbs the real kernel lines of the
  same host for up to 60 seconds or 2,000 lines (option 19); those lines are not emitted as their own records, and past
  16 KiB some of them survive only in the omitted-lines count. Signature detection (#21) must not treat program `kernel` as
  proof that a line came from the kernel; if the journal reader is to tell them apart, it has to keep `_TRANSPORT` first,
  which is #21's decision.
