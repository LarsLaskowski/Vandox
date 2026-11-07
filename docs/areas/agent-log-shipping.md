# Agent log shipping

## Scope

The `logs` collector of `vandox-agent`: shipping the systemd journal as `log_line` records, its positions, the filter, the
classification of live lines, the bounds and commit of a run, and its gaps and log lines; the backend's detection relies on it. The
configured files are in [Agent log shipping from files](agent-log-shipping-files.md); the rules here that do not name the journal hold
for them too.

## The collector

- `logs` runs at an interval of 2 seconds with a timeout of 2 seconds, placed after `kernel` in the job list. It ships the lines of
  the journal and of the files `log_shipping.files` names as `log_line` records ([Wire format records](wire-format-records.md)) and
  records a `gap` for every span of lines it cannot ship. It reads the journal files itself with the reader of
  [Agent kernel events and boots](agent-kernel-events.md), decodes zstd-compressed items with a bounded decoder of its own, starts no
  program and writes no file.
- Its options are `log_shipping` ([Configuration options](configuration-options.md)): with `log_shipping.journal: false` it reads no
  journal and drops its journal positions; without configured files it reads no file and no time zone.
- The collector always runs: when it cannot be built from the checked options, the agent does not start (`agent cannot start` with
  the error, exit code 1).

## The journal

**Files.** The machine ID and the directory are found as for the kernel collector
([Agent kernel events and boots](agent-kernel-events.md), *Where it reads*): `/var/log/journal/<machine-id>`, else
`/run/log/journal/<machine-id>`, the first that can be listed. Of its regular files the system files (`system.journal`,
`system@*.journal`, `system@*.journal~`) and the user files (`user-<UID>.journal`, `user-<UID>@*.journal`, `user-<UID>@*.journal~`,
the UID 1 to 10 digits) are read, at most 1,024, listed in chunks of 256; journald's default `SplitMode=uid` puts the entries of users
with a UID of 1000 or more (on Plesk the web-space users) into the user files. `system.journal` and `user-<UID>.journal` are
**online**, the others archived; a set-aside file is read like an archived one. The header of every listed file is read at every run
under the layout rules of *Layout* (kernel events area). A file is read through its main entry array, so every entry of every
transport is seen.

**Entries.** Of each entry the items `MESSAGE`, `PRIORITY`, `_HOSTNAME`, `SYSLOG_IDENTIFIER`, `_COMM`, `_PID` and `SYSLOG_PID` are
read, each value up to 16,384 bytes (a longer value is cut without reading the rest); of every other item only the first 64 bytes, to
see its name. Of each name only the first item is kept (a later item of that name is passed over, a plain one without reading its
value), so an entry keeps at most seven items. The mapping is the import's journal mapping
([System and mail log parsers](log-parsers-system.md), *Journal export*): the capture time from `__REALTIME_TIMESTAMP`, `host` from
`_HOSTNAME`, `program` from `SYSLOG_IDENTIFIER` (else `_COMM` when that is absent or empty), `pid` from `_PID`, else `SYSLOG_PID`
(1 to 10 digits, 1 to 2,147,483,647, else 0), `priority` from `PRIORITY` when it is one digit 0 to 7, `message` from `MESSAGE`, the
first value of a repeated item, `log` and `source` `journal`; *Classification* then sets `event` and the priority of mail lines and
joins MariaDB entries and kernel reports. An entry without `MESSAGE` or with a `__REALTIME_TIMESTAMP` of 0 or over
9,223,372,036,854,775 µs is not shipped and no gap.

**Text of every line.** Every text, of every source, is made valid UTF-8 (each invalid byte becomes U+FFFD) and cut at a character
boundary — `message` to 16,384 bytes, `host` and `program` to 1,024 — with `truncated` set when one of them was cut, here or at the
read; control and format characters are kept as written, for whoever displays the line to escape.

