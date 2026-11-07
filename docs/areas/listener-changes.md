# Listener changes

## Scope

The new listeners and the listeners whose socket UID changed, across agent starts and as the agent found them, and how the forensics
report shows them in its section **New listeners**. The agent reports a **new listener** by comparing a run's listener inventory with the
previous run's, held in memory, so the first run after an agent start is a baseline and a listener that opened while the agent was
stopped (an update, a crash, a reboot with a service that starts at boot) is never reported by the agent ([Agent
connections](agent-connections.md)). The backend closes that gap when it stores the snapshots (record 0116) and shows the result, with the
agent's own findings, in the [Forensics report](forensics-report.md). Operators rely on it to spot a takeover of a port.

## Comparison across agent starts

- **Which snapshots are compared.** For each agent ID, the first connection snapshot after each agent start (`listener_check` `start`,
  the agent's first run after it started or the machine rebooted) with the last one of the same agent before it. Both are picked by the
  record's `seq`, the agent's persistent counter, which increases strictly across restarts and reboots, never by capture time, which is
  shown only (a clock can step back after a reboot). No other pair is compared.
- Only a **usable** snapshot takes part: origin `agent`, source `connections`, an agent ID, and the fields `listener_check`, `inventory`
  and `tables` of format 1.1 ([Wire format records](wire-format-records.md)). Snapshots of another source, of a 1.0 agent and imported
  ones are never a side of a comparison, never a finding and not counted.
- **Outcomes** of a start (a function of the two snapshots alone, with the agent's rules; the result is stored, [Storage
  writing](storage-writing.md), *Listener marks and comparisons*):
  - `not compared: no earlier inventory`: no usable snapshot of the agent has a lower `seq` (a first installation, or the earlier part is
    not stored);
  - `not compared: inventories not comparable`: a snapshot's `inventory` is `skipped` (a table line was skipped), the two read different
    tables, or the stored earlier snapshot is missing or does not decode (which never fails the batch that stores it);
  - `not compared: ports 1 to 1023 not complete`: either `inventory` is `cut`, so a listener on a port below 1024 may be missing from the
    list;
  - `all ports`: both inventories are `all`: every listener is compared;
  - `ports 1 to 1023 only`: either is `privileged` (the list was cut for size or at the inventory cap, but holds every listener on ports 1
    to 1023): only the listeners on ports below 1024 are compared.

  A start that is not compared is always a finding and says why; it never reads as no new listener.
