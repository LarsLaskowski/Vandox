# Spec: Report a known listener endpoint whose socket UID changes

Source: Issue #305
Status: Approved by Product Manager

## Problem

Since #279 every listener entry of a `connection_snapshot` carries the socket's UID (`uid`, the lowest when the endpoint's sockets have
several) and `mixed_uids` ([Agent](../../docs/areas/agent.md), *Socket UIDs*). The listener check compares inventories by protocol,
address and port only ([plan](plan.md), *Verified facts* 1 and 3), so an endpoint that stays in the inventory while the user behind it changes is never reported: neither by the agent nor by the backend's
comparison across an agent start (record 0116, *A new listener*).

The case that matters most is a **takeover**: a service on an unprivileged port (MariaDB on 3306, Plesk on 8443) stops or crashes and
another local user binds the same endpoint before the next run; the endpoint is known, so nothing is reported, although the socket now
belongs to someone else. The issue's own example cannot happen as written (Q1).

The report must not become noise for endpoints that legitimately share sockets of several users: a mixed state that persists is
reported once, when it begins, not every run.

## Scenarios

- S1: Given the inventory of the last run holds `tcp 0.0.0.0:3306` with socket UID 110 (`mysql`), when MariaDB stops and a
  process of UID 1001 binds `0.0.0.0:3306` before the next run, then that run's snapshot is full and reports `tcp 0.0.0.0:3306` as a
  listener whose socket UID changed, from 110 to 1001, with the current owner's PID and command when known; it is not a new listener.
- S2: Given `udp 0.0.0.0:5353` has socket UID 107 and no `mixed_uids`, when a process of UID 1001 opens a second
  socket on it with `SO_REUSEADDR`, then the next snapshot is full and reports the endpoint as changed: UID 107 before, UID 107 now,
  `mixed_uids` now true.
- S3: Given `tcp 0.0.0.0:8443` bound to a device has socket UID 1001, when a root process binds the same endpoint on
  another device, then the endpoint is reported as changed: UID 1001 before, UID 0 now with `mixed_uids` true.
- S4: Given an endpoint already reported as mixed or changed, when later runs see the same lowest UID and the same
  `mixed_uids`, then nothing more is reported for it and those runs stay regular snapshots.
- S5: Given stored agent snapshots that report changed listeners, when the operator opens the
  forensics report, then the section *New listeners* shows them in a finding found by the agent, each endpoint with its current socket
  UID and user and its socket UID before, below the new listeners of the same run when it has any.
- S6: Given the agent's last snapshot before a start holds `tcp 0.0.0.0:3306` with UID 110 and its first
  snapshot after the start holds it with UID 1001, when the backend compares the start (record 0116), then the finding after the agent
  start lists the endpoint as changed, as in S1, with its UID before.

## Errors and edge cases

- E1: A socket of a mixed endpoint leaves. When the lower UID's socket left, so the lowest UID rises (UIDs 0 and 1001, then 1001 alone),
  the endpoint is reported as changed (the user left on it is a takeover as in S1); when the higher one left (`mixed_uids` turns false,
  the lowest UID stays), nothing is reported.