**Compressed items.** journald compresses every item whose payload is 512 bytes or more (on the server with zstd). Its name lies
inside its frame, so every compressed item of an entry counts, wanted or not: at most 4 are decoded per entry, and a fifth makes the
entry unreadable without the rest being read (a coredump's entry is a gap). An item is
decoded only when it is zstd, its stored object is at most 4 MiB (a larger one is not read) and holds exactly one frame that is
single-segment, states a content size of at most 4 MiB and uses no dictionary — the form journald writes. Before anything is decoded
the frame header is checked and the block headers are walked to the last block: a reserved block type, a block over 128 KiB or past
the object, or bytes left after the last block (and the checksum, when the frame has one) refuse the frame, so no second frame
follows it. The decoder runs single-threaded with memory and window bounded to 4 MiB and writes into one output buffer of 4 MiB +
128 KiB + 16 bytes, allocated once and never grown; a result outside that buffer is refused. Of a decoded item at most its 16,384
bytes of value are kept. An item compressed with XZ or LZ4, a frame refused or failing to decode, a larger object and a damaged data
object of an item make the entry **unreadable**: a gap over it (*Gaps*).

**Journal positions.** The collector keeps a position per sequence-number ID: the sequence number of the last entry read and the time
of the last readable entry, which never moves back. Each run groups the files by ID, orders the groups by the head time of their
first file and the files of a group by their head sequence number, and reads each ID from its position: a file that ends at or
before the position is skipped, the first entry after the position is found by bisection over the main entry array, and the entries
are shipped in their order, one ID after the other. An ID without a position starts:

- when no journal position is held at all (a first start, the journal turned on or readable only now): at the tail of its latest
  file, without a gap;
- otherwise at its first entry when the head time of its first file is after the newest journal position held when the run started
  (a new ID, a new user journal);
- otherwise at the tail of its latest file, with one gap from the newest position's time to that file's tail time when that is
  later, because the skipped part may hold lines newer than every position (the clock stepped back, a user journal whose first
  entries are older, an ID whose position was not saved).

No file is shipped twice. An ID none of whose files is online and whose position is at its latest file's tail is
forgotten, unless it holds the newest position, which is never forgotten. An ID of which no file is left is forgotten with one gap
from its position's time to the head time of the earliest ID that starts after it (else the run's start). When files of an ID are
left but none holds the position and the first later file does not start at the position's next sequence number, the entries
between are gone (vacuumed): one gap from the position's time to that file's head time (when the head time is not later, to the
next readable entry), and reading goes on in that file. A hole in the sequence numbers between files is not checked (one ID's
numbers skip those journald gave another ID's entries), so this may record a gap where only such entries lay between. At most 256 journal positions are saved: those of IDs with an online file first, then the others, newest
first; a further ID keeps its position in memory only and after a restart starts at its tail by the rules above, with their gap.

## A run

**Filter.** After mapping, a line is not shipped when it has a priority less severe than `log_shipping.min_priority` (a line without
a priority always passes) or when it meets every condition a drop rule sets: `log` equal to `journal` or the configured path,
`program` equal to the mapped program, `priority` that priority or less severe (a line without a priority never meets it), and
`message` an RE2 search in the mapped message (cleaned and cut). A filtered line moves the position like a shipped one.

**Budgets.** A run ends when it has produced 1,000 records (gaps included), at the record that brings the text of its log lines (UTF-8
bytes of `log`, `host`, `program` and `message`) to 4 MiB, after one second of reading, or at its deadline; the next runs read the
rest. Filtered lines count only against the second, so a flood of filtered lines delays wanted ones but never stops them. The clock is
looked at after every shipped record, every 1,000 entries or lines examined, every full 64 KiB read from a file and every journal entry
whose reading decoded an item (a decoded 4 MiB item costs about 4 ms). The journal and each configured file are
**sources**; the source read first changes from run to run in turn (the journal first in one run, file 0 first in the next, and so
on), so a backlog in one source never keeps the others waiting. Within a source the order is kept.

**Record check.** Every record is checked as the scheduler stamps and checks it ([Agent](agent.md), *Scheduling*) before it is
returned; a refused record is replaced by one gap from the previous line of the same source (the previous entry of the ID or line of
the file read, else the position's time) to the record's time, and a gap that would still fail becomes the last microsecond before
the run's start. So the scheduler never refuses a record of this collector, and a hostile line never makes a span be read again.

**Commit, save and give-up.** A run works on a copy of the committed reading state (positions, held lines, renamed files, the per-file
previous instants, the refused journal files). When the scheduler reports the run kept ([Agent](agent.md), *Scheduling*, the
observer), that copy becomes the committed state at once, and changed positions are handed to the state file as an urgent change
([Agent](agent.md), *Agent state*), so they are saved within one state interval. A run reported not kept (an error, a refused record,
a sink refusal) is a **dropped** run; a run that timed out or was skipped is not reported and counts as one dropped run at the next
run, which learns that its predecessor was lost. A dropped run's copy, which an abandoned worker may still change, is never
committed; the next run reads again from the committed state, so the lines return as long as the journal or the file still holds
them. At the fourth dropped run in a row from one committed state the span is **given up**:

- when a dropped run's result came back, the next run starts from the reading state the last such run reached (that copy is not
  committed) and records one gap from the committed time (the newest of the journal positions' times and the files' last times,
  else the run's start) to the latest time of an entry or line that run read (else the run's start); the span's lines are not shipped
  and reading goes on after them. Every following run starts from that same state and carries the same gap until a run is kept and
  its copy is committed, so the gap is never lost with a dropped run; a run that started from the span and is dropped too does not
  replace the span;
- when none came back (all timed out or were skipped), one gap runs from the committed time to the run's start, once per committed
  state, and the positions stay.

A sink refusal lasting more than four runs (8 seconds: a spool write that keeps failing or a hand-over the spool cannot place; a full
spool drops its oldest records instead) therefore gives up lines the journal or the file still holds. A run that is
abandoned never moves a committed or saved position.

## Gaps

Each gap has source and collector `logs`, cause `unknown` and the run's start as its capture time; a gap whose end would not be after
its start ends 1 µs after it. Besides the gaps of the journal positions, a refused record and a given-up span above, and those of the
configured files ([Agent log shipping from files](agent-log-shipping-files.md): a lost rotation, a truncation, a given-up renamed
file), gaps are recorded for:

- **an unreadable entry**: from the previous entry read in the ID (shipped or filtered; else the position's time, else the run's start)
  to the next readable entry (else the run's start, or the last unreadable entry's time when the run stopped before the end of the
  ID); several in a row give one gap;
- **a journal file the reader refuses** (`unsupported layout`, a header that is `malformed file`): one gap from the newest journal
  position's time (else the run's start) to the run's start the first time the agent process meets the file, and while the online
  file is refused a further gap from the end of the last one to the run's start at the first run at least 600 seconds after it. The
  refused files (at most 1,024) are part of the reading state, so the gap of a dropped run is recorded again by the run that reads
  again; the other files are read on;
