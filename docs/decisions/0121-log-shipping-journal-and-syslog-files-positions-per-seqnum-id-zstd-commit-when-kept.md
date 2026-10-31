# 0121: Log shipping reads the journal per seqnum ID with bounded zstd decoding and syslog and error log files without links, and commits its positions when the scheduler keeps a run

- **Status:** Accepted
- **Date:** 2026-10-23
- **Area:** Agent
- **Source:** Issue #37, Issue #289
- **Supersedes:** —

## Context

#37 ships the journal and configured `syslog`-format files live, without loss or duplicates across a restart. The agent already reads the binary journal in pure Go for the kernel collector
([0119](0119-kernel-messages-from-the-binary-journal-files-read-in-pure-go-without-decompression.md)), which decompresses nothing and
keeps one position. Three copies of the server's journal (an online and an archived system file and a user file, inspected read-only and
not committed) showed: every transport (`syslog`, `stdout`, `journal`, `kernel`, `audit`, `driver`) and audit entries without
`PRIORITY`; 340 compressed data objects, all zstd (`MESSAGE`, `SYSLOG_RAW`, `_CMDLINE`), every frame single-segment with a 2-byte
content size, no checksum, no dictionary and one compressed block, content (and so window) 512 to 19,718 bytes, messages up to 19,710
bytes; and a `user-10000.journal` whose seqnum ID differs from that of both system files while its sequence numbers continue the system
counter. The agent must stay static, start no program (0019), add no right (0030) and bound its memory (0025); a service user can
replace its own log file with a link, and the agent can read every file.

#289 adds the Plesk panel log and the `sw-cp-server`, nginx and Apache error logs, which exist on the server only as files, so that the
web signatures (record 0094) read live lines. The import parses them (record 0092: three line grammars, raw records for unreadable
lines); the agent does not reuse the .NET parsers (0073). The Product Manager decided that the agent keeps its rights: a file the group
`adm` cannot read is not shipped, and the operator grants read access by hand as `deploy/agent/README.md` describes. No line of the
server's logs is available, so the formats stay as unverified as record 0092 states.

## Options considered

1. **Decode zstd items with `github.com/klauspost/compress/zstd`** (chosen) — pure Go, maintained, refuses a frame whose window or
   content size exceeds a configured bound before allocating; one new dependency and decoder surface in security area 10.
2. **A hand-written zstd decoder** — no dependency, but Huffman and FSE decoding of every compressed block the server writes is a large,
   new parser of hostile input for one feature.
3. **Leave compressed items unread** (as 0119) — no new code, but every message of 512 bytes or more would be a gap, against the spec
   (S9).
4. **`journalctl -f -o export` as a child, or `sd-journal` through cgo** — ruled out by 0019 and 0119 (a program under memory pressure,
   a non-static binary).
5. **One journal position, as the kernel collector keeps** — simple, but the user file's own seqnum ID means files are not one ordered
   sequence; a position per seqnum ID (chosen), with the files of one ID merged by sequence number, needs no assumption about how
   journald assigns IDs to user files.
6. **Commit at the start of the next run only, as the kernel collector does** — no change to the scheduler, but a stop cannot know
   whether the last run was kept, so a restart repeats or loses that run (S4); a scheduler callback when a run's records were kept
   (chosen) commits at once and lets the agent save the state once more when it stops.
7. **Store the configured paths in the state file** — readable, but 16 paths of 1,024 bytes at JSON's worst escaping exceed the 16 KiB
   bound of the state file; storing a SHA-256 of the path (chosen) gives every path the same 64 hex digits, so the bound depends only
   on the number of positions.
8. **Refuse links with `openat2(RESOLVE_NO_SYMLINKS)`** — one call, but needs `golang.org/x/sys/unix` as a direct dependency; opening
   each element of the path with `openat` and `O_NOFOLLOW` from `/` (chosen) gives the same refusal with the standard library, without
   a window between check and open. `os.Root` was not taken: it follows links inside the root.

