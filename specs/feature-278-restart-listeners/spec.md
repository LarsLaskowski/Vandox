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

And the operator sees no new listener anywhere today, not even the ones the agent does report: no page, report or command of the backend
reads `listener_check` or `new_listeners` (only the model and its validation do). The Product Manager decided (open question 1) that the
forensics report shows both in one new section.

Checked against the code and documents (head 46ddcc3):

- Confirmed: the agent's first run after a start is full and compares nothing (`internal/connstats/inventory.go`, `compare`: `prev == nil`
  gives `none`); its snapshots carry the inventory (`listeners`, ports 1 to 1023 first) and `aggregates_incomplete`.
- Refuted for today: "the inventories before and after the restart are both stored". The agent drops every record until the spool
  exists (#38), and the sender (#39) and the ingest API (#40) are not built (`docs/areas/agent.md`, *Status*; `PortRoutingMiddleware`
  answers 404 on the ingest listener). The records **will** be stored once v0.2.0's ingest exists (one row per record with agent ID,
  boot ID, capture time and payload, `docs/areas/storage.md`, *Schema*); the report works on stored records and can be tested by storing
  snapshots through the storage layer.
- Not true as worded: "when both inventories are complete". The wire field `complete` is false for every **regular** snapshot
  (`aggregates_only`), and almost every snapshot before a restart is regular, so `complete` would almost never allow the all-port
  comparison. This spec reads "complete" as "the inventory holds every listener" (open question 2).

## Scenarios

The report is the forensics report of `vandoxd report` and of the page `/report` with its Markdown download (one block model, record
0108), in English (`-lang en`, the default) and German (`de`). The new section **New listeners** (`## New listeners`, German
`## Neue Listener`) stands after *Memory consumers* and before *Outages*. Each **finding** is one list item: the capture time of the
snapshot, the agent ID, how it was found, what was compared and how many listeners are new, followed by one nested item per new listener.
The exact phrases are in the table of AC11.

- S1: Given agent `web-1` ran with a listener inventory that holds every listener, was stopped, and a service opened `0.0.0.0:23` before
  the agent started again with an inventory that holds every listener, when the operator runs `vandoxd report`, then *New listeners* holds
  a finding at the capture time of the first snapshot after the start, `after an agent start (compared with <time of the last snapshot before
  it>)`, `all ports`, `1 new`, and the nested item `tcp 0.0.0.0:23`, PID and command of the owner from the snapshot after the start.