- **A new listener** is a listener of the later snapshot whose protocol and local address and port are not in the earlier one (the agent's
  key: the address is compared as a value of its own family, so `0.0.0.0` and `::ffff:0.0.0.0` are different addresses). A change of the
  owner is not new, an unconnected UDP socket on a port from 32768 to 60999 is not listed (the agent's rule), and a listener without a
  local endpoint is ignored. A compared start without a new listener or a changed endpoint writes no finding. A finding lists at most 64
  new listeners in inventory order, each with protocol, local address and port and, when known, the owner's PID and command and the socket
  UID with its user, then `and <n> more`.
- **A changed endpoint** is a listener that both snapshots hold (the same key) whose socket UID changed, decided with the agent's rule over
  the two stored lists: the compared ports as for new listeners (`privileged`: ports 1 to 1023), not an unconnected UDP socket on a port
  from 32768 to 60999, a `uid` on both sides, and the lowest UID differs or `mixed_uids` turned from false to true (a `mixed_uids` that
  turns false reports nothing). Of a key that occurs twice in a list the first entry counts. The changed endpoint is the later snapshot's
  entry (owner, UID and user now) with the earlier one's UID and `mixed_uids` as its previous values; a new key is a new listener only. A
  finding lists at most 64 in the later snapshot's order and counts the rest. A start with changed endpoints is a finding even without a
  new listener; a start that is not compared has none. Starts compared before the change keep their stored result without changes
  (record 0116).
- **Order independence.** The comparison of a start depends on the records only: it is computed again when a snapshot is stored below a
  stored start (backfill sends the current data first), so the same records give the same section whatever order or batches they were
  stored in, and a resent batch changes nothing. The findings are each listed once.

## The agent's own findings

Every stored usable snapshot whose `listener_check` is `all` or `privileged` and that reports at least one new listener is a finding
**found by the agent**, with the agent's own list (the first 64 stored) and its omitted count; a `none` or `start` snapshot reports none.
The changed listeners the agent reported (the first 64 stored and their count) are listed in the same finding; a snapshot that reports
changed listeners and no new listener is a finding of its own kind, **found by the agent with changes only**.

## What the report reads

The report reads the derived tables `connection_marks` and `listener_comparisons` ([Storage](storage.md), [Storage reading and log
exploration](storage-reading.md), *Listener history*) after its first pass over the incidents, by four small reads: the counts of the
lowest 64 agent IDs, the latest 20 findings after an agent start, the latest 20 findings of the agent and the latest 20 findings of the
agent with changes only. No connection snapshot is read or decoded by the report; the write path decodes the snapshots around an agent
start when it stores them.

## Caps

- The head names at most the 64 lowest agent IDs.
- The findings after an agent start and the findings found by the agent are capped **separately** at the latest 20 each (by capture time,
  then agent ID, then `seq`), so a server whose passive FTP produces an agent finding every minute cannot push a restart finding out. The
  agent findings with changes only are a third kind, capped at the latest 20 apart from the other two, so a run of them never removes a
  finding with a new listener (a finding with a new listener stays in its kind and lists its changes too).
- Within the section the findings of both agent kinds stand together by agent ID, then `seq`. The omitted rows stay stored.
- A local user who toggles the mixed state of a shared `SO_REUSEADDR` UDP endpoint (avahi on 5353, under `all`) in about 40 runs fills the
  20 slots with change-only findings and pushes an earlier takeover out of the 20 latest; the cap note shows the number left out.

## Report section

The section `## New listeners` (`## Neue Listener`) stands after the memory consumers and before the outages of the [Forensics
report](forensics-report.md), *Content and format*, whose formats of times and numbers it uses.

- Without any stored usable agent connection snapshot it is one paragraph: `No agent connection snapshot stored: new listeners unknown.`
  (`Kein Verbindungs-Snapshot eines Agenten gespeichert: neue Listener unbekannt.`).
- Otherwise one list item per agent (the head): the agent ID, the number of its connection snapshots with the first and last capture time,
  and its agent starts: `<n> connection snapshots from <time> to <time>; <n> agent starts: <n> compared on all ports, <n> on ports 1 to 1023
  only, <n> not compared` (German: `<n> Verbindungs-Snapshots von <time> bis <time>; <n> Agentenstarts: <n> auf allen Ports verglichen, <n>
  nur auf Ports 1 bis 1023, <n> nicht verglichen`).
- Then the findings after an agent start, then those found by the agent (both agent kinds together), each one list item with its
  listeners as items below it:
  - `<time>, agent <id>, after an agent start (compared with <time>), all ports: <n> new` or `…, ports 1 to 1023 only: <n> new`
    (`nach einem Agentenstart (verglichen mit <time>), alle Ports` / `nur Ports 1 bis 1023`, `<n> neu`);
  - `<time>, agent <id>, after an agent start, not compared: <reason>` with the reason `no earlier inventory`, `ports 1 to 1023 not
    complete` or `inventories not comparable` (`nicht verglichen: kein früheres Inventar`, `Ports 1 bis 1023 unvollständig`, `Inventare
    nicht vergleichbar`);
  - `<time>, agent <id>, found by the agent, all ports: <n> new` (`vom Agenten gefunden`);
  - when a finding has changed endpoints (listed and omitted together, `m` at least 1) its head ends with `, <m> with a changed socket
    UID` (`, <m> mit geänderter Socket-UID`) after the new count; a finding with changes only reads `…: 0 new, 2 with a changed socket
    UID`;
  - a listener: `<proto> <address>:<port>, PID <pid> <command>` or `<proto> <address>:<port>, no owner` (`kein Besitzer`), the address in
    the backend's numeric form (an IPv4-mapped address as `::ffff:` and the dotted IPv4 address), followed by the socket's UID: `, socket
    UID <uid> <user>` (`Socket-UID`), the user as a code span, or `, socket UID <uid>` when no user name is known, or `, socket UID
    unknown` (`Socket-UID unbekannt`) when the entry has no UID; a user name longer than 32 UTF-8 bytes is shown cut with ` (cut)` (`
    (gekürzt)`) after it, and ` (lowest of several)` (` (niedrigste von mehreren)`) ends the line when the endpoint's sockets have several
    UIDs (the UID shown is then the lowest of them). The socket UID is the UID of the socket's creator, not of the user of the PID shown (a
    daemon that binds as root and runs as `www-data` reads `socket UID 0 root`), see [Agent connections](agent-connections.md), *Socket
    UIDs*; `and <n> more` (`und <n> weitere`) for the listeners left out. After the new listeners and their `and <n> more` come the changed
    endpoints, one item each, and their own `and <n> more`;
  - a changed endpoint: `<proto> <address>:<port>, PID <pid>` (`, PID <pid>` in German too) or `<proto> <address>:<port>, no owner`,
    without the owner's command, then the socket UID text of a listener (current UID, user as a code span cut to 32 bytes with ` (cut)`,
    ` (lowest of several)` when mixed now), then `, before: socket UID <uid>` (`, vorher: Socket-UID <uid>`) and ` (lowest of several)`
    (` (niedrigste von mehreren)`) when it was mixed before; the previous user is not carried by the wire and not shown.
- When no finding holds a new listener (listed or omitted; a finding with changes only does not count) the section ends with `No new
  listener found.` (`Kein neuer Listener gefunden.`), or, when a start was not compared, `No new listener found among the compared
  inventories.` (`Kein neuer Listener unter den verglichenen Inventaren gefunden.`).