- **a journal file damaged in its structure** (an entry array or entry object that violates the layout): reading of the file stops at
  the damage, the lines before it are shipped and the position stays before it. An archived or set-aside file is given up at once. In
  an online file the read is repeated once after its size is read again; the run then stops reading that ID, and the third run in a
  row that stops at the same file, ID and sequence number gives the file up. Giving up records one gap from the last entry read from
  the file (else the position's time, else the run's start) to its tail time (an online file's header read again first; without a
  tail time, to the run's start), moves the position to the file's tail and goes on with the next file of the ID or the entries
  journald appends later. A cut-off end of an archived or set-aside file ends it the same way, also when its tail time is that of the
  last entry read (a 1 µs gap marks the cut-off end), as does a damaged file given up at its tail entry.

No gap is recorded while a source cannot be read and its position stays, because its lines are still there: no machine ID, no journal
directory, a directory that cannot be listed, a journal file that cannot be opened or read for another reason (the run stops reading
the journal or that ID), and the file cases of [Agent log shipping from files](agent-log-shipping-files.md) (*Not read, no gap*).
Reading resumes at the position.

## Logging

One line per change, fixed texts only, never a path, a field value or a message, and no line per shipped line, so the agent's own
entries in the journal, which it ships like any other, cause no loop. The lines of the configured files and the time zone are in
[Agent log shipping from files](agent-log-shipping-files.md) (*Logging*).

| Line | Attributes | When |
| ---- | ---------- | ---- |
| warning `journal not readable` | `err`: `journal: machine ID not readable`, `journal: no journal directory`, `journal: directory not readable` or `journal: <online\|archived>: <unsupported layout\|malformed file\|read failed>` | the first run with a problem after the start or after the journal was read; a different problem meanwhile logs nothing |
| info `journal readable again` | — | the first run that lists and reads the journal without a problem after that |

## Classification

The agent classifies its live lines with the import's three stages, ported and bound by the same golden records as the import
([System and mail log parsers](log-parsers-system.md)), ordinal and on bytes: the mail classification (priority and `event` of
Postfix and Dovecot lines, `postfix.fatal`, `dovecot.database_unavailable` and the others), the joining of MariaDB error log lines into
one entry per header (the events `mariadb.start`, `ready`, `shutdown`, `shutdown_complete`, `abort`, `recovery_start` and
`recovery_end`) and the grouping of kernel reports (OOM kills and `cut here` reports) into one record. The bounds are the import's: an
entry of at most 16,384 bytes, joined from lines of the same host, program and process within 60 seconds of its header by capture time
(before or after); a report of at most 2,000 lines, with a head of 8,192 bytes, a tail and `[N lines omitted]`, joined from kernel
lines of its host within 60 seconds after its first line. A group is always one record at its close, with the capture time, source
and sequence of its first line; a record the check refuses becomes a gap over the group.

- **Order.** The filter (`min_priority`, `drop`) decides first, on the line as mapped; only lines that pass are classified. A dropped
  MariaDB header leaves its continuation lines to the open entry of the same host, program and process within 60 seconds, else they
  are shipped as plain lines. `log_shipping.min_priority: notice` or stricter therefore drops the MariaDB journal lines, which carry
  priority 6 (assumed from the unit's `SyslogLevel=`, not checked on the server), so no `mariadb.*` event comes from the journal then;
  `deploy/agent/agent.yaml` says so.
- **Which sources.** Every configured file of the format `syslog` and every journal sequence-number ID has its own stages, so a
  journal line and a file line never join. A line of a user journal file (`user-<UID>.journal`, its archives) gets the mail
  classification only and passes the other stages untouched; the file decides, not the ID. The files of
  the error log formats get no stages.
- **Memory.** A source holds stage state, and a group its buffers, only while a group is open; one flag per source marks an open
  MariaDB crash recovery (`recovery_start` seen, `recovery_end` not yet) while set. So the 256 user IDs cost nothing, and at most one
  entry and one report per source are held however many forged headers or start lines come.
- **When a group closes.** At the ends the stages know: a line that ends it (a header, a line of its key that does not join or does
  not fit, an end line of a report, a start line, the 2,000th line). By time: when a shipped line of its source is captured more than
  60 seconds after the group's **anchor**, and, when the source has been read to its end in a run, when the run's start is more than 60
  seconds past the anchor. The anchor is the earlier of the first line's capture time and the start of the run that read it, so a
  line dated in the future closes 60 seconds after its run; a line dated after the start of its run counts as captured at that start
  for the expiry. By the source: at the end of the reading of any journal ID in a run, except the ID whose online file is
  `system.journal`; at the end of a renamed file; at a rotation, where the lines of the kept file move to the renamed part (a group
  open across a rotation is two records); before a file is read at the path while a renamed part is still read; before every gap of
  the source (truncation, lost rotation, the give-up of a renamed file, vacuumed span, unreadable entry, refused journal file, an ID
  forgotten with its gap), so the group is sent before the gap and the next line does not join; and, after a give-up, before the
  give-up gap, from the dropped fork.
- **Held-back positions.** A source with an open group is saved at its oldest held line, not at its read position: for the journal
  the sequence number and time before that entry, for a file the offset of that line in the current or the renamed part and the time
  of the line before. A line a group holds is never covered by a saved position. After a crash, or a stop whose flush was not kept,
  the source is read again from its oldest held line: the lines of the open groups, and those shipped after them, come again as
  duplicates (at most the span of one group plus the state interval), and none is lost, also with a filter or time zone changed in
  between. The state file keeps its shape and bounds; an ID whose held-back position falls off the 256 positions starts by the rules
  of *Journal positions*, with their gap.
- **The stop flush.** After the scheduler has stopped and before the last save ([Agent](agent.md), *Agent state*), the agent flushes
  the collectors that hold lines, with a deadline of 5 seconds. A collector whose worker the stop left out is waited for within that
  deadline and skipped when it does not return, so a flush never runs beside a run; a flush runs in a worker of its own and is
  abandoned at the deadline, with nothing put and no kept report. Warnings: `flush skipped, collector still running` and
  `flush abandoned, deadline reached`, each with the collector's name only. The `logs` flush resolves an
  earlier pending run as dropped, forks the committed state, closes every group in the fork and returns the records, committing
  nothing; only the report that the records were kept adopts the fork and saves the shipped positions. So a clean stop repeats nothing
  and loses nothing, a flush that was not kept leaves the held-back positions, and a group closed by the flush can be two records when
  the source goes on after a restart.

## Memory

The collector holds at most 24 MiB live, independent of the size of the journal, of the files, of the backlog, of an entry and of a
line: the records of one run (at most 4 MiB of text, the crossing record and the fixed part of at most 1,000 records), the journal
reader's buffers and at most 1,024 file headers, the zstd path (the output buffer above and one input buffer grown once to at most
4 MiB, both reused), the 64 KiB read buffer, the held lines of at most 16,385 bytes per current and renamed file in the committed
state and up to two copies, and the open groups of *Classification*: an entry (at most 16,384 bytes) and a report (at most about
16 KiB) per holding source, in the committed state, a fork and the last dropped fork. A test holds the bound with every field at its
maximum, 100,000 journal entries, lines over 16 KiB and full groups in the online system journal and 16 files among 256 user IDs and 8
further IDs (measured peak 1.63 MiB above the base, three copies included).

## Checked against the server

The reader with the decoder read every entry of the server's two system files (72,909 and 73,265 entries, 340 zstd frames of
descriptor `0x60`) and of a later copy with a user journal (`user-10000.journal` and 22 archives; 54,360 system entries, 38 to 444 per
user file) without an unreadable item (the files are not committed). The user files' seqnum IDs differ from the system file's, their
sequence numbers rise strictly within and across the archives, and those of the current user file lie inside the system file's range
without colliding with a system number, as record 0121 assumes. CI writes `testdata/journal/live.export` (21 entries of the server's
transports and field forms, one message of 20,000 bytes) as journal files with zstd frames of the server's form
(`testdata/journal/zstd-frames.txt`); the agent's and the import's tests both read the records the import maps from it
(`testdata/journal/live-records.jsonl`).

