# Spec: Overview page

Source: Issue #48
Status: Approved by Lead

## Problem

The operator wants to see at a glance whether the monitored server is healthy. Today the start page (`/`) shows a fixed text and three
links, and nothing tells an hour-old calm value or a backfill on its way apart from a healthy server, against the guarantee that missing
data is never normal (`.squad/project.md`, *Guarantees*).

## Scenarios

- S1: Given live data, when the user opens *Overview* (`/`), then it shows *Data freshness*, *Services*, *Key figures* and *Incidents*
  in this order, below `Updated <time>`.
- S2: Given the page is open, when 30 seconds have passed, then it redraws with new data without a page load.
- S3: Given the agent backfills after a backend outage, when the user opens the page, then *Data freshness* says a backfill is running
  and shows its progress until it is done.
- S4: Given the agent stopped sending, when the user opens the page, then the last live data, *Services* and every key figure older
  than 5 minutes are marked `stale`.
- S5: Given a unit is `failed`, when the user opens the page, then it is listed first and marked as failed.
- S6: Given an incident whose window has not ended, then *Incidents* lists it as open with a link to its page.

## Errors and edge cases

- E1: Empty store or no agent ever stored: *Data freshness* shows `No live data has arrived yet. Install the agent, or import logs as
  the README describes.`, *Services* `No service state is stored.`, each key figure `unknown`, *Incidents* `No incidents are stored.
  Run vandoxd incidents after an import.`; the page is no error.
- E2: Current records are stored as backfilled: the newest batch received (AC1) shows that data arrives.
- E3: An open sequence gap exists but no batch has been received for more than 5 minutes: the backfill line reads `Backfill stalled:
  data from <start> to <end> is missing, no data received since <time>.` instead of the running form.
- E4: A figure the latest sample lacks (MariaDB `down` or without `Memory_used`, no PSI on the kernel) shows `unknown` (MariaDB: its
  availability `down` or `not answering`), never 0. A swap total of 0 shows `no swap`; a share whose total is 0 shows no percentage.
- E5: A refresh fails or passes its 10-second deadline: the page keeps the values it shows and adds `Refresh failed at <time>; the values
  are from <time>.` (before any values: `; no values have been read yet.`); the next refresh runs on schedule and removes the line when it succeeds.
- E6: A refresh due while one runs is skipped.
- E7: The user leaves the page or closes it: refreshing stops and a running read is cancelled. While the live connection is lost the
  page makes no store call; it refreshes at once when the connection is back.
- E8: Unit names, mount points and incident victims are external text from the agent: shown only through the web UI's external text
  display ([Web UI](../../docs/areas/web-ui.md), *External text*), so a forged name carries no markup or direction control.
- E9: A unit or mount removed from the agent's configuration: a unit is listed until its latest record is more than 2 hours older than
  the newest service record, a mount until its latest value is more than 10 minutes older than the newest file system value.
- E10: Many records: the page shows at most 256 units and 16 mounts, with `and <n> more` for the rest (incident windows never overlap,
  so at most one incident is open).
- E11: The agent's numbers jumped ([Agent spool](../../docs/areas/agent-spool.md), *Sequence numbers*), so an open sequence gap spans
  numbers never issued: the page shows no count of missing records.
- E12: Open sequence gaps of another agent ID than that of the newest batch received (an agent ID replaced) are not counted as a backfill.
- E13: A time after now (a clock ahead, a forged or mis-zoned kill) shows its age as `ahead by <age>`; an incident whose first kill lies
  after now is never open and is marked `dated in the future` where it is listed. Records captured more than 90 seconds after now (the
  live allowance) are not used for freshness, services, figures or MariaDB, so a clock far ahead cannot hide current data.
- E14: An agent is known but its newest records were removed by retention: *Last data received* shows `unknown` marked `stale`, not
  the E1 text.
- E15: Refreshes are no session activity: an idle open overview ends its session 1 hour after the last activity
  ([Web login](../../docs/areas/web-login.md), *Sessions*).

## Non-functional requirements

- N1: Prerendered (the first response) the page shows its heading and `Loading…` and reads nothing; it reads once interactive and then
  on each refresh ([Web UI](../../docs/areas/web-ui.md), *Pages and render mode*).
- N2: Every store read of the page takes a slot of the views' shared read gate ([Web UI](../../docs/areas/web-ui.md), *Store reads of
  the views*), and one refresh makes a fixed number of store calls, independent of how much data is stored.
- N3: Memory per circuit is bounded by the caps of E10.
- N4: No new configuration option, route, endpoint or listener; the page stays behind the login like every page.
- N5: Times are shown as `yyyy-MM-dd HH:mm:ss` UTC, as the other views; an age as whole seconds below a minute, whole minutes below an
  hour, hours and minutes below a day, days and hours above (`45 s`, `12 min`, `3 h 5 min`, `2 d 4 h`), and a negative age as in E13.
  Sizes in binary units with one decimal (`3.2 GiB`), shares in percent with one decimal.

## Acceptance criteria

- AC1: *Data freshness* shows the capture time and age of the newest `metric` record stored as live among the newest 20,000 metric
  records, or `before <time>` (the oldest of them) when none of them is live, marked `stale` when older than 5 minutes; and the receive
  time and age of the newest batch received, read as that of the agent's highest-numbered record (the live stream's latest batch); with
  no agent, the text of E1. (S1, S4, E1, E2, E13, E14, N5)
- AC2: While an open sequence gap of the agent ID of the newest batch exists and a batch was received within 5 minutes, *Data
  freshness* shows `Backfill running: data from <start> to <end> is missing.`, the earliest start and latest end of those gaps, followed
  by the spool line of AC3; no count of missing records is shown, also for a gap over a jump of 10^15 numbers; otherwise the stalled
  form of E3; with no such gap `No backfill running.` (S3, E3, E11, E12)
