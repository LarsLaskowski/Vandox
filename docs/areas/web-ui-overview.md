# Overview page

## Scope

The page `/` of the web UI: what it shows about the monitored server (data freshness and backfill, the agent's spool, services, key
figures, incidents), the texts it uses, the sources and the staleness rules of each value, how it refreshes over its live connection,
what it reads from the store and how it caps its data. The user relies on it to see at a glance whether the server is healthy and
whether the data shown is current.

## Behavior

### Page and refresh

- The page is the start page `/` and the entry *Overview* of the navigation. Prerendered (the first response) it shows the heading
  *Overview* and `Loading…` and reads nothing; it reads once its live connection is up.
- Once interactive it shows `Updated <time>` and refreshes **every 30 seconds** without a page load. A refresh makes five store calls in
  turn: freshness, latest values, MariaDB status, service states and the 20 newest incident summaries.
- A refresh has a **deadline of 10 seconds**. A refresh that is due while one runs is skipped and not made up. While the live connection
  is lost the page starts no refresh; when the connection is back it refreshes at once. Leaving the page cancels a running read and
  stops the timer.
- A refresh that passes its deadline or fails with a store error keeps the values shown and adds `Refresh failed at <time>; the values
  are from <time>.` (`Refresh failed at <time>; no values have been read yet.` before the first success); no error text is shown. The
  next successful refresh removes the line. Other errors go to the framework's error banner.
- The page bounds every read by now plus 90 seconds: a record captured later is not used for freshness, services, figures or MariaDB,
  so a clock far ahead hides no current data, and the page does not show such records.
- Times are shown as `yyyy-MM-dd HH:mm:ss` in UTC. An age is whole seconds below a minute (`45 s`), whole minutes below an hour
  (`12 min`), hours and minutes below a day (`3 h 5 min`), days and hours above (`2 d 4 h`). A time after now shows `(ahead by <age>)`
  and is never stale. Sizes are in binary units with one decimal (`3.2 GiB`), shares in percent with one decimal (`25.0 %`).
- A value older than **5 minutes** is marked `stale`; a value exactly 5 minutes old is not.
- The refreshes are no session activity ([Web login](web-login.md), *Sessions*).

### Data freshness

- *Last live data*: the capture time of the newest `metric` record stored as live among the newest 20,000 metric records, with its age
  (`<time> (<age> ago)`). When none of them is live it reads `before <time>` (the oldest of them), marked `stale`. With no agent it
  reads `No live data has arrived yet. Install the agent, or import logs as the README describes.`
- *Last data received*: the receive time of the highest-numbered record of the agent whose batch arrived last, with its age. An agent
  whose newest records were removed shows `unknown`, marked `stale`; without any agent it shows `unknown`.
- *Backfill*: while an open sequence gap of that agent ID exists and a batch was received within 5 minutes it reads `Backfill running:
  data from <start> to <end> is missing.` (earliest start, latest end of the open gaps); with no batch for more than 5 minutes
  `Backfill stalled: data from <start> to <end> is missing, no data received since <time>.`; without an open gap `No backfill
  running.` No count of missing records is shown, because a gap can span numbers the agent never issued ([Agent spool](agent-spool.md),
  *Sequence numbers*). Open gaps of another agent ID are not counted.
