# 0125: Sender with a live and a backfill stream, one shared backoff, refused records given up with a recorded gap

- **Status:** Accepted
- **Date:** 2026-10-24
- **Area:** Agent
- **Source:** Issue #39
- **Supersedes:** —

## Context

After an outage of the backend host the agent holds hours to days of records in its spool (record 0123) and must deliver
them all without a gap (guarantee *No data gaps unless explicitly recorded*, record 0028) while current data still arrives
on time, because the backend tells live from backfilled data by lateness (record 0022, #41). The server has 2 GB of RAM
(record 0025) and the link ends in a home network, so the backlog must be throttled (record 0045). The spool keeps at most 64
acknowledged ranges above its oldest unacknowledged record and refuses an acknowledgement beyond that (record 0123); a run of
records left unacknowledged in the middle of acknowledged ones costs one range for as long as it stays. A backend can refuse a
single batch as invalid (record 0124). The spec's challenge showed that leaving refused records unacknowledged, or opening a
new range on every reconnect, can exhaust the 64 ranges, after which the spool stops deleting acknowledged files and drops
stored data for its size bound.

## Options considered

Streams:

1. **One stream, oldest first** — simplest, but current data waits behind the backlog and is classified as backfill.
2. **One stream that alternates current and backlog batches** — current data still waits behind each backlog request, and the
   throttle would slow current data too.
3. **Two streams, live and backfill, with one shared episode and backoff** (chosen) — current data has its own request and is
   never throttled; while the backend does not accept batches only one probe is made at a time, so an unreachable backend costs
   one attempt per wait.

Refused records:

1. **Retry refused batches in later passes** — keeps the records but leaves one unacknowledged hole per refused batch;
   rejected by the spec challenge (exhausts the 64 ranges).
2. **Give the whole refused batch up with a gap** — no hole, but one bad record costs up to about 2,700 good ones.
3. **Split the refused batch until only the refused records remain, give each of them up with a recorded gap, at most 10 an
   hour** (chosen) — no hole, the loss is one record per bad record and is recorded; a backend that refuses everything is
   caught by the hourly bound: the stream that meets it waits until a give-up slot frees, so it costs at most 10 records and a
   bounded number of requests an hour, while the other stream goes on.

Ranges after reconnects:

1. **Raise the spool's cap** — only moves the limit; record 0123 sized it for two streams.
2. **Never jump the live stream forward** — no holes at all, but every outage delays current data by the whole backlog.
3. **Jump to the newest records while fewer than 32 ranges are open, else resume where the live stream stopped** (chosen) —
   current data first in every normal case; under a backend that flaps more than 32 times during one backfill current data
   is late until the backfill catches up, but nothing is lost and the spool keeps deleting files.

Bandwidth: a fixed limit was rejected in favour of the option `backend.backfill_bandwidth`, because the home connection and
the backend's disk differ between installations; the throttle counts compressed request bytes, the quantity the link carries.

## Decision

The sender runs a live and a backfill stream over one shared episode and backoff, throttles only the backfill stream, splits a
refused batch and gives each refused record up with a `gap` record (cause `unknown`, its sequence number) at most 10 times an
hour (beyond that the stream waits for a free slot), advances a stream's position only after the spool kept the acknowledgement, jumps the live stream forward only while fewer than 32 acknowledged ranges are open, and pauses the backfill in the
memory pressure mode. The values, log lines and order of events are in the [Agent](../areas/agent.md) area (*Sending*); the option is in [Configuration and secrets](../areas/configuration-and-secrets.md).

## Consequences

- A record the backend refuses on its own is lost, but recorded as a gap the backend receives; the guarantee holds.
- A version mismatch must not be answered with a refusal status (record 0124), or the hourly bound would still give up 10
  records an hour.
- The backend's lateness check (#41) must allow the 4-second send interval plus up to ten back-to-back requests (10 seconds in all).
- The hourly bound is kept in memory: each start of the agent allows 10 more give-ups. Restarts are rare and each give-up is a recorded
  gap, so a persisted bound was not worth a second state file.
- The sender needs the spool's count of open acknowledged ranges, a read-only addition to the spool.
- The agent opens the spool before it builds the sender and the scheduler, so it closes the spool when either of them fails.
  Both failures are defensive only: `config.LoadAgent` already rejects every backend URL the HTTP transport refuses and every
  log-shipping setting the scheduler refuses, and the scheduler's sink is always the spool. The two closes stay without a unit
  test. A hook variable or an injectable constructor only to reach them would add production code with no other use. Removing
  the close after a scheduler failure would leave the spool open on a path that a looser loader would make real. Add a test
  when a loader change makes either failure reachable.
- The pause lines mark a backfill the memory pressure mode holds back, so they depend on records waiting to be backfilled, not on
  a batch already sent: an agent that starts under the mode with a backlog logs the pause before its first batch (the operator
  sees why the backlog does not move), one without a backlog logs nothing. An outage always leaves the records of its failed
  live batch to the backfill, so a reconnect under the mode is a pause with a backlog, not a case without one.
- Revisit the 32-range threshold and the hourly bound if the server measurement (#44) or real outages show other figures.