- AC3: *Data freshness* shows, from the latest self sample with its time, the agent's spool size (`agent_spool_bytes`) and the time of
  its oldest unsent record (sample time minus `agent_spool_age_seconds`), or `nothing unsent` when that age is 0; the sample is marked
  `stale` past 5 minutes; without one, `unknown`. (S1, S3, E1, E4)
- AC4: *Services* lists each unit of E9 with its latest `active_state` and `sub_state`, the time it last became active and its restart
  count; units whose state is not `active` come first, then by unit name in ordinal order; a `failed` unit is marked; names are shown as
  external text. The section is marked `stale` with the newest batch's receive time when no batch was received for more than 5
  minutes, and a unit whose latest record is more than 1 hour 5 minutes old is marked `stale` with its time. (S4, S5, E1, E8, E9, E10)
- AC5: *Key figures* shows, each from its latest value of the agent's sources (`system`, `mariadb`, `agent`) with that value's time: available memory (`mem_available_bytes`, and its
  share of `mem_total_bytes`), swap used of total, PSI `pressure_avg60` of memory, CPU and I/O (`some`, and `full` for memory and I/O),
  available space and its share per mount of E9 (`filesystem_available_bytes`, `filesystem_size_bytes`), and MariaDB's availability,
  `Threads_connected` of `max_connections` (shown only as a non-negative integer, else `unknown`), `Memory_used` and
  `mariadb_memory_max_bytes`; a value older than 5 minutes is marked
  `stale`, a missing one `unknown`, a zero total as E4. (S1, S4, E1, E4, E9, E10, E13)
- AC6: *Incidents* lists the open incidents — the page's own term: window end after now and first kill not after now — newest first
  with number (linked to `incidents/<number>`), first kill and victim, else `No open incident.`, and always the latest stored incident
  with its number, first kill and a link (marked as E13 when its kill lies after now), or the text of E1. (S6, E1, E8, E10, E13)
- AC7: The page refreshes every 30 seconds over its live connection without a page load and shows `Updated <time>`; a failed refresh
  keeps the values with the line of E5; refreshes never overlap, pause while the connection is lost and stop when the page is left.
  (S2, E5, E6, E7)
- AC8: Prerendered the page shows its heading and `Loading…` and makes no store call; one refresh makes at most a fixed number of store
  calls, each through the shared read gate. (N1, N2, N3)
- AC9: While the page shows the overview, its circuit's activity does not extend the session; a session whose last activity was more
  than 1 hour ago ends at the next check even though the page kept refreshing. (E15)

## Out of scope

- Health thresholds that color a figure (rule engine #56, alerts view #62); the page judges only freshness and `failed`.
- Charts and history (#49, #52); the list of gaps with causes (#54).
- Building incidents: they are still built by `vandoxd incidents` only (live incidents: #53).
- A count or percentage of backfill progress (E11).
- N4 needs no criterion: nothing is added.

## Open questions

- What is an "open incident"? Answered: incidents have no such state for the user; the page defines its own (AC6), and shows the
  latest incident beside it because the manual builder rarely leaves one open (#53 changes that).
- What is backfill "progress"? Answered: the gaps' span and the agent's spool figures (AC2, AC3); a count is wrong across a jump (E11).
- Which PSI window? Answered: `avg60`, steadier than `avg10` for a page read every 30 seconds.
- Which agent? Answered: the backend accepts one agent ID ([Ingest endpoint](../../docs/areas/ingest-endpoint.md)); figures are the
  newest agent records of any ID, the backfill that of the newest batch's ID (E12).
- "MariaDB memory": Answered: `Memory_used` beside `mariadb_memory_max_bytes` ([Agent MariaDB collector](../../docs/areas/agent-mariadb.md)).

## Area documents

- New `docs/areas/web-ui-overview.md` (Overview page): sections, texts, sources, staleness, refresh, caps, memory per circuit.
- `docs/areas/web-ui.md` and `storage-reading.md`: the overview and its reads; the full list is in `plan.md`.

## Challenge

- M1 (count includes never-issued numbers) — accepted, verified in agent-spool and storage-writing: the count is dropped; AC2 shows the
  gaps' span and the spool line of AC3, E11 names the jump, and AC2 asserts no count over a jump of 10^15 numbers.
- M2 (Services without freshness) — accepted, stricter: the section is `stale` after 5 minutes without a batch, a unit past 1 hour 5
  minutes (hourly re-record plus the 30-second interval, with margin) (AC4, S4).
- Minor 1 — accepted: live data is the newest live `metric` capture time, data received the batch receive time; future times E13, N5.
- Minor 2 — accepted: E4 shows `no swap` for a zero total and no share over a zero total.
- Minor 3 — accepted: E12, AC2. Minor 4 — accepted: AC3. Minor 5 — accepted: E9 retires mounts after 10 minutes.
- Minor 6 — accepted: AC6 defines the page's own term without a first kill after now, E13 marks it; with that, no window cap is needed.
- Minor 7 — accepted: no store call while disconnected (E7, AC7). Minor 8 — accepted: the E1 texts point to the agent, the import and
  `vandoxd incidents`.

## Amendments

| Criterion | Change | Reason | Step | Decided by |
| --------- | ------ | ------ | ---- | ---------- |
| AC1 | an agent without stored records shows `unknown`, not the E1 text (E14) | plan challenge minor 4 | 2 | Lead |
| AC5 | `max_connections` only as a non-negative integer; records more than 90 s after now ignored (E13) | Security B2, plan challenge major 1 | 2 | Lead |
| AC9 | added: the overview's refreshes do not extend the session (E15) | Security B1 | 2 | Lead |
