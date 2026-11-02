# Spec: Report a known listener endpoint whose socket UID changes

Source: Issue #305
Status: Draft

## Problem

Since #279 every listener entry of a `connection_snapshot` carries the socket's UID (`uid`, the lowest when the endpoint's sockets have
several) and `mixed_uids` ([Agent](../../docs/areas/agent.md), *Socket UIDs*). The listener check compares inventories by protocol,
address and port only (`internal/connstats/inventory.go`, `compare`, lines 41-69: the previous run keeps the keys and nothing else), so
an endpoint that stays in the inventory while the user behind it changes is never reported: neither by the agent nor by the backend's
comparison across an agent start (record 0116, *A new listener*). The operator reading the forensics report sees no trace of it.

The case that matters most is a **takeover**: a service on an unprivileged port (MariaDB on 3306, Plesk on 8443) stops or crashes and
another local user binds the same endpoint before the next run; the endpoint is known, so nothing is reported, although the socket now
belongs to someone else. The issue's example is narrower than the kernel allows (see *Open questions*, Q1): a local user cannot add a
socket to `0.0.0.0:22` held by an unbound `sshd`. A second UID can join an endpoint only when both sockets are bound to devices,
both UDP sockets set `SO_REUSEADDR` (mDNS on 5353 is the legitimate example), or a holder of `CAP_CHOWN` gives one socket away;
`SO_REUSEPORT` groups need one UID (record 0115, option 16).

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
  forensics report, then the section *New listeners* shows them as a finding found by the agent, each endpoint with its socket UID and
  user before and now, beside the new listeners of the same run.
- S6: Given the agent's last snapshot before a start holds `tcp 0.0.0.0:3306` with UID 110 and its first
  snapshot after the start holds it with UID 1001, when the backend compares the start (record 0116), then the finding after the agent
  start lists the endpoint as changed, as in S1.

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
  inventory order are listed and the rest is a count; the run never fails and no other list loses entries for it beyond the record's
  size budget.
- E7: `/etc/passwd` unreadable: the user names are decimal UIDs, the change is still detected on the numbers.
- E8: An entry whose UID changes and that is new in the same run cannot happen: a new endpoint has no previous UID and is reported as
  new only.
- E9: Two different UIDs replace each other on an already mixed endpoint while the lowest stays the same (UIDs 0 and 1001, then 0 and
  1002): not detectable from the lowest UID and the flag; a stated residual.

## Non-functional requirements

- Memory: the previous inventory keeps, per key, the lowest UID and the flag in addition to the key (at most 4,096 entries); no list of
  UIDs per endpoint (record 0115, option 16).
- Wire format: the new fields are optional additions to `connection_snapshot` of format 1.1, which is unreleased (only v0.1.0 is
  tagged), so they extend 1.1 in place; a reader that does not know them ignores them.
- Storage: agent findings and comparisons of stored snapshots carry the changes under the existing caps (64 entries per finding, 20
  findings per kind); stored snapshots from before the change read as having no UID changes.
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
- [ ] AC4: At most 64 changed endpoints are listed per snapshot, in inventory order, the rest counted; a run with more succeeds (E6).
- [ ] AC5: A changed endpoint that disappears from the change list and qualifies again in a later run is reported again, at most once per
  run (E3).
- [ ] AC6: Both decoders accept the new fields, apply their validation rules, and read a snapshot without them as having no changes
  (NFR wire format, E2).
- [ ] AC7: The forensics report's section *New listeners* lists the changes the agent reported in its finding found by the agent, and
  the changes the backend found for a compared agent start in the finding after that start, each endpoint with protocol, address and
  port, owner, the current socket UID and user and the previous UID, under the existing caps (64 endpoints per finding, 20 findings per
  kind); a run or start with changes but no new listener is a finding too (S5, S6).
- [ ] AC8: When the backend stores the snapshots around an agent start (record 0116), it applies the rules of AC1 to AC3 to the two
  stored snapshots and keeps the changed endpoints with the comparison, with the same result whatever the order of storing (S6).

## Out of scope

- E9: tracking the set of UIDs per endpoint.
- An alert or Telegram notification for a change (no alert rules are built, detection and reports area).
- The process's effective user of a listener (the process snapshot has it, record 0115, option 14).
- Reporting the change of an endpoint's owner PID or command.

## Open questions

- Q1: Can a local user add a socket to a known endpoint such as `0.0.0.0:22`, as the issue says? Answered: no, for an endpoint held by
  a socket not bound to a device. Upstream v5.15 `inet_csk_bind_conflict` (`net/ipv4/inet_connection_sock.c`, lines 157-181) and
  `udp_lib_lport_inuse` (`net/ipv4/udp.c`, lines 140-153) treat two sockets as conflicting unless both are bound to devices (and to
  different ones), and a reuse-port pair must have the same UID; ports below 1024 also need `CAP_NET_BIND_SERVICE`. A second UID on an
  endpoint therefore needs both sockets device-bound, two UDP sockets with `SO_REUSEADDR`, or `CAP_CHOWN`. The report is still useful
  for the takeover (S1) and the cases above. To be confirmed by measurement in the plan's *Verified facts*.
- Q2: Where should the change be visible: in the agent's snapshot only, also in the forensics report's agent findings, or also in the
  backend's comparison across an agent start? Answered: by the Product Manager, option C: all three, the agent's snapshot,
  the forensics report's findings and the backend's comparison across an agent start with the agent's rules (record 0116) (S5, S6, AC7,
  AC8).
- Q3: Is a persistent mixed state reported every run? Answered: no, only at the run where the endpoint's lowest UID or its flag
  changes (S4), the same flood bound as a returning listener (record 0115, *The listener check*).

## Area documents

- [Agent](../../docs/areas/agent.md), *Connections*: *The listener check* gains the changed-listener rule, its cap and that it makes a
  full snapshot; *Socket UIDs* and *Residuals* replace "a listener whose UID changes between two runs is not reported" with the rule and
  the residual E9 and the bind-conflict facts of Q1.
- [Wire format](../../docs/areas/wire-format.md), *`connection_snapshot`*: the new optional fields, their validation errors and the
  version note of 1.1.
- [Detection, alerts and reports](../../docs/areas/detection-and-reports.md), *New listeners*: the backend's comparison of a start
  also finds changed endpoints with the agent's rule, the agent's findings carry its reported changes, a finding with changes only;
  *Content and format*: the finding head with the count of changed endpoints and the line of a changed endpoint (UID and user now,
  UID before), and the section's closing sentences.
- [Storage](../../docs/areas/storage.md): what `connection_marks` (the agent's reported changes, cut to 64, and their count) and
  `listener_comparisons` (the changes of a start) keep, and that rows from before the change read as no changes.