## Residuals (record 0121)

- After an unclean end (SIGKILL, a crash, a power loss) the lines read after the last saved position are shipped again: at most one
  state interval plus one collector interval (10 + 2 seconds); nothing marks them as duplicates. A stop with SIGTERM is exact.
- Any local user can write journal entries with any `SYSLOG_IDENTIFIER`, `PRIORITY` and message (`logger`, the native socket), a
  service user anything into its own log file; such lines are shipped as written. Only `_PID`, `_COMM` and `_HOSTNAME` are journald's.
- A journal without keyed hashes, XZ or LZ4 items and entries with more than four compressed items are lost to recorded gaps.
- A refused journal file gives its gap again after every agent start; a file replaced by a link between the listing and the read of
  the journal directory is followed, as for the kernel collector.
- Lines are shipped per source and journal ID, not merged by time.
- Differences from the import, whose classification the live stages follow: (a) a group closes by the clock as well, because live
  input has no end; (b) a line captured within 60 seconds of the header that arrives after a line already more than 60 seconds later
  no longer joins (only when the clock steps back); (c) an entry open when its journal ID or renamed file ends, or at a rotation or a
  gap, is closed there, and the lines after it start a new group; (d) a line dated after the start of its run counts as captured at
  that start for the expiry; (e) records come out in the order their groups close, not by capture time; (f) the recovery state of the
  MariaDB entries is kept per source while it is set, but not in the state file: a restart loses an open recovery, and its
  `recovery_end` gets no event; (g) a line without a syslog header is shipped raw and passes the stages, where the import skips it.