- The report's notes name each cap hit and the number left out: `Only the latest 20 findings after an agent start are listed; <n> earlier
  ones are left out.`, the same for the findings of the agent, `Only the latest 20 findings of the agent with changed socket UIDs only are
  listed; <n> earlier ones are left out.` (`Nur die letzten 20 Funde des Agenten mit nur geänderten Socket-UIDs sind aufgeführt; <n>
  frühere fehlen.`) and `Only the first 64 agent IDs are listed; <n> more are left out.`, each with its German text.

## External text

The owner commands of the listeners, the user names of their sockets and the agent IDs of the section are external text, written only as
code spans with the escapes of [Forensics report](forensics-report.md), *External text in the report*, in the Markdown, the download and
on the page alike. A user name is cut to its first 32 UTF-8 bytes on a character boundary before it is escaped, so the Markdown, the
download and the page show the same prefix. The agent ID stands inside a sentence of the section (and in the head item), never in a heading
or a table cell: the sentence is split at the place of the agent ID and the code span stands between the parts. The protocol, endpoint,
PID, times and counts are plain text. The backend's log holds no listener, command or address.

## Limits and errors

- **Memory**: the section keeps the head of at most 64 agent IDs (under 64 KB) and at most 20 findings after an agent start, 20 found by
  the agent and 20 found by the agent with changes only, each with at most 64 listeners of a command of up to 1,024 UTF-8 bytes and a user
  name of up to 1,024 bytes: a finding is at most about 280 KB, and each of the three kinds may also hold up to 64 changed entries, which
  are stored and read with an empty command (a user name of 1,024 bytes as UTF-16 is 2 KiB, so an entry is at most about 2.2 KB and 64 of
  them about 140 KB). That is 40 findings of about 420 KB (16.8 MB) and 20 change-only findings of about 140 KB (2.8 MB), so the section is
  at most about 20 MB in the run (computed from these bounds, not measured as a whole; on the page, where every text also carries its runs,
  up to about 36 MB per circuit, [Report page](web-ui-report-page.md), *Limits and errors*).
- **Time**: the section adds four reads of the narrow derived tables and decodes no payload, and may add at most 5 seconds to a run. Over a
  database with one more year of connection snapshots (one agent, 525,600 marks written through the store's write path in batches of
  1,440, 100 agent starts, 10,000 snapshots with agent findings, 762 MiB) the counts take 101 to 155 ms, the findings after an agent start
  9 ms (first read, then 1 to 2 ms) and the findings of the agent 3 ms (then 1 to 2 ms), and these three together 136 ms on their first
  run, on the development host in a Debug build ([BENCHMARKS.md](../BENCHMARKS.md), *Listener reads*); the fourth read, the agent's
  findings with changes only, uses its own partial index `connection_marks_changes` and was not measured.
- **Residuals**: a listener that opened and closed while the agent was stopped is never seen; a start whose first snapshot is lost (a gap,
  a record the scheduler dropped) is not seen as a start, and its successor was compared by the agent with the lost inventory; findings are
  "latest" by capture time, so a clock that stepped back may keep an older finding instead of a newer one. No alert is raised by a finding
  (the rules are not built; backfilled data never alerts, [Detection, alerts and reports](detection-and-reports.md), *Live data and
  backfill*). A compromised agent can forge snapshots and so findings, like any record.

## Related decisions

- [0148](../decisions/0148-agent-marks-its-first-snapshot-after-a-start-and-what-its-listener-list-holds.md) — why the agent marks its first
  snapshot after a start.
- [0116](../decisions/0116-listeners-across-agent-starts-compared-when-stored.md) — why the storage layer compares across the start when it
  stores the snapshots.
- [0149](../decisions/0149-new-listeners-section-capped-per-finding-kind-changed-endpoints-without-command.md) — why the report shows these
  findings with the agent's own under separate caps.
- [0143](../decisions/0143-new-listener-check-against-the-last-inventory-bounded-privileged-ports-first.md) — why the agent's connection
  snapshots carry a bounded listener check.
- [0148](../decisions/0148-agent-marks-its-first-snapshot-after-a-start-and-what-its-listener-list-holds.md) — why a snapshot states its
  inventory.

## Not here

- The agent's own listener check, `inventory`, socket UIDs and the unconnected UDP rule: [Agent connections](agent-connections.md); the
  fields of `connection_snapshot`: [Wire format records](wire-format-records.md).
- The rows `connection_marks` and `listener_comparisons`, when they are written and how many listeners they keep: [Storage
  writing](storage-writing.md), *Listener marks and comparisons*; how they are read: [Storage reading and log
  exploration](storage-reading.md), *Listener history*; their schema: [Storage](storage.md).
- The rest of the report and its formats: [Forensics report](forensics-report.md); the report page: [Report page](web-ui-report-page.md).

## Implementation

`Vandox.Core.Reports` (`ListenerComparer`, `ListenerComparison`, `ListenerOutcome`, `ListenerFinding`, `ListenerFindingKind`,
`ListenerTally`, `ListenerReport`, and `ReportLimits.MaxShownUserBytes` for the 32-byte cut); the composer of `Vandox.Backend`
(`Reports/ReportComposer`) for the section; the web UI's `GatedListenerHistoryReader`. The comparison is called by the storage write path
([Storage writing](storage-writing.md)).
