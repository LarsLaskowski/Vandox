# Spec: Show the socket's UID and user in the new-listener findings

Source: Issue #304
Status: Draft

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
- E6: A `user` of spaces only is written as the report writes such a text (` ` per space inside the code span); on the page a `user` whose
  escaped text is longer than 2,048 displayed characters is cut with the logs view's marker, as a command is.
- E7: The rest of the section does not change: the head, the finding lines, `and <n> more`, the starts that were not compared (which list no
  listener), the closing sentence and the notes.

## Non-functional requirements

- Security (area 12 of `.squad/project.md`): the `user` is external text a forged or compromised agent chooses (the wire allows a short text of
  1,024 bytes); it is written only through the report's code-span path and on the page only as an `ExternalText`, never in a heading, a table
  cell or a `MarkupString`. The `uid` is a number the backend formats itself.
- Memory: each listener of a finding gains one external text, so the bounds of the section in
  [Detection, alerts and reports](../../docs/areas/detection-and-reports.md) (*Limits of the report*, about 5.5 MB in the run) and
  [Web UI](../../docs/areas/web-ui.md) (*Limits and errors*, up to about 16 MB per circuit) grow by the user names: at most 64 names of up to
  1,024 bytes per finding, 40 findings. The caps stay (record 0116, option 7); the area documents state the new bounds.
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
  input as an escape), a control character, a backslash sequence or spaces only gives the code span the command with the same text would get;
  a decimal user is shown unchanged (E1, E5, E6).
- [ ] AC7: The report page shows the user name as the text of an `ExternalText` in a `code` element, capped like the command, no `MarkupString`;
  the download equals `vandoxd report` output byte for byte for data that holds S1 to S3 and E1 (S4, E1, E6).
- [ ] AC8: The German report writes `Socket-UID <uid>`, ` (niedrigste von mehreren)` and `Socket-UID unbekannt` (S5).
- [ ] AC9: The golden reports `testdata/report/forensics-en.md` and `forensics-de.md` show S1, S2, S3, E2 and E3; every other line of the
  section is unchanged (E7).

## Out of scope

- The UIDs of connection entries, of the full inventory (`listeners`) and of the incident view's connection data.
- The holder process's own user (the process snapshot has it; record 0115, option 14), and any explanation beyond the label "socket UID".
- A change of the comparison: a change of the UID on a known endpoint stays no new listener (record 0115, option 16).
- Alerts on a listener's UID.

## Open questions

- Which wording tells the socket's UID from the process's user? Answered: record 0115 (*Consequences*) says the `uid` is whose socket it is, not
  whose process, and a root-created socket held by a `www-data` worker reads root; the label is therefore `socket UID`, not `user` or `UID`.
- What does the line say when `uid` is absent: nothing, or "unknown"? Answered: record 0115 and the wire format say an absent `uid` reads as
  "UID unknown", never as root; the line says so (E3).
- Do the caps of the section have to fall to keep the old memory bounds? Answered: no. Record 0116 (option 7) kept 20 findings per kind against a
  higher page bound because only a signed-in client opens a circuit and the bound stays fixed; one more short text per listener keeps it fixed.
  The new numbers are restated in the area documents (NFR *Memory*).
- Does the storage need a change? Answered: no; the stored lists are serialized `Listener` entries and already carry the fields
  (`ListenerQueries.SerializeListeners`, `ListenerMarkWriter`).

## Area documents

- [Detection, alerts and reports](../../docs/areas/detection-and-reports.md): *New listeners* (a finding lists each listener with the socket
  UID), *Content and format* (the listener line forms of AC1 to AC5 and AC8 in both languages), *External text in the report* (the user names of
  the new listeners are external text), *Limits of the report* (the section's bound with the user names).
- [Web UI](../../docs/areas/web-ui.md): *Report page* (the user names of the new listeners are external text in an `ExternalText`) and
  *Limits and errors* (the per-circuit bound of the section with the user names).