- A `syslog` file not read to its end (a budget-limited backlog) closes its groups only by capture time; a flood of lines dated
  within the window keeps a group and the held-back position as long as it lasts, and a crash then reads the whole span again.
- Event signatures see live lines: forged `postfix/*`, `dovecot` and, in the system files, `mariadbd` and `kernel` lines make events
  live too, which alert rules built on them (#56, #61) must weigh. A line of a user journal file never joins an entry.

## Related decisions

- [0121](../decisions/0121-log-shipping-journal-and-syslog-files-positions-per-seqnum-id-zstd-commit-when-kept.md) — why a position
  per sequence-number ID over system and user files, bounded zstd decoding, commit when kept, the stop flush, the import's stages
  (options 15 to 23) and drop patterns bounded by compiled size.
- [0119](../decisions/0119-kernel-messages-from-the-binary-journal-files-read-in-pure-go-without-decompression.md) — why the binary
  journal files are read directly.
- [0028](../decisions/0028-data-gaps-are-always-recorded.md) — why every span that cannot be shipped is a recorded gap.

## Not here

- Configured log files, their formats, rotation, truncation and the time zone: [Agent log shipping from files](agent-log-shipping-files.md).
- Scheduler, state file, stop order, gaps in general: [Agent](agent.md).
- The journal reader's layout rules and where the journal is found: [Agent kernel events and boots](agent-kernel-events.md).
- The `log_shipping` options and their error texts: [Configuration options](configuration-options.md).
- Its rights: [Agent rights](agent-rights.md); where records go next: [Agent spool](agent-spool.md).
- The `log_line` record: [Wire format records](wire-format-records.md); the import's mappings and stages:
  [System and mail log parsers](log-parsers-system.md).

## Implementation

`internal/logship` (the collector, filter, positions, stop flush), `internal/logstages` (the port of the import's stages; fixtures
`testdata/logs/mail.log`, `kern.log-oom`), `internal/journal` (`File.SeekAll`, `File.NextWith`), `internal/journal/journaltest` (test
journal writer), `internal/zstdfield` (over `github.com/klauspost/compress/zstd`, low-memory mode, one goroutine). Tests: `TestLogs_ForgedFloodHoldsOneEntryAndOneReportPerSource`,
`TestLogs_HeapStaysBounded`, `TestDecoder_HeapStaysBoundedForTheLargestFrame`, `TestReal_NextWithDecodesEveryEntry` (with
`VANDOX_REAL_JOURNAL_DIR`), `TestWrite_CompressedFramesMatchTheServerForm`.