- *Agent spool*: from the agent's latest self sample (`agent_spool_bytes`, `agent_spool_age_seconds`): `<size>, oldest unsent record
  <time>, as of <time>` (the sample time minus the age), or `<size>, nothing unsent, as of <time>` when the age is 0; `stale` when the
  sample is older than 5 minutes, `unknown` without a sample.

### Services

- Each unit of the newest service records is listed with `<active_state> (<sub_state>)`, the time it last became active (`unknown`
  when the stored value is no time), its restart count and the time of its latest record. A unit whose latest record is more than
  1 hour 5 minutes old is marked `stale`; a unit leaves the list when its latest record is more than 2 hours older than the newest
  service record.
- Units whose state is not `active` come first, then the others, each group by unit name in ordinal order; the row of a `failed` unit
  is marked. At most 256 units are shown, followed by `and <n> more`.
- When no batch was received for more than 5 minutes the section starts with `stale: no data received since <time>`. Without a stored
  state it reads `No service state is stored.`
- Unit names are external text ([Web UI](web-ui.md), *External text*).

### Key figures

- The table lists, in this order, with the time of each value: *Available memory* (`mem_available_bytes`, with its share of
  `mem_total_bytes`), *Swap* (`<used> of <total> used (<share>)`, `no swap` when the total is 0), *Memory pressure* (`some <x> %, full
  <y> %`), *CPU pressure* (`some <x> %`), *I/O pressure* (as memory) from `pressure_avg60`, *Disk* with one row per mount
  (`<available> available (<share>) of <size>`), *MariaDB* (`up`, `down` or `not answering`), *MariaDB connections* (`<n> of <max>`)
  and *MariaDB memory* (`<used> of at most <max>`, `mariadb_memory_max_bytes`).
- A value the latest values lack shows `unknown`, never 0; a share whose total is 0 is left out. MariaDB's other figures are `unknown`
  while it is not `up`. `max_connections` is shown only when the stored text is a non-negative integer of at most 20 ASCII digits that
  fits a 64-bit integer, else the connections are `unknown`.
- A mount is shown until its latest value is more than 10 minutes older than the newest file system value. At most 16 mounts are
  shown in the order of their label text, followed by `and <n> more mounts`. Mount points are external text.

### Incidents

- Open incidents are listed as `#<n>, open, first kill <time>, <victim>` with a link to `incidents/<n>`, newest first. An incident is
  open when its window ends after now and its first kill is not after now; windows never overlap, so at most one is open. Without one
  the page says `No open incident.`
- The latest stored incident is always shown with its number, link, first kill and victim, with ` dated in the future` when its first
  kill lies after now. Without any incident the page says `No incidents are stored. Run vandoxd incidents after an import.`
- Victims are external text.

## Limits and errors

- **Store reads.** Each of the five calls takes one slot of the views' read gate ([Web UI](web-ui.md), *Store reads of the views*);
  the page makes no further store call. The reads are described in [Storage reading](storage-reading.md),
  *Overview reads*.
- **Memory per circuit** is bounded by the caps: one model of at most 256 unit rows, 16 mounts and 20 incident summaries, with external
  text capped like the other views; the store reads of a refresh hold at most 16 label texts per series.
- **Load.** Every open overview page makes five store calls every 30 seconds; many open pages can delay the other views' calls within
  their own deadlines.
- A figure the agent does not send, or an agent that stopped, shows as `unknown` or `stale`, never as normal.

## Related decisions

- [0218](../decisions/0218-the-overview-reads-latest-values-through-its-own-bounded-store-reads.md) — why the overview has its own bounded store reads.
- [0219](../decisions/0219-the-overview-refreshes-on-a-timer-that-skips-overlaps-and-pauses-while-disconnected.md) — why a timer that skips overlaps and pauses while the connection is lost.
- [0220](../decisions/0220-a-circuit-showing-the-overview-does-not-extend-its-session.md) — why the overview's refreshes do not extend the session.

## Not here

- The reads themselves and their bounds: [Storage reading](storage-reading.md).
- The gate, circuits, external text and navigation: [Web UI](web-ui.md); the session rule: [Web login](web-login.md).
- The incident page: [Incidents view](web-ui-incidents-view.md); what the agent records: [Wire format records](wire-format-records.md).
- Health thresholds, alerts and charts of history: not built yet.

## Implementation

`Vandox.Backend`: `Components/Pages/OverviewPage` (markup and code), `Overview/OverviewView` (the model from the reads), `OverviewText`,
`OverviewRefresher`, `CircuitConnection` (connection state of a circuit, registered scoped and as a circuit handler) and
`GatedOverviewReader` (the gate); the reads are `IOverviewReader` of `Vandox.Storage`.
