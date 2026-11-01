# Spec: Show the socket's UID and user in the new-listener findings

Source: Issue #304
Status: Approved by Lead

## Problem

An operator who reads the *New listeners* section of the forensics report (`vandoxd report`, the report page `/report` and its Markdown
download) sees for each new listener only the owner's PID and command. The command is the process's own text, and it is missing whenever
the agent could not see the holder (`no owner`), which is exactly when the operator most needs to know whose listener it is. Since #279
(record [0115](../../docs/decisions/0115-connection-collector-proc-net-tables-owners-from-fd-regular-and-full-snapshots-bounded-listener-check.md),
options 13 to 19) every listener entry the agent sends carries the socket's UID from the kernel table (`uid`), its name (`user`) and
`mixed_uids` when the endpoint's sockets have several UIDs; the backend stores them with each finding's listeners, but the report drops
them (`ReportComposer.ListenerPieces`, `src/Vandox.Backend/Reports/ReportComposer.cs:885`). Record 0115 names showing them as a follow-up.

Claims of the issue checked against the code: the listener entries carry `uid`, `user` and `mixed_uids` (`SocketOwner`, `Listener.MixedUids`
in `src/Vandox.Core/Model/`) — confirmed; the stored lists of `connection_marks.new_listeners` and `listener_comparisons.new_listeners` are the
serialized `Listener` entries (`ListenerQueries.SerializeListeners`), so they hold the three fields for every snapshot decoded with them —
confirmed; the report and the page show only PID and command — confirmed (the page renders the composer's blocks, `ReportContent`; no other
view lists listeners).

## Scenarios

All lines are list items under a finding of the section *New listeners* ([Detection, alerts and reports](../../docs/areas/detection-and-reports.md),
*Content and format*); `` `x` `` stands for the external text `x` written as a code span by the report's rules.

- S1: Given a finding whose new listener `tcp 0.0.0.0:23` has the owner PID 200 `telnetd`, `uid` 0 and `user` `root`, when the operator runs
  `vandoxd report`, then the listener's line reads ``tcp 0.0.0.0:23, PID 200 ` telnetd `, socket UID 0 ` root ` ``.
- S2: Given a new listener without an owner (`tcp6 [::]:8443`) with `uid` 33 and `user` `www-data`, when the report is made, then the line reads
  ``tcp6 [::]:8443, no owner, socket UID 33 ` www-data ` ``.
- S3: Given a new listener with `uid` 0, `user` `root` and `mixed_uids` true, when the report is made, then the line ends with
  ``socket UID 0 ` root ` (lowest of several)``.
- S4: Given the same data, when the operator opens `/report` or downloads `report/markdown`, then the page shows the same text with the user name
  in a `code` element as an external text (like the command), and the download is byte for byte the command's output.
- S5: Given `-lang de` (or `?lang=de`), when the report is made, then the parts read `Socket-UID <uid>`, `(niedrigste von mehreren)` and
  `Socket-UID unbekannt`; the rest of the line is unchanged (`PID`, `kein Besitzer`).

## Errors and edge cases

