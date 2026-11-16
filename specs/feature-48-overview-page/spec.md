# Spec: Overview page

Source: Issue #48
Status: Draft

## Problem

The operator opens the web UI to learn whether the monitored server is healthy right now. Today the start page (`/`) shows a fixed
text and three links; the current state of the services, the key figures, open incidents and whether the shown data is current at all
can only be pieced together from the logs view, the incidents view and the report. A value that looks calm but is an hour old, or a
backfill still on its way, is not told apart from a healthy server, against the guarantee that missing data is never normal
(`.squad/project.md`, *Guarantees*).

## Scenarios

- S1: Given the agent sends live data, when the signed-in user opens *Overview* (`/`), then the page shows, in this order, the sections
  *Data freshness*, *Services*, *Key figures* and *Incidents*, and below the heading the line `Updated <time>`.
- S2: Given the page is open, when 30 seconds have passed, then the page reads the data again and redraws over its live connection,
  without a page load, and `Updated <time>` moves on.
- S3: Given the backend was off and the agent now backfills its spool, when the user opens the page, then *Data freshness* says a
  backfill is running, how many records are still missing and which span they cover, and the line updates with every refresh until the
  backfill is done.
- S4: Given the agent stopped sending, when the user opens the page, then *Data freshness* shows the last live data with its age marked
  `stale`, and every key figure older than 5 minutes is marked `stale` beside its time.
- S5: Given a unit is `failed`, when the user opens the page, then the unit is listed before every active unit and marked as failed.
- S6: Given an incident whose window has not ended, when the user opens the page, then *Incidents* lists it as open with a link to its
  page.

## Errors and edge cases

- E1: Empty store or no agent record yet: *Data freshness* shows `No live data has arrived yet.`, *Services* `No service state is
  stored.`, each key figure `unknown`, *Incidents* `No incidents are stored.`; the page is no error.
- E2: Current records arrive but are stored as backfilled (after an unclean stop they stay backfilled for the backfill plus 5 minutes,
  [Detection, alerts and reports](../../docs/areas/detection-and-reports.md), *Live data and backfill*): *Data freshness* shows, beside
  the last live data, the newest data received with its own time and age, so arriving data is not read as a stopped agent.
- E3: Records are still missing (an open sequence gap) but no data has been received for more than 5 minutes: the backfill line reads
  `Backfill stalled: <n> records missing, no data received since <time>.` instead of the running form.
- E4: A figure the latest sample lacks (no swap configured, MariaDB `down` or without `Memory_used`, no PSI on the kernel) shows
  `unknown` (MariaDB: its availability `down` or `not answering`), never 0.
- E5: A refresh fails or passes its 10-second deadline: the page keeps the values it shows and adds `Refresh failed at <time>; the values
  are from <time>.`; the next refresh runs on schedule and removes the line when it succeeds.
- E6: A refresh still running when the next one is due: the due one is skipped, so refreshes of one page never overlap.
- E7: The user leaves the page or closes it: refreshing stops and a running read is cancelled.
- E8: Unit names, mount points and incident victims are external text from the agent: shown only through the web UI's external text
  display ([Web UI](../../docs/areas/web-ui.md), *External text*), so a forged name carries no markup or direction control.
- E9: A unit removed from the agent's configuration: listed until its latest record is more than 2 hours older than the newest
  service record, then no longer.
- E10: Many records: the page shows at most 256 units, 16 mounts and 20 open incidents, with `and <n> more` for the rest.

## Non-functional requirements

- N1: Prerendered (the first response) the page shows its heading and `Loading…` and reads nothing; it reads once interactive and then
  on each refresh ([Web UI](../../docs/areas/web-ui.md), *Pages and render mode*).
- N2: Every store read of the page takes a slot of the views' shared read gate ([Web UI](../../docs/areas/web-ui.md), *Store reads of
  the views*), and one refresh makes a fixed number of store calls, independent of how much data is stored.
- N3: Memory per circuit is bounded by the caps of E10; the amount is stated in the area document.
- N4: No new configuration option, route, endpoint or listener; the page stays behind the login like every page.
- N5: Times are shown as `yyyy-MM-dd HH:mm:ss` UTC, as the other views; an age as whole seconds below a minute, whole minutes below an
  hour, hours and minutes below a day, days and hours above (`45 s`, `12 min`, `3 h 5 min`, `2 d 4 h`). Sizes in binary units with one
  decimal (`3.2 GiB`), shares in percent with one decimal.

## Acceptance criteria

