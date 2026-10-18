# Spec: Report listeners that appeared while the agent was stopped

Source: Issue #278
Status: Draft

## Problem

The connection collector of `vandox-agent` (#33, record 0115) reports a **new listener** only by comparing a run's listener inventory with
the previous successful run's inventory. That previous inventory exists only in memory, so the first run after every agent start is a
baseline (`listener_check` `none`): a listener that opened while the agent was stopped (an update, a crash, a reboot with a service that
starts at boot) is never reported. The operator relies on that report to notice an unexpected service, and a listener that turns up
across a reboot on a privileged port is the case that matters most for the server's security. The agent area document and record 0115
list this as a residual and name #278 as the backend comparison that closes it.

Checked against the code and documents (head 46ddcc3):

- Confirmed: the agent's first run after a start is full and compares nothing (`internal/connstats/inventory.go`, `compare`: `prev == nil`
  gives `none`); its snapshots carry the inventory (`listeners`, ports 1 to 1023 first) and `aggregates_incomplete`.
- Refuted for today: "the inventories before and after the restart are both stored". The agent drops every record until the spool
  exists (#38), and the sender (#39) and the ingest API (#40) are not built (`docs/areas/agent.md`, *Status*; `PortRoutingMiddleware`
  answers 404 on the ingest listener). The records **will** be stored once v0.2.0's ingest exists (one row per record with agent ID,
  boot ID, capture time and payload, `docs/areas/storage.md`, *Schema*); the comparison works on stored records and can be tested by
  storing snapshots through the storage layer.
- Not true as worded: "when both inventories are complete". The wire field `complete` is false for every **regular** snapshot
  (`aggregates_only`), and almost every snapshot before a restart is regular, so `complete` would almost never allow the all-port
  comparison. This spec reads "complete" as "the inventory holds every listener" (see *Open questions*).
- Today nothing in the backend shows new listeners, not even the ones the agent reports in `new_listeners`: no page, report or command
  reads `listener_check` or `new_listeners` (only the model and its validation do). See *Open questions*, question 1.

## Scenarios

The wording a user reads depends on where the findings are shown (*Open questions*, question 1, escalated); the scenarios state the
content of a finding.

- S1: Given the agent ran with a TCP listener inventory that holds every listener, was stopped, and a service opened `0.0.0.0:23` before it
  started again with an inventory that holds every listener, when the backend compares the last stored inventory before the start with
  the first stored one after it, then it reports `tcp 0.0.0.0:23` as a new listener after an agent start, with the owner's PID and
  command from the snapshot after the start, the capture times of both snapshots, and the comparison "all ports".
