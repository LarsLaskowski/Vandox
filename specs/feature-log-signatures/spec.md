# Spec: Signature detection for known events

Status: Approved by Lead

Source: Issue #21 (and its comment of 2026-10-10)

## Problem / motivation

v0.1.0 is the forensics release: Vandox imports the production server's logs and explains past outages (memory
exhaustion, the OOM killer ends MariaDB, Plesk and mail go down). The parsers of #16 to #20 store the lines; a few of
them already carry a parser event (MariaDB lifecycle, Postfix and Dovecot fatal and panic). What is missing is one place
that recognizes the known event types in every stored log line, whatever file or source it came from, and hands outage
reconstruction (#23) a list of events that each point back to the lines they were read from.

## Behavior

- The backend has a **catalog of signatures**, written as data (an embedded YAML file), not as code. Each signature has
  an ID, a description, optional conditions on the line's origin (its program or its record source) and parser event,
  an optional message pattern and the named fields the pattern extracts. Adding a signature is a catalog entry plus its tests; the engine does not change.
- The built-in catalog recognizes, in this order: `oom_kill` (victim pid and command, anon-RSS in bytes,
  `oom_score_adj`), `mariadb_crash` (signal), `mariadb_recovery_start`, `mariadb_recovery_end`,
  `mariadb_too_many_connections` (Postfix's and Dovecot's MySQL client errors), `service_start` and `service_stop`
  (description, and the unit when systemd prints it), `service_failed` (unit and result), `disk_full` (MariaDB's own
  write errors), `postfix_fatal`, `dovecot_fatal` and `boot` (kernel version).
- A **detector** takes stored log lines in capture order (each with its record ID) and returns **detected events**: the
  signature, the capture time of the first line, the host, the extracted fields, the record IDs of its lines and their
  count.
- The copies of one event (the same kernel line in `syslog`, `kern.log` and a journal export; a MariaDB entry in its error
  log and in the journal) are **one event** that lists every copy's record ID: lines of the same signature, a compatible
  host and equal fields within 60 seconds of the event's first line are merged.
- A detected event is a **classification**, not proof of origin: a program name (`kernel`, `systemd`, `mariadbd`) can be
  forged by any local process. The journal reader keeps no `_TRANSPORT`. Text a remote party controls (a referrer, a
  requested path, a mail address) cannot trigger a signature: a signature without a parser event names the writers it
  reads and anchors its pattern at the start of their server-written text.
- Hostile line text cannot make detection hang or grow memory without bound: patterns run in linear time, fields are
  validated before conversion, and open events and linked lines per event are capped.
- Nothing is stored, and no stored record is changed. No command or view shows events yet; #23 is the first consumer.

## Acceptance criteria

- [ ] AC-S1: Each signature has positive and negative test cases (issue AC 1).
- [ ] AC-S2: Detected events reference their source lines by record ID; an OOM kill stored in `syslog` and `kern.log`
  (and the journal) is one event that lists each copy (issue AC 2, issue comment).
- [ ] AC-S3: A new signature can be added without changing the engine: a catalog text with a signature the engine has
  never seen is loaded and detected (issue AC 3).
- [ ] AC-S4: The catalog is checked when it loads; a malformed entry is refused with a fixed error text.
- [ ] AC-S5: Detection is bounded on hostile input (linear-time patterns, validated fields, capped state), and
  client-supplied text in web server lines triggers no signature.

## Out of scope

- Storing events, a command or a web view that shows them (#23, #27, #28).
- Reading the store page by page for the detector (#23), and live detection on agent data (#53).
- Signatures defined by the operator in configuration.
- Disk-full and `Too many connections` lines of PHP, nginx, Apache and Plesk (follow-up issue: their messages carry
  client-supplied text before the error and need a pattern per format).
- Keeping journald's `_TRANSPORT` or `UNIT` fields.
- Multi-line correlation (crash, then recovery, then ready) into an outage: #23.

## Open questions

None. The decisions the issue left open (where detection runs, how copies are counted, whether `_TRANSPORT` is kept,
the pattern language) are recorded in decision record 0094.