For the error logs (#289):

9. **Detect the format by content, as the import does** — no new option values, but a file's first line would choose its source, and
   `sw-cp-server`'s log cannot be told from nginx's by content; the operator names the format per file (chosen), as `syslog` today.
10. **Extend the syslog package with the error log grammars** — one package, but its name and its `Parse` are about syslog headers;
    **a package with a clock of its own** would hold the skipped- and repeated-hour rules twice; a package `internal/errorlogline`
    (chosen) holds the three grammars and resolves their times through the syslog `Clock`, which gains a resolve for a local time with
    its year.
11. **A raw line takes the file's modification time where it has no predecessor, as the import does** — a live file's modification time is
    that of its newest line, not of the raw line; the file's last time (chosen) is the rule `syslog` raw lines already follow.
12. **Prove parity with hand-written Go expectations** — they would pass a Go port that misreads the grammar as the test's author did;
    golden records written by the C# import and read by the Go tests (chosen) bind both languages, as the journal's `live-records.jsonl`,
    without `log`, which differs by design (the configured path against the listed name).
13. **File access** — `CAP_DAC_READ_SEARCH` is ruled out by 0122; an ACL set by the installer would be a new right (a `security`
    change); the Product Manager chose no new right and a documented by-hand grant, with a default ACL on the directory so that a file a
    rotation creates inherits it, its effect on the real server unverified. Measured in the plan review: plain `setfacl` follows a link
    planted by a directory's owner and would grant the agent its target, so the procedure uses `setfacl -P` after `namei -l`; a default
    ACL replaces the umask for every later file and subdirectory, so it is recommended only where rotation uses `create <mode>`, with
    `g::` and `o::` no wider than the current files.
14. **A line's time as the file's bookkeeping time** — as #37 did; but these formats, and RFC 3339 syslog lines, carry a year the
    writer controls, and a forged year in 2262 would move the start of a truncation's or a given-up span's gap past the lost lines.
    **The run's start plus the 24-hour tolerance of year-less times** still lets a gap start a day ahead. **The earlier of the line's
    time and the run's start** moves a gap start to that run's start, past honest lines written before it that a run stopped on its
    budget right after the forged line left unread. **A line dated after the start of the run that reads it does not move the file's
    last time or the run's latest time** (chosen): they keep their previous value, an earlier line's time or the start of the run that
    took the position, which lies at or before the forged line's real write time, so the start of a truncation's, a lost rotation's or
    a given-up span's gap never passes a lost line; the end of a pending lost-rotation gap is the earlier of the line's time and the
    run's start. The record keeps its own time. Positions saved before this change with a future time (RFC 3339 syslog lines only)
    stay unbounded until the next line dated before its run is read: accepted.

## Decision

The `logs` collector reads every system and user journal file of the machine's journal directory with a position per seqnum ID,
decodes zstd items through option 1's library under a fixed bound (any other compression is a gap), reads configured files only
through a link-free `openat` walk, and commits its positions when the scheduler reports the run kept (a new optional callback), giving
up a span after the fourth dropped run as the kernel collector does; the agent saves its state once more when it stops. The rules are
in the [Agent](../areas/agent.md) area (*Log shipping*, *Agent state*) and the
[Configuration and secrets](../areas/configuration-and-secrets.md) area (`log_shipping`).

For #289 the configured files also take the formats `plesk-panel`, `sw-cp-server-error`, `nginx-error` and `apache-error`, parsed by
`internal/errorlogline` with the import's mapping and raw lines, the agent gains no right, and a file line dated after its run's start
never moves the bookkeeping times (options 9 to 14). The rules are in the
Agent area (*Log shipping*).

## Consequences

- The agent gains its first dependency besides the YAML library; `govulncheck` covers it, and its decoder bounds are a security
  property checked by a heap test.
- Positions per seqnum ID need room for many user journals: up to 256 IDs are saved and the state file's bound rises from 16 to 64 KiB.
  An ID without a position is read from its start only when it is newer than the newest position held, so a forgotten ID, a journal
  turned on later or more IDs than the bound never make a file be shipped twice; an ID started at its end whose end is newer than every
  position held records one gap, so skipped newer lines (a clock step back, an unsaved ID after a restart) are never lost silently.
- Library and decoder bounds are chosen against hostile input, not only real frames: only objects that hold exactly one single-segment frame
  with a stated size of at most 4 MiB are decoded, into one fixed buffer, at most 4 per entry, because the library stops other frames only after their output
  passed the bound and one crafted entry could otherwise use most of a run; drop patterns are bounded by their compiled size, 400
  instructions each and together, because RE2's linear time still multiplies by the program's length: about 0.2 ms per instruction on a
  16 KiB line, so 1,000 per pattern cost up to 300 ms a line and let a flood of long lines push the collector into gaps; the price is
  about a dozen ordinary patterns.
- A renamed log file is read until its writer has moved, and its position is saved, because rsyslog reopens its files only after all
  renames. After a restart the renamed file is searched by name in its directory and accepted only on the identity of the descriptor
  that was opened, so no window lies between a check and the open; the same link-free walk reaches the directory.
- Where the import skips a line it cannot date (an impossible or unstorable syslog time), the collector ships it raw, because live a
  skipped line would be a loss without a gap; live and imported records of such a line therefore differ.
- What a local writer controls must not hide lines without a gap: an entry keeps only the first item of each read name (the import's
  rule), so repeating a name cannot push `MESSAGE` out of the kept items; a run that stops on its time inside a line goes on there in
  the next run, so a line of any length cannot block its file; a renamed file is finished, even across runs, before a newer file is
  taken, and a read error of it holds the newer file back, so neither a budget stop nor an error drops or reorders its rest. For the
  same reason the agent refuses to start when the collector cannot be built instead of running without it.
- The positions within the journal favor no loss over no gap: the newest journal position is never forgotten (forgetting it would
  let the time rule read a forgotten ID again), and a vacuum is judged only where no file of the ID holds the position, so it may
  report a gap where only another ID's sequence numbers lay between.
- A collector whose positions must survive a stop needs the save at the stop and an urgent save within one state interval: the urgent
  threshold became half the state interval (5 s), because the `state` job's start times jitter on its 10-second grid.
- The source read first rotates from run to run, so a backlog in the journal or one file never keeps the others waiting; lines are
  ordered per source, not across sources.
- A crash repeats at most one state interval plus one collector interval of lines (the spec's E1); exact delivery across a crash needs
  the spool (#38) to store positions with the records.
- Live lines carry no classification (`event`, mail markers) until #290.
- Error log records equal the import's except `log` for a line of valid UTF-8 read after its predecessor in the same zone. They differ
  where the agent cannot know what the import knows (a raw line without a predecessor, the previous instant carried across a rotation,
  an invalid byte replaced one by one) and where a raw line's borrowed old time makes the record backfilled (0130); the Agent area names
  each.
- A grant by hand can be lost by a rotation with a restrictive mode or a Plesk repair; the standing `log file not readable` warning is
  the sign, and the renamed file's rest waits until the path opens again. MariaDB's and Dovecot's own files remain import-only.
- The `COMPACT` layout of systemd 252 and later is read like the server's layout, by the same reader (0119, #286).