- E2: An entry without `uid` on either side (built only from inode-0 lines, a 1.0 agent's or an imported snapshot): no UID change is
  reported for it; absent means unknown, never root.
- E3: A second socket that comes and goes between runs (an mDNS client of another user that starts often): reported each time the
  mixed state begins again, like a listener that disappears and returns is new again; at most once per run per endpoint.
- E4: An unconnected UDP socket on a port from 32768 to 60999: never reported as changed, as it is never reported as new.
- E5: A comparison `none` or `start`: no change is reported; `privileged`: only endpoints on ports 1 to 1023 are compared for a
  change, as for new listeners.
- E6: More than 64 changed endpoints in one run (a local user who opens many device-bound or `SO_REUSEADDR` sockets): the first 64 in
  inventory order are listed and the rest is a count, which also counts the entries the record's size budget left out; the run never
  fails, and the list stands after `new_listeners` and before `listeners` in the budget, so it can cut `listeners` only as
  `new_listeners` already can.
- E7: `/etc/passwd` unreadable: decimal user names; the change is still detected.
- E8: An entry whose UID changes and that is new in the same run cannot happen: a new endpoint has no previous UID and is reported as
  new only.
- E9: Two different UIDs replace each other on an already mixed endpoint while the lowest stays the same (UIDs 0 and 1001, then 0 and
  1002): not detectable from the lowest UID and the flag; a stated residual.
- E10: A shared endpoint that keeps toggling (E3) on a busy server: its findings found by the agent, which hold changes only, are capped
  apart from the findings with a new listener, so they never push a new listener out of the report.

## Non-functional requirements

- Memory: the previous inventory keeps, per key, the lowest UID and the flag in addition to the key (at most 4,096 entries); no list of
  UIDs per endpoint (record 0115, option 16).
- Wire format: the new fields are optional additions to `connection_snapshot` of format 1.1, which is unreleased (only v0.1.0 is
  tagged), so they extend 1.1 in place; a reader that does not know them ignores them.
- Storage: the agent's stored marks keep the first 64 reported changes and their count; a run with changes only must be selectable as a
  finding, so the schema gains a step (today's finding index covers `new_count > 0` only). Snapshots stored before the step read as having
  no reported changes. A start compared before the step keeps its stored outcome without changes until it is compared again (a snapshot
  stored below it), as record 0116 states for any change of the comparison rules; no rebuild of stored comparisons.
- Report memory: a third kind of findings (changes only, capped at 20) adds to the page's bound of the *New listeners* section; the plan
  measures it and updates the figure in `.squad/project.md` and the web UI area.
- Security: every UID and user name is the kernel's number and the `/etc/passwd` name under the existing rules; user names in the
  report stay code spans cut to 32 bytes; no log line names a UID, a user or an address.
- Logs: no new log line.
- Configuration: none.

## Acceptance criteria

- [ ] AC1: For a compared run (`all`, or `privileged` on ports 1 to 1023), an endpoint in both inventories, with a `uid` in both, whose
  lowest UID differs or whose `mixed_uids` turned from absent to true, is reported in the snapshot as changed with its previous `uid`
  and `mixed_uids` and its current entry, and makes the snapshot full; the change is decided on the UID numbers, so it is reported
  with decimal user names when `/etc/passwd` cannot be read, and an endpoint new in the run is reported only as a new listener, never
  as changed (S1, S2, S3, E1, E5, E7, E8).
- [ ] AC2: An endpoint whose lowest UID and `mixed_uids` are the same as in the previous run, whose `mixed_uids` only turned false, or
  that lacks a `uid` in either run is not reported as changed and does not by itself make the snapshot full (S4, E1, E2).
- [ ] AC3: An unconnected UDP endpoint on ports 32768 to 60999, a `none` or `start` comparison and an endpoint above 1023 under a
  `privileged` comparison report no change (E4, E5).
- [ ] AC4: At most 64 changed endpoints are listed per snapshot, in inventory order, the rest counted together with the entries the size
  budget left out; the list is filled after `new_listeners` and before `listeners`; a run with more succeeds (E6).
- [ ] AC5: A changed endpoint that disappears from the change list and qualifies again in a later run is reported again, at most once per
  run (E3).
- [ ] AC6: Both decoders accept the new fields and read a snapshot without them as having no changes; they refuse, with a field error, a
  changed-endpoint list or its omitted count unless `listener_check` is `all` or `privileged` (the rule of `new_listeners`) and a
  changed entry without a current `uid` or without a previous UID (so also a previous mixed flag without a previous UID); an omitted
  count of 0 reads as absent and a negative one is malformed, as for `new_listeners_omitted` (NFR wire format, E2).
- [ ] AC7: The forensics report's section *New listeners* lists the changes the agent reported in its finding found by the agent, and
  the changes the backend found for a compared agent start in the finding after that start; a run or start with changes but no new
  listener is a finding too. A changed endpoint's line is `<proto> <address>:<port>, PID <pid>` (or `, no owner` as today), without the
  owner's command, then today's socket UID text (current UID and user, ` (lowest of several)` when it is mixed now), followed by `, before: socket UID <uid>` (`, vorher: Socket-UID <uid>`) and ` (lowest of several)`
  when it was mixed before. A finding holds up to 64 new listeners and, separately, up to 64 changed endpoints, each list with its own
  `and <n> more`; the finding head gives both counts. Findings found by the agent that hold changes only are capped at the latest 20
  apart from the 20 that hold a new listener and the 20 after an agent start, so a run of change-only findings never removes a finding
  with a new listener (S5, S6, E10).
- [ ] AC8: When the backend stores the snapshots around an agent start (record 0116), it applies the rules of AC1 to AC3 to the two
  stored snapshots and keeps the changed endpoints with the comparison, with the same result whatever the order of storing (S6).

## Out of scope

- E9: tracking the set of UIDs per endpoint.
- An alert or Telegram notification for a change (no alert rules are built, detection and reports area).
- The process's effective user of a listener (the process snapshot has it, record 0115, option 14).
- Reporting the change of an endpoint's owner PID or command.

## Open questions

- Q1: Can a local user add a socket to a known endpoint such as `0.0.0.0:22`, as the issue says? Answered: no, for an endpoint held by
  a socket not bound to a device: upstream v5.15 `inet_csk_bind_conflict` and `udp_lib_lport_inuse` make two sockets conflict unless
  both are bound to different devices, a reuse-port pair needs one UID, and ports below 1024 need `CAP_NET_BIND_SERVICE`. A second UID
  needs two device-bound sockets, two UDP sockets with `SO_REUSEADDR` (mDNS on 5353) or `CAP_CHOWN`. Measured on kernel
  6.18.44 ([plan](plan.md), *Verified facts*).
- Q2: Where should the change be visible: in the agent's snapshot only, also in the forensics report's agent findings, or also in the
  backend's comparison across an agent start? Answered: by the Product Manager, option C: all three, the agent's snapshot,
  the forensics report's findings and the backend's comparison across an agent start with the agent's rules (record 0116) (S5, S6, AC7,
  AC8).
- Q3: Is a persistent mixed state reported every run? Answered: no, only at the run where the endpoint's lowest UID or its flag
  changes (S4), the same flood bound as a returning listener (record 0115, *The listener check*).

## Area documents

- [Agent](../../docs/areas/agent.md), *Connections*: *The listener check* gains the changed-listener rule, its cap and that it makes a
  full snapshot; *Socket UIDs* and *Residuals* replace "a listener whose UID changes between two runs is not reported" with the rule and
  the residual E9 and the bind-conflict facts of Q1, and the residuals the new-listener check already has, now for a takeover too: a
  local user who keeps more than 4,096 listeners open forces `privileged`, so a takeover on a port from 1024 up is not reported while
  it lasts; a `none` run still becomes the base, so a takeover that happens before it is never reported.
- [Wire format](../../docs/areas/wire-format.md), *`connection_snapshot`*: the new optional fields, their validation errors and the
  version note of 1.1.
- [Detection, alerts and reports](../../docs/areas/detection-and-reports.md), *New listeners*: the backend's comparison of a start
  also finds changed endpoints with the agent's rule, the agent's findings carry its reported changes, a finding with changes only;
  *Content and format*: the finding head with the count of changed endpoints and the line of a changed endpoint (UID and user now,
  UID before), and the section's closing sentences.
- [Storage](../../docs/areas/storage.md): what `connection_marks` (the agent's reported changes, cut to 64, and their count) and
  `listener_comparisons` (the changes of a start) keep, and that rows from before the change read as no changes.

## Challenge

- MAJOR 1 (change-only findings share the agent's 20 slots with new-listener findings): accepted. Findings found by the agent that hold
  only changes get a third cap of the latest 20, apart from the 20 with a new listener and the 20 after an agent start (record 0116's
  reason for separate caps); a finding with a new listener stays in its kind and also lists its changes. Added E10, AC7's cap sentence,
  and the report-memory requirement.
- MINOR 1 (S5 shows a previous user the wire does not carry): accepted, S5 aligned with AC7: the previous socket UID only, no previous
  user.
- MINOR 2 (the mixed flag is not shown): accepted, AC7 fixes the line: ` (lowest of several)` for now as today, and
  `, before: socket UID <uid>` with ` (lowest of several)` when it was mixed before.
- MINOR 3 (no validation rule named): accepted, AC6 names the rules: the list and its count need `listener_check` `all` or
  `privileged`, a count of at least 1, a current and a previous UID per entry, and a previous mixed flag needs the previous UID.
- MINOR 4 (place in the size budget): accepted, E6 and AC4: the list follows `new_listeners` and comes before `listeners`, and the
  entries the size budget leaves out add to its omitted count.
- MINOR 5 (finding index and stored comparisons): accepted, the storage requirement names a schema step for change-only marks and
  states that starts compared before it are not recomputed until compared again, as record 0116 says for any rule change.
- MINOR 6 (inherited residuals): accepted, both added to the *Residuals* entry of the agent area document.
- MINOR 7 (64 per finding shared or separate): accepted, AC7: 64 new listeners and 64 changed endpoints separately, each list with its
  own `and <n> more`, the head with both counts.