- AC1: *Data freshness* shows the capture time and age of the newest record stored as live, and of the newest agent record of any
  classification; the live line's age is marked `stale` when it exceeds 5 minutes; with no agent record, `No live data has arrived
  yet.` (S1, S4, E1, E2, N5)
- AC2: While an open sequence gap exists, *Data freshness* shows `Backfill running: <n> records missing from <start> to <end>.`, `<n>`
  the sum of the missing numbers of all open sequence gaps and the span their times cover; with no data received for more than 5
  minutes the stalled form of E3; with no open sequence gap `No backfill running.` (S3, E3)
- AC3: *Data freshness* shows the agent's spool size (`agent_spool_bytes`) and the time of its oldest unsent record (sample time minus
  `agent_spool_age_seconds`) from the latest self sample, with that sample's time; without one, `unknown`. (S1, E1, E4)
- AC4: *Services* lists each unit of E9 with its latest `active_state` and `sub_state`, the time it last became active and its restart
  count; units whose state is not `active` come first, then by unit name in ordinal order; a `failed` unit is marked; names are shown as
  external text. (S5, E1, E8, E9, E10)
- AC5: *Key figures* shows, each from its latest stored value with that value's time: available memory (`mem_available_bytes`, and its
  share of `mem_total_bytes`), swap used of total, PSI `pressure_avg60` of memory, CPU and I/O (`some`, and `full` for memory and I/O),
  available space and its share per mount (`filesystem_available_bytes`, `filesystem_size_bytes`), and MariaDB's availability,
  `Threads_connected` of `max_connections`, `Memory_used` and `mariadb_memory_max_bytes`; a value older than 5 minutes is marked
  `stale`, a missing one `unknown`. (S1, S4, E1, E4, E10)
- AC6: *Incidents* lists the open incidents (window end after now) newest first with number (linked to `incidents/<number>`), first
  kill and victim, else `No open incident.`, and always the latest stored incident with its number, first kill and a link, or `No
  incidents are stored.` (S6, E1, E8, E10)
- AC7: The page refreshes every 30 seconds over its live connection without a page load and shows `Updated <time>`; a failed refresh
  keeps the values with the line of E5; refreshes never overlap and stop when the page is left. (S2, E5, E6, E7)
- AC8: Prerendered the page shows its heading and `Loading…` and makes no store call; one refresh makes at most a fixed number of store
  calls, each through the shared read gate. (N1, N2, N3)

## Out of scope

- Health verdicts or thresholds that color a figure good or bad: they come with the rule engine and the alerts view (#56, #62); the
  page judges only freshness (`stale`, `unknown`) and the unit state `failed`.
- Charts and history of the figures (History view #49, Services and MariaDB view #52); the list of all gaps with causes (#54).
- Building incidents: they are still built by `vandoxd incidents` only (live incidents: #53).
- A percentage of backfill progress: the backend does not know the backfill's starting size, only what is still missing (AC2).
- N4 needs no criterion: the change adds no option, route or listener.

## Open questions

- What is an "open incident"? Answered: incidents have no open or closed state for the user; [Incidents](../../docs/areas/incidents.md)
  calls an incident open while its window has not ended, so the page lists incidents whose window ends after now (with the manual
  builder rarely any, hence the latest incident beside them; with #53 they become current).
- What is backfill "progress"? Answered: the backend learns of missing records through open sequence gaps
  ([Detection, alerts and reports](../../docs/areas/detection-and-reports.md), *Live data and backfill*); the remaining count and span
  shrink as the backfill arrives, which is the progress shown (AC2); the agent's own spool figures complete it (AC3).
- Which PSI window? Answered: `avg60`, steadier than `avg10` for a page read every 30 seconds and still current.
- Which agent? Answered: the ingest endpoint accepts one configured agent ID ([Ingest endpoint](../../docs/areas/ingest-endpoint.md));
  the page reads the agent records stored, of any agent ID, so data of an earlier ID still counts until newer data replaces it.
- "MariaDB memory": Answered: the server's own `Memory_used` beside its theoretical maximum `mariadb_memory_max_bytes`
  ([Agent MariaDB collector](../../docs/areas/agent-mariadb.md)).

## Area documents

- New `docs/areas/web-ui-overview.md` (Overview page) with its index row: the sections and their texts, the sources of every figure,
  staleness and `unknown`, the backfill line, the refresh with its interval, deadline and failure line, the caps and the memory per circuit.
- `docs/areas/web-ui.md`: the overview among the views in *Scope*, *Store reads of the views* and *Not here* (no longer planned).
- `docs/areas/time-series-query.md`: *Scope* and *Not here* name the overview only if the plan has it call the query.