- E1: A hostile `user` (a backtick run such as `` a`b ``, Markdown or HTML such as `<b>x</b>`, a U+202E, a control character, a backslash
  sequence) is written by the report's rules for external text, exactly as a command would be: escaped, fenced one backtick longer than its
  longest run, never read as Markdown or HTML; on the page it is literal text of an `ExternalText`.
- E2: A listener with `uid` and without `user` (accepted by the wire format, "a reader shows the number"): the line ends with `socket UID 33`
  and no code span (not the dash `—`); with `mixed_uids` it ends with `socket UID 33 (lowest of several)`.
- E3: A listener without `uid` (a list stored before the backend read the field, or a producer that does not send it): the line ends with
  `socket UID unknown` — never `socket UID 0` or `root`.
- E4: `uid` 0 is shown as `socket UID 0`, and the largest UID 4294967295 as `socket UID 4294967295`: a present 0 is never treated as absent.
- E5: A `user` that is the UID in decimal (the agent's name for a UID without a `passwd` line, e.g. `1234`) is shown as it is:
  ``socket UID 1234 ` 1234 ` ``.
- E6: A `user` of spaces only is written exactly as a command of spaces only: each space as the escape `\u0020` inside the code span
  (`MarkdownText.Code`).
- E7: Only the listener lines change, each by exactly its `, socket UID …` suffix; every other line of the section (the head, the finding lines,
  `and <n> more`, the starts that were not compared, the closing sentence) and the notes stay byte for byte as they are.
- E8: A `user` longer than **32 UTF-8 bytes** (`useradd` makes names of at most 32 characters from its default ASCII set; the agent cuts
  names to 256 bytes; a forged or other producer may send up to 1,024) is shown as its longest prefix of at most 32 bytes that ends on a
  character boundary, cut before it is escaped, followed by the plain text ` (cut)` (`(gekürzt)`) after the code span and before
  ` (lowest of several)`. A name of exactly 32 bytes is not cut. The UID number beside it still
  tells two names with the same prefix apart.

## Non-functional requirements

- Security (area 12 of `.squad/project.md`): the `user` is external text a forged or compromised agent chooses (the wire allows a short text of
  1,024 bytes); it is written only through the report's code-span path and on the page only as an `ExternalText`, never in a heading, a table
  cell or a `MarkupString`. The `uid` is a number the backend formats itself.
- Memory: each listener of a finding gains one external text, with the caps of record 0116 (option 7: 20 findings per kind, 64 listeners
  each) unchanged. **In the run** the decoded findings hold each `user` as stored, before any cut: up to 1,024 bytes (about 2 KiB as .NET
  text) beside the command, so the section grows from about 5.5 MB to **about 11 MB**, kept once across the service (one report runs at a
  time) ([Detection, alerts and reports](../../docs/areas/detection-and-reports.md), *Limits of the report*). **On the page**, which keeps the
  composed document and the runs of its external texts per circuit, the cost of a text is set by its runs (`LogText.Runs` gives each
  stretch of plain or escaped characters its own `TextRun` and string), and the worst case alternates a plain and an escaped character.
  Measured by Security (`LogText.Runs` over 2,560 texts, retained heap): a 64-byte alternating name 4,848 bytes, a 1,024-byte alternating
  command about 22.9 KiB. With the shown name cut to 32 bytes (E8, at most 32 runs) the names add about **2.5 KB a listener, about 6.5 MB
  per circuit** (at most 3 KiB a name, about 7.9 MB, pinned by a committed test); the commands' share, measured, is **about 60 MB per circuit**, not the 16 MB the documents
  state today (a figure from before #304 that counted characters, not runs). The area documents, record 0116 and `.squad/project.md` state
  the measured figures, up to about 68 MB per circuit for the section plus the render tree; bounding the commands' share is a follow-up issue
  ([Web UI](../../docs/areas/web-ui.md), *Limits and errors*).
- Time: no new read; the fields come from the listener lists the section already decodes.
- Compatibility: no wire, storage or schema change; stored lists without the fields read as E3. Page and download stay byte-identical to
  `vandoxd report` for the same data and language.
- Configuration and logs: none.

## Acceptance criteria

- [ ] AC1: A listener with an owner and a `uid` is written as `<proto> <address>:<port>, PID <pid> <command>, socket UID <uid> <user>`, the user
  as a code span of external text (S1).
- [ ] AC2: A listener without an owner and with a `uid` is written as `<proto> <address>:<port>, no owner, socket UID <uid> <user>` (S2).
- [ ] AC3: A listener with `mixed_uids` true ends with ` (lowest of several)` after the user (or after the number when there is no user) (S3, E2).
- [ ] AC4: A listener with `uid` and no `user` ends with `socket UID <uid>` and writes no code span and no dash for the user (E2).
- [ ] AC5: A listener without `uid` ends with `socket UID unknown`, with or without an owner; `uid` 0 and 4294967295 are written as numbers (E3, E4).
- [ ] AC6: The `user` goes through the same escaping and fencing as the command: a user holding a backtick run, `<b>x</b>`, U+202E (in the test
  input as an escape, asserted to hold its backslash), a control character, a backslash sequence or spaces only gives the code span that a
  command with the same text gets; a decimal user is shown unchanged (E1, E5, E6).
- [ ] AC7: The report page shows the user name as the text of an `ExternalText` in a `code` element, no `MarkupString`; the download equals
  `vandoxd report` output byte for byte for data that holds S1 to S3, E1 and E8 (S4, E1).
- [ ] AC8: The German report writes `Socket-UID <uid>`, ` (niedrigste von mehreren)`, `Socket-UID unbekannt` and ` (gekürzt)` (S5, E8).
- [ ] AC9: In the golden reports `testdata/report/forensics-en.md` and `forensics-de.md` every listener line gains exactly its suffix and
  nothing else; together they show S1, S2, S3, E2 and E3 (at least one existing listener line keeps no `uid`), and every non-listener line of
  the section and the notes stay byte-identical (E7).
- [ ] AC10: A `user` of 33 bytes or more is shown cut to its longest prefix of at most 32 bytes on a character boundary (a multi-byte character
  that crosses byte 32 is left out whole), followed by ` (cut)`; a 32-byte name is shown whole without it; the cut is made before the escaping,
  so a name of 1,024 control characters shows 32 escapes (E8).

## Out of scope

- The UIDs of connection entries, of the full inventory (`listeners`) and of the incident view's connection data.
- The holder process's own user (the process snapshot has it; record 0115, option 14), and any explanation in the report beyond the label "socket UID" (the area document explains it).
- A change of the comparison: a change of the UID on a known endpoint stays no new listener (record 0115, option 16).
- Alerts on a listener's UID.

## Open questions

- Which wording tells the socket's UID from the process's user? Answered: record 0115 (*Consequences*) says the `uid` is whose socket it is, not
  whose process, and a root-created socket held by a `www-data` worker reads root; the label is therefore `socket UID`, not `user` or `UID`.
- What does the line say when `uid` is absent: nothing, or "unknown"? Answered: record 0115 and the wire format say an absent `uid` reads as
  "UID unknown", never as root; the line says so (E3).
- How is the memory of the section kept bounded? Answered: by the 32-byte cap of the shown name (E8), not by lower caps of findings, which
  record 0116 (option 7) rejected for the forensic window they cost. Record 0116 is unreleased (no tag contains it) and is edited in place
  by the Lead with the measured figures; `.squad/project.md` (security areas 3 and 12) is a project file a product PR may change
  (`AGENTS.md`), updated by the Dev. The understated figure for the commands predates this feature; this pull request corrects the text it
  rewrites, and a follow-up issue bounds that share.
- Does the storage need a change? Answered: no; the stored lists are serialized `Listener` entries and already carry the fields
  (`ListenerQueries.SerializeListeners`, `ListenerMarkWriter`).

## Area documents

- [Detection, alerts and reports](../../docs/areas/detection-and-reports.md): *New listeners* (a finding lists each listener with the socket
  UID), *Content and format* (the listener line forms of AC1 to AC5, AC8 and AC10 in both languages, and one sentence: the socket UID is the
  UID of the socket's creator, not the user of the PID shown, so `PID 200 apache2, socket UID 0 root` is expected for a daemon that binds as
  root and runs its workers as another user; [Agent](../../docs/areas/agent.md), *Socket UIDs*, and record 0115, *Consequences*), *External
  text in the report* (the user names of the new listeners are external text, shown cut to 32 bytes), *Limits of the report* (about 11 MB in the run).
- [Web UI](../../docs/areas/web-ui.md): *Report page* (the user names of the new listeners are external text in an `ExternalText`) and
  *Limits and errors* (the measured per-circuit figures of the section, up to about 68 MB).
- Outside `docs/areas/`: record 0116, option 7 (the measured figures, Lead) and `.squad/project.md`, security area 3 (the per-circuit figure)
  and area 12 (the user name among the section's external texts, with its cut) (Dev).

## Challenge

- MAJOR 1 (the memory NFR misstated record 0116 and gave no numbers): accepted. The shown name is cut before escaping (E8, AC10; 32 bytes
  after the Security plan review, below); the NFR states measured numbers (the run holds the decoded names uncut, about 11 MB once per
  service), and record 0116 option 7 and `.squad/project.md` are in scope (*Area documents*), the latter allowed in a product PR.
- MINOR 2 (spaces-only rendering): accepted; E6 now says each space is written as the escape `\u0020`, exactly as for a command of spaces
  only (the first version lost the escape when the file was written).
- MINOR 3 (golden lines): accepted; E7 and AC9 say every listener line gains exactly its suffix, every other line stays byte-identical, and an
  existing listener line keeps no `uid` so E3 is in the goldens.
- MINOR 4 (creator's UID, not the PID's user): accepted; the sentence is in the *Content and format* outline of *Area documents*.
- Security plan review, finding 1 (the page's cost of a name is set by its runs; the 64-byte cap added about 12.4 MB per circuit, measured):
  the cap is lowered to 32 bytes, the length `useradd` allows, and the NFR states measured figures (E8, AC10, NFR *Memory*).
