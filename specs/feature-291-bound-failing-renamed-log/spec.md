# Spec: Bound how long a failing renamed log file holds back its newer lines

Source: Issue #291
Status: Draft

## Problem

An operator who ships a `syslog` or error log file with the agent (`log_shipping.files`) relies on its lines arriving live and in
order. After a rotation the agent reads the renamed file to its end before the file now at the configured path
([Agent](/docs/areas/agent.md), *Log shipping*, *Rotation*). When reads of the renamed file keep failing (`internal/logship/files.go`,
`readRenamed` and `finishRenamed`: a read error returns before the current file is read), every newer line of that configured file
is held back with no end: no alert, report or log search sees them, the only sign is the standing warning `log file not readable`
(`read failed`), and no gap is recorded because the lines are still on disk. A restart does not end it either: the renamed file is
found again by its identity and read on (`findRenamed`), so a lasting error holds the file back again. Today this is a stated residual
of record 0121.

## Scenarios

- S1: Given a renamed file whose read fails, when a later run, before the bound, reads it without an error, then its rest is shipped
  in order before the newer lines of the file at the path, nothing is lost, no gap is recorded, and the agent logs
  `log file readable again` with the file's index.
- S2: Given a renamed file whose read has failed in 149 runs in a row (about 5 minutes at the collector's 2-second interval), when the
  150th run's read of it fails too, then the renamed file is given up: its rest is not shipped, one gap is recorded for it, the agent
  logs the warning `renamed log file given up` with `file` (the index in `log_shipping.files`), and the same run goes on with the file
  at the path from its position, so its held-back lines are shipped. Once that file is read without a problem the agent logs
  `log file readable again`.
- S3: Given a renamed file whose reads keep failing and a further rotation, so that a third file is at the configured path, when the
  bound is reached, then the renamed file is given up as in S2 and the rotation is handled as today: the file read so far becomes the
  renamed file and the new file is read from its start.
- S4: Given a renamed file whose reads fail and an agent restart, when the renamed file is found again by its identity, then the count
  starts anew from that start and the file is given up 150 failing runs later if its reads still fail; a renamed file no longer found
  becomes a gap as today (*Rotation while the agent was stopped*).

## Errors and edge cases

- E1: A run reads one or more whole lines of the renamed file before its read fails. The lines are shipped and the count starts anew,
  so a file that is read on in pieces is never given up.
- E2: A run does not read the renamed file: it stops on its budget, its time or its deadline before the file, the configured path
  cannot be opened (*E13* of the error log formats, unchanged), or the time zone is not readable. The count does not change.
- E3: The run that gives the file up is dropped (an error, a refused record, a sink refusal) or abandoned. The next run reads again
  from the committed state and gives the file up again; the gap is shipped once with a kept run, never lost and never twice.
- E4: At the give-up the renamed file holds the start of a line whose rest is unreadable. That start is shipped as it is, as at a
  truncation, before the gap.
- E5: No line was read from the renamed file since the rotation (its first read fails). The gap starts at the capture time of the last
  line read from that file before the rotation.
- E6: The last line read from the renamed file is dated after its run (a forged year, a clock stepped back). The gap's start follows
  the bound of a file's last time (*A file's last time*), so it lies at or before every line it loses.
- E7: Two configured files each have a failing renamed file. Each file has its own count and its own give-up; the other files and
  the journal are read as before.
- E8: The agent is restarted again within every 150 runs (a restart loop). The renamed file is never given up and holds back the file
  as today; a stated residual.

## Non-functional requirements

- Bound: 150 runs in a row whose read of the renamed file fails, counted by runs, not by the wall clock, so a clock step neither
  shortens nor stretches it; fixed, no new configuration.
- The count is per configured file, held in memory only; the state file keeps its shape and bounds (`.squad/project.md`), so a state
  file written before this change is read as today.
- Memory: one counter per configured file (at most 16); no new buffer.
- Logs: the new warning is a fixed text with the file's index only, never a path or file content, logged once per give-up.
- The gap is an ordinary gap of the log collector: source and collector `logs`, cause `unknown`, the run's start as capture time,
  ending at the run's start (1 µs after its start when it would not end after it).

