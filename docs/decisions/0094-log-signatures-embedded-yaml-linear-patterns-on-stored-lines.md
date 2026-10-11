# 0094: Log signatures are data: an embedded YAML catalog of linear-time patterns, matched on stored log lines on demand, with the copies of one event merged

- **Status:** Proposed
- **Date:** 2026-10-11
- **Area:** Detection, alerts and reports
- **Source:** Issue #21
- **Supersedes:** —

## Context

Issue #21 asks that known events be recognized across all log sources: OOM kills with victim, anon-RSS and
`oom_score_adj`, MariaDB crash and recovery, `Too many connections`, systemd service start, stop and failure, a full
disk, Postfix and Dovecot fatal errors and the server's boot. Signatures are to be data, not code branches, so that a
new one needs no engine change, and each detected event links to its source lines. Detection is deterministic
([0008](0008-deterministic-detection-and-alerting.md)); outage reconstruction (#23) builds incidents from the events.

What the input is: the parsers of #16 to #20 store `log_line` records; some lines already carry an `event` that a parser
recognized (MariaDB lifecycle, [0088](0088-mariadb-error-log-entries-by-content-and-lifecycle-events-in-log-line.md);
Postfix and Dovecot fatal and panic, [0091](0091-mail-lines-classified-in-the-syslog-and-journal-parsers.md)), and a
kernel OOM report is one record with head and tail
([0086](0086-system-log-parsers-generic-syslog-claim-and-grouped-kernel-reports.md)). Ubuntu's packaged rsyslog
configuration writes every kernel line to `syslog` and `kern.log` (not checked on the server), and a journal export of
the same period holds the same lines again, so one kill can be stored up to three times; 0086 requires that it is not
counted twice. 0086 also leaves to this issue whether the journal reader keeps `_TRANSPORT`, because a program name
`kernel` is no proof of origin. Stored records are never updated (storage, *Writing*), and imports are idempotent per
file content, so a content imported once is never parsed again.

## Options considered

Where detection runs:

1. **In the parsers, at import time, as a further `event`** — no new component; but a content imported before a
   signature existed is never read again, copies in different files are parsed separately and cannot be merged, and
   every parser would carry signature code.
2. **On stored log lines, on demand, the events returned and not stored** (chosen) — every stored line is seen by the
   current catalog, the copies of one event can be merged across files and sources, and records stay untouched; each
   consumer pays a scan of the lines it asks about.
3. **On stored log lines, the events kept in a table of their own** — fast to read; but a schema step, and stored
   events go stale when the catalog or the data changes. #23 stores incidents and decides what it keeps.
4. **As `kernel_event` records of origin `backend`** — reuses the model for OOM kills and boots; but the other event
   types have no record kind, and a record cannot reference other records.

How a signature is written:

5. **Code per signature** — excluded by the issue.
6. **YAML with regular expressions on .NET's non-backtracking engine** (chosen) — the established pattern language,
   with named groups for the fields, alternation and optional parts for kernel and systemd versions; the engine runs
   in time linear in the input, so a hostile line cannot make a match hang, and constructs it cannot run in linear
   time (lookarounds, backreferences, atomic groups) are refused when the catalog loads. The parsers stay
   hand-written; the patterns here are trusted data compiled into the binary.
7. **YAML with a hand-written template language** — no regular expression engine; but alternation, optional parts
   and the choice between several occurrences of a delimiter would be new engine code to write and secure.
8. **Signature files the operator configures** — extensible without a release; but a new configuration option, input
   that is no longer trusted and patterns nobody tested. The catalog stays embedded; adding a signature is a change
   to the YAML file and its tests.

How copies of one event are counted:

9. **Every matching line is an event** — counts a kill stored in `syslog`, `kern.log` and the journal three times.
10. **Lines with an identical message are one event** — fails between the journal (no printk time stamp) and syslog,
    and between a MariaDB entry of the error log and the same entry from the journal.
11. **Same signature, compatible host, equal fields, within 60 seconds of the event's first line** (chosen) — the
    fields of an OOM kill (pid, command, anon-RSS, `oom_score_adj`) cannot repeat within a minute, and 60 seconds
    covers the delay between journald's and rsyslog's time stamps of one line, also at boot and under memory
    pressure. The price: a signature without distinguishing fields folds real repeats within a minute into one event,
    whose lines are all still linked and counted.

How far a kernel line is trusted:

12. **Keep `_TRANSPORT` in the journal reader and require the kernel transport** — closes forging in the journal; but
    a new `log_line` field in the wire format, the model and storage, and the syslog files carry no such field, so
    both sources would still be read differently.
13. **Program `kernel` as a classification, the limit documented** (chosen) — one rule for every source, as in 0086,
    0088 and 0091; an event names its source lines, so a reader can see where it came from.

## Decision

Options 2, 6, 11 and 13: signatures are an embedded YAML catalog whose message patterns run on
.NET's non-backtracking regular expression engine; a detector matches them on stored `log_line` records in capture
order and returns events that list their record IDs, merging the copies of one event; nothing is stored and records
are never changed. The rules are in [Detection, alerts and reports](../areas/detection-and-reports.md) (*Log
signatures*).

## Consequences

- Adding a signature is an entry in the catalog plus its positive and negative tests; the detector does not change.
- A detected event is a classification of text, not proof that the program it names wrote the line: any local process
  can log as `kernel`, `systemd`, `mariadbd` or a mail daemon. Events carry their record IDs so that consumers can show
  and weigh the lines.
- Nothing calls the detector yet; outage reconstruction (#23) reads the stored lines in capture order and calls it.
  Reading more than one page of lines that share a capture time needs a keyset on the record ID, which the record
  reader does not offer yet (#23's decision).
- On systemd 249 (Ubuntu 22.04) a started or stopped unit is named by its description, not its unit name, and the
  journal reader keeps no `UNIT` field, so `Started`/`Stopped` lines of scopes and timers match the service signatures
  too; newer systemd versions print the unit name, which the signatures read when present.
- The OOM kill line of `vm.oom_kill_allocating_task` is recognized by the signature, but the kernel report grouper does
  not end a report at it (follow-up issue).
- To revisit: storing events (option 3) once a consumer needs them faster than a scan, or operator-defined signatures
  (option 8), each a decision of its own.