- S2: Given the same with a new TCP listener on `[::]:8443`, then `tcp6 [::]:8443` is listed as well (all ports compared).
- S3: Given the inventory before or after the start does not hold every listener (cut at 4,096 or for the record's size) but both hold
  every listener on ports 1 to 1023, then the finding reads `ports 1 to 1023 only`; a new `0.0.0.0:25` is listed, a new `0.0.0.0:8443` is
  not.
- S4: Given the server rebooted while the agent was stopped (the boot ID changes), then the finding is made exactly as after an agent restart
  within one boot.
- S5: Given a listener that exists before and after the start on the same protocol, address and port but with another owner, then it is not
  listed, and a comparison without a new listener writes no finding (it counts in the section's head, S8).
- S6: Given the agent sent current data first and backfilled the older snapshots afterwards (or a batch was resent), when the report is made
  after both are stored, in either order, then the finding is the same and appears once.
- S7: Given a stored snapshot of the agent whose own check was `all` or `privileged` and that reports new listeners (`new_listeners`,
  `new_listeners_omitted`), then *New listeners* holds a finding at its capture time, `found by the agent`, `all ports` or `ports 1 to 1023
  only` as the agent's `listener_check` says, the count, and the listeners the agent listed with their owner.
- S8: Given agent snapshots are stored, then the section starts with a head: the number of the agent's connection snapshots with the first
  and last capture time, and the number of agent starts with how many of them were compared on all ports, on ports 1 to 1023 only and not
  at all. When there is no finding and every start was compared, the section says `No new listener found.`; when a start was not
  compared, it says `No new listener found among the compared inventories.`

## Errors and edge cases

- E1: No stored snapshot of the agent before the start (its first start ever, or every earlier snapshot lost): the finding reads `after an
  agent start`, `not compared: no earlier inventory`, never that there are no new listeners.
- E2: The first run after the start failed or timed out (a gap): the first **successful** snapshot after the start is the one compared.
- E3: Two starts with no successful snapshot in between: the first successful snapshot after the second start is compared with the last one
  before the first start, and that is one agent start in the head.
- E4: Either inventory does not hold every listener on ports 1 to 1023 (cut inside the privileged part): `not compared: ports 1 to 1023 not
  complete`; a table line was skipped in either run, or the two runs read a different set of tables (the agent's own reasons for `none`):
  `not compared: inventories not comparable`. Never that there are no new listeners.
- E5: An unconnected UDP socket on a port from 32768 to 60999 (the kernel's default local port range): not listed, as the agent does not
  report it.
- E6: More than 64 new listeners in one finding: the first 64 in inventory order are listed and the rest is `and <n> more` (for the agent's
  findings, its `new_listeners_omitted`).
- E7: Several agents (agent IDs) in one database: each agent's snapshots are compared only with that agent's, and each finding names its agent.
- E8: Snapshots without the 1.1 fields (an agent of format 1.0) or of another source (the imported legacy `lsof` snapshots, origin `import`):
  never a side of a comparison and never a finding; they do not count in the head.
- E9: Records between the two snapshots are missing (a recorded gap: the spool dropped data, the backend was down past the spool's bound):
  the comparison runs against the last stored snapshot before the start, and both capture times show the span it covers.
- E10: Hostile owner text: the owner's `command` is text the process chose (up to 1,024 bytes, any character the wire format allows) and the
  agent ID is the agent's; both are written only as code spans by the report's rules for external text (security area 12), so no character
  of them is read as Markdown, HTML or a link, on the page or in the download. Addresses and ports are the producer's numeric form.
- E11: Huge input: both inventories at their maximum of 4,096 listeners each, a server that reports a short-lived listener every minute
  (passive FTP, record 0115) for a year, many agent IDs: the report keeps at most the fixed amounts of *Non-functional requirements*; over a
  cap the section says which and how many findings or agents it left out, in the section and in *Notes*.
- E12: No connection snapshot of an agent stored (an archive of imported logs only, today's state): the section says `No agent connection
  snapshot stored: new listeners unknown.`, never `No new listener found.`
- E13: A run on the page that passes its deadline of 2 minutes (more snapshots to read): the page shows its existing timeout text; the
  command has no deadline.
- E14: A listener that opened and closed again while the agent was stopped: not seen by either inventory and not listed (*Out of scope*).

## Non-functional requirements

- Memory: the report reads every stored connection snapshot of an agent one at a time, in capture order, and keeps across the run per agent
  the keys of one inventory (at most 4,096) for at most 64 agent IDs (an agent ID first seen after that gets no finding and is counted in
  the notes), and at most the latest 100 findings with at most 64 listeners each (the earlier ones counted in the notes), so the additional
  memory stays within a fixed bound that the plan states in MB beside the report's existing 60 MB.
- Time: the run grows with the stored connection snapshots (up to about 1 MiB of JSON each, one a minute, four a minute under memory
  pressure); the plan states the cost and measures it next to the report's existing figure, and the page's deadline of 2 minutes is
  unchanged.
- Security: the report page and its download are behind the web login (security area 3); external text follows *External text in the
  report* (security area 12); no new listener, route, secret or configuration.
- Compatibility: wire format 1.1 is unreleased (only `v0.1.0`, with 1.0, is tagged), so a field the agent adds to `connection_snapshot` for
  this change extends 1.1 in place; snapshots of a 1.0 agent and imported ones read as before. Existing sections of the report do not change.
- Determinism: the findings follow from the stored records alone (record 0008), in capture order, independent of arrival order; the report
  stores nothing.
- No alert and no Telegram message (the rule engine is #56, Telegram #60); the guarantee that backfilled data never raises an alert by
  itself is untouched.
- Configuration: none new. Language: `en` and `de` as the report's other sections.
- Logs: no line per listener and no process text or address in the backend's log.

## Acceptance criteria

- [ ] AC1: For each agent ID, the first successful snapshot of source `connections` after each agent start (an agent restart or a reboot) is
  compared with the last successful one of the same agent before that start, by capture time; no other snapshot pair is compared by the
  backend (S1, S4, E2, E3, E7).
- [ ] AC2: When both inventories hold every listener, every port is compared and the finding reads `all ports` (S1, S2).
- [ ] AC3: When either inventory does not hold every listener but both hold every listener on ports 1 to 1023, only those ports are compared
  and the finding reads `ports 1 to 1023 only` (S3).
- [ ] AC4: An agent start that is not compared gives a finding `not compared:` with one reason: `no earlier inventory`, `ports 1 to 1023 not
  complete` or `inventories not comparable`; it never reads as no new listener (E1, E4).
- [ ] AC5: A listener is new when its protocol, address and port are in the later inventory and not in the earlier one; an owner change is not
  new, an unconnected UDP socket on ports 32768 to 60999 is not listed, and a compared start without a new listener writes no finding (S5, E5).
- [ ] AC6: A finding lists at most 64 new listeners in inventory order, each with protocol, local address and port and, when known, the owner's
  PID and command, followed by `and <n> more` for the rest (E6).
- [ ] AC7: Every stored agent snapshot whose `listener_check` is `all` or `privileged` and that reports at least one new listener gives one
  finding `found by the agent` with the agent's own list and omitted count (S7).
- [ ] AC8: The same stored records give the same section, each finding once, whatever order they were stored in and however often a batch was
  resent (S6, E9).
- [ ] AC9: Snapshots of another source or without the 1.1 fields are never a side of a comparison, never a finding and not counted (E8).
- [ ] AC10: The owner's command and the agent ID are written as code spans by the rules of *External text in the report*, so a command holding
  Markdown, HTML, a backtick run or U+202E renders as literal text on the page and in the download (E10).
- [ ] AC11: The section **New listeners** stands after *Memory consumers* and before *Outages* in `vandoxd report`, on `/report` and in its
  download, with the same text in all three, and uses these phrases (S1–S8, E12, E13):

  | Part | English | German |
  | ---- | ------- | ------ |
  | heading | `## New listeners` | `## Neue Listener` |
  | agent start | `after an agent start (compared with <time>)` | `nach einem Agentenstart (verglichen mit <time>)` |
  | agent's own | `found by the agent` | `vom Agenten gefunden` |
  | all ports | `all ports` | `alle Ports` |
  | privileged | `ports 1 to 1023 only` | `nur Ports 1 bis 1023` |
  | not compared | `not compared: no earlier inventory`, `not compared: ports 1 to 1023 not complete`, `not compared: inventories not comparable` | `nicht verglichen: kein früheres Inventar`, `nicht verglichen: Ports 1 bis 1023 unvollständig`, `nicht verglichen: Inventare nicht vergleichbar` |
  | count | `<n> new` | `<n> neu` |
  | rest | `and <n> more` | `und <n> weitere` |
  | none found | `No new listener found.` / `No new listener found among the compared inventories.` | `Kein neuer Listener gefunden.` / `Kein neuer Listener unter den verglichenen Inventaren gefunden.` |
  | no data | `No agent connection snapshot stored: new listeners unknown.` | `Kein Verbindungs-Snapshot eines Agenten gespeichert: neue Listener unbekannt.` |

  Times follow the report's format (`YYYY-MM-DD HH:MM UTC`), counts its digits; the head of S8 is written in the same style as the report's
  head.
- [ ] AC12: The report keeps at most 64 agent IDs' inventories and the latest 100 findings; over a cap the section and *Notes* name the cap and the
  number of agents or findings left out, and the page keeps its 2-minute deadline (E11, E13).

## Out of scope

- E14: listeners that open and close while the agent is stopped; no inventory can show them.
- The agent's own `none` checks within a run of the agent (a skipped line, changed tables): they are not findings; the agent logs them
  (record 0115).
- Alerting or a Telegram message on a finding (#56, #60), and the network view with the listener inventory over time (#51).
- Storing agent snapshots in the backend: the spool, the sender and the ingest API (#38, #39, #40).

## Open questions

1. Where does the user see these findings? Answered: by the Product Manager (escalation of the spec step): a section *New listeners* in the
   forensics report (`vandoxd report` and the page `/report` with its download), listing both the listeners the agent reported and those
   found across an agent start. The other options, only data for #51 and #56, or deferring behind #40 and folding into #51, were declined.
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
5. Is a finding on backfilled data an alert? Answered: no; the report raises no alert (record 0022 applies once alerting exists).
6. Does the report need `vandoxd incidents` to run first for this section? Answered: no; the section reads the stored connection snapshots
   only, not the incidents (`docs/areas/detection-and-reports.md`, *What it reads*, gains the snapshots).

## Area documents

- [Detection, alerts and reports](../../docs/areas/detection-and-reports.md), *Forensics report*: *What it reads* gains the connection
  snapshots; a new subsection on new listeners (which snapshots are compared, the three outcomes with their reasons, the rule for a new
  listener, the agent's own findings, the limit of 64, order independence); *Content and format* gains the section and its phrases in both
  languages; *External text in the report* gains the command and the agent ID; *Limits of the report* gains the caps, the memory and the time.
- [Web UI](../../docs/areas/web-ui.md), *Report page*: the page and the download show the new section (same blocks).
- [Agent](../../docs/areas/agent.md), *Connections*, *Residuals*: the restart residual points to the report's section instead of #278; any
  field the agent adds for it.
- [Wire format](../../docs/areas/wire-format.md), *`connection_snapshot`*: any field added for it.