- S2: Given the same, with a new TCP listener on `[::]:8443`, then `tcp6 [::]:8443` is reported as new as well (all ports compared).
- S3: Given the inventory before or after the start does not hold every listener (cut at 4,096 or for the record's size) but both hold every
  listener on ports 1 to 1023, when the backend compares them, then only listeners on ports 1 to 1023 are compared: a new `0.0.0.0:25` is
  reported, a new `0.0.0.0:8443` is not, and the comparison reads "ports 1 to 1023 only".
- S4: Given the server rebooted while the agent was stopped (the boot ID changes), then the comparison runs exactly as after an agent
  restart within one boot.
- S5: Given a listener that exists before and after the start, on the same protocol, address and port but with another owner, then it is
  not reported.
- S6: Given the agent sent current data first and backfilled the older snapshots afterwards (or a batch is resent), when both snapshots
  are stored, in either order, then the finding is the same and exists once.

## Errors and edge cases

- E1: No stored snapshot of the agent before the start (its first start ever, or every earlier snapshot lost): nothing is compared, and
  the result says that no earlier inventory exists, never that there are no new listeners.
- E2: The first run after the start failed or timed out (a gap): the first **successful** snapshot after the start is the one compared.
- E3: Two starts with no successful snapshot in between: the first successful snapshot after the second start is compared with the last
  one before the first start.
- E4: Either inventory does not hold every listener on ports 1 to 1023 (cut inside the privileged part), a table line was skipped in either
  run, or the two runs read a different set of tables (the agent's own reasons for `none`): nothing is compared, and the result says so with
  the reason in words, never that there are no new listeners.
- E5: An unconnected UDP socket on a port from 32768 to 60999 (the kernel's default local port range): not reported, as the agent does not
  report it.
- E6: More than 64 new listeners: the first 64 in inventory order are listed and the rest is a count ("and N more"), as in the agent's
  `new_listeners` and `new_listeners_omitted`.
- E7: Several agents (agent IDs) in one database: each agent's snapshots are compared only with that agent's.
- E8: Snapshots without the 1.1 fields (an agent of format 1.0) or of another source (the imported legacy `lsof` snapshots, origin `import`):
  never used as either side of a comparison.
- E9: Records between the two snapshots are missing (a recorded gap: the spool dropped data, the backend was down past the spool's bound):
  the comparison runs against the last stored snapshot before the start, and both capture times show the span it covers.
- E10: The owner's `command` is text the process chose (up to 1,024 bytes, any characters the wire format allows): shown as plain text,
  never interpreted as markup or a link, with the escaping rules of the surface that shows it.
- E11: Both inventories at their maximum of 4,096 listeners each: the comparison stays within a fixed memory bound and does not slow a page or
  report noticeably (*Non-functional requirements*).
- E12: A listener that opened and closed again while the agent was stopped: not seen by either inventory and not reported (*Out of scope*).

## Non-functional requirements

- Memory: one comparison holds at most two inventories of 4,096 listeners each; reading the findings for a time range stays within the
  bounds of the surface that shows them (the record query's limit of 10,000 records).
- Security: the findings are behind the web login like every page (security area 3) when shown in the web UI; text a process chose
  (`command`) is never interpreted; no new listener, route reachable without login, or secret.
- Compatibility: wire format 1.1 is unreleased (only `v0.1.0`, with 1.0, is tagged), so a field the agent adds to `connection_snapshot`
  for this change extends 1.1 in place; snapshots of a 1.0 agent and imported ones read as before.
- Determinism: the findings follow from the stored records alone (record 0008), in capture order, independent of arrival order.
- No alert and no Telegram message (the rule engine is #56, Telegram #60); the guarantee that backfilled data never raises an alert by
  itself is untouched.
- Configuration: none new.
- Logs: no line per listener and no process text or address in the backend's log.

## Acceptance criteria

- [ ] AC1: For each agent ID, the first successful snapshot of source `connections` after each agent start (an agent restart or a reboot) is
  compared with the last successful one of the same agent before that start, by capture time; no other snapshot pair is compared by this
  feature (S1, S4, E2, E3, E7).
- [ ] AC2: When both inventories hold every listener, every port is compared and the finding says "all ports" (S1, S2).
- [ ] AC3: When either inventory does not hold every listener but both hold every listener on ports 1 to 1023, only those ports are
  compared and the finding says "ports 1 to 1023 only" (S3).
- [ ] AC4: When no earlier snapshot exists, or the privileged part of either inventory is not whole, or the agent's own rules would make no
  comparison (a skipped table line, a different set of tables), nothing is compared and the finding states which of these applied; it never
  reads as "no new listeners" (E1, E4).
- [ ] AC5: A listener is new when its protocol, address and port are in the later inventory and not in the earlier one; an owner change is
  not new, and an unconnected UDP socket on ports 32768 to 60999 is not reported (S5, E5).
- [ ] AC6: A finding lists at most 64 new listeners in inventory order, each with protocol, local address and port and, when known, the
  owner's PID and command, plus the count of the rest (E6).
- [ ] AC7: The same stored records give the same findings, once each, whatever order they were stored in and however often a batch was
  resent (S6, E9).
- [ ] AC8: Snapshots of another source or without the 1.1 fields are never a side of a comparison (E8).
- [ ] AC9: The owner's command is shown as plain text (E10).
- [ ] AC10: One comparison holds at most two inventories of 4,096 listeners each and no other snapshot's inventory (E11).
- [ ] AC11: Where the findings are shown: to be written from the Product Manager's answer to question 1 (S1–S6).

## Out of scope

- E12: listeners that open and close while the agent is stopped; no inventory can show them.
- Alerting or a Telegram message on a finding (#56, #60), and the network view with the listener inventory over time (#51).
- Storing agent snapshots in the backend: the spool, the sender and the ingest API (#38, #39, #40).
- Comparisons the agent already makes between its own consecutive runs (record 0115).

## Open questions

1. Where does the user see these findings? Today the backend shows no new listener at all, not even the agent's own `new_listeners`, and
   agent snapshots reach it only once #38 to #40 exist. **Escalated to the Product Manager** (the options are in the Lead's result); AC11
   and the wording of the scenarios follow from the answer.
2. What does "both inventories are complete" mean? Answered: the inventory holds every listener the run saw, not the wire field `complete`,
   which is false for every regular snapshot (`docs/areas/wire-format.md`, *`connection_snapshot`*; `docs/areas/agent.md`, *Regular and full
   snapshots*), so the issue's literal reading would almost never compare all ports. This matches the agent's own rule, which compares all
   ports unless an inventory was cut (`internal/connstats/inventory.go`, `compare`).
3. Which listeners count as new? Answered: the agent's rule (record 0115, `docs/areas/agent.md`, *The listener check*): key protocol, address
   and port, an owner change is not new, unconnected UDP on 32768 to 60999 is excluded, at most 64 listed plus a count. A backend comparison
   with other rules would report differently across a restart than within a run.
4. How does the backend know that a snapshot is the first after an agent start? Answered: as a requirement, not a design. The stored data
   does not say it today (`listener_check` `none` also follows a skipped line, a changed set of tables or a cut inside the privileged part,
   and the boot ID changes only on a reboot), and neither does it say whether a cut inventory's privileged part is whole when
   `aggregates_incomplete` is set for another reason (owners unknown). The plan decides how the backend learns both; wire format 1.1 is
   unreleased (`git ls-remote --tags origin`: `v0.1.0` only), so an added `connection_snapshot` field is allowed without a new minor.
5. Is a finding on backfilled data an alert? Answered: no; this feature raises no alert (record 0022 applies once alerting exists).

## Area documents

- [Detection, alerts and reports](../../docs/areas/detection-and-reports.md): a new section on new listeners after an agent start: which
  snapshots are compared, the three outcomes (all ports, ports 1 to 1023 only, not compared with its reason), the rule for a new listener,
  the limit of 64, order independence, and its residuals.
- [Agent](../../docs/areas/agent.md), *Connections*, *Residuals*: the restart residual points to the backend comparison instead of #278; any
  field the agent adds for it.
- [Wire format](../../docs/areas/wire-format.md), *`connection_snapshot`*: any field added for it.
- The area document of the surface the Product Manager chooses (question 1): [Web UI](../../docs/areas/web-ui.md) or the forensics report
  in *Detection, alerts and reports*.