## Acceptance criteria

- [ ] AC1: A renamed file whose reads fail in fewer than 150 runs in a row and then succeed loses no line and records no gap; its rest
  is shipped before the newer lines of the file at the path, and `log file readable again` is logged (S1).
- [ ] AC2: In the 150th run in a row whose read of the renamed file fails, the collector ships the held start of a line as it is,
  records exactly one gap from the capture time of the last line read from the renamed file (before the rotation when none was read
  after it, bounded as a file's last time) to the run's start, closes and forgets the renamed file (it is no longer saved with the
  position), and in the same run reads the file at the path from its position (S2, E4, E5, E6).
- [ ] AC3: A run that reads at least one whole line of the renamed file, or reads it without an error, starts the count anew; a run
  that does not read the renamed file leaves the count unchanged (E1, E2).
- [ ] AC4: When the bound is reached while a further file is at the configured path, the renamed file is given up as in AC2, the
  file read so far becomes the renamed file and the new file is read from its start (S3).
- [ ] AC5: After a restart the count starts at zero; a renamed file found again is read on and given up after 150 failing runs from
  that start, one not found gives today's gap (S4, E8).
- [ ] AC6: When the give-up run is dropped, the following runs give the file up again from the committed state, and the kept records
  hold the give-up gap exactly once (E3).
- [ ] AC7: The count and the give-up of one configured file do not change the reading of another file or the journal (E7).
- [ ] AC8: The give-up logs the warning `renamed log file given up` with the attribute `file` and nothing else, once per give-up; a
  later run that reads the file at the path without a problem logs `log file readable again` (S2).

## Out of scope

- Skipping only the unreadable part of the renamed file and reading on behind it: an I/O error has no known extent, so the rest is
  given up whole, as the issue proposes.
- A configured path that cannot be opened while its renamed file waits (E13 of the error log formats, *Rotation while the agent was
  stopped* when the directory cannot be reached): no newer line is readable there, so nothing is held back that could be shipped.
- A read error of the file at the path itself: its position stays without a gap, as today.
- Saving the count in the state file (E8 stays a residual).

## Open questions

- Is a bound wanted at all? Answered: yes. The guarantee *No data gaps unless explicitly recorded* (`.squad/project.md`) allows a
  recorded gap, and an unbounded hold-back keeps every newer line of the file from alerting; the issue is labelled `type: feature`
  and asks for the bound with a recorded gap (Lead decision).
- Which bound? Answered: 150 failing runs in a row, about 5 minutes (Lead decision). It is counted in runs so that the clock cannot
  move it (record 0121 already distrusts line times after the run's start). It is longer than the journal's give-up of a damaged file
  after three runs, because a damaged structure stays damaged while an I/O error can be transient; 5 minutes delays the alerts of
  one file by at most that and lets a short failure pass without loss.
- Does the issue's "until a read succeeds or the agent restarts" hold? Answered: partly. Confirmed: a read error stops the reading of
  the configured file for the run (`internal/logship/files.go`, `read` and `readRenamed`; `rotate` through `finishRenamed`), the
  warning stands, and no gap is recorded (*Log shipping*, the paragraph after the gaps list). Refuted: a restart ends it only when the
  renamed file is no longer found; otherwise `findRenamed` opens it again by its identity and a lasting error holds back again.
- Is the count kept across a restart? Answered: no; a fresh descriptor is a new try, and the state file keeps its shape (E8).

## Area documents

- [Agent](/docs/areas/agent.md), *Log shipping*: *Rotation* gains the bound and the give-up; *Gaps of the log collector* lists the
  give-up gap; the paragraph "No gap is recorded while a source cannot be read" names the exception; the *Logging* table gains
  `renamed log file given up`, and the paragraph after it says a read error of the renamed file holds the file back for at most 150
  runs; *Residuals (record 0121)* replaces the hold-back bullet with the restart-loop residual (E8).
- `deploy/agent/README.md`, *What it logs*, and `.squad/project.md`, the log shipping collector's list of fixed log texts, gain the
  new warning. Record 0121 (unreleased) is extended in the plan.
