# 0125: Sender with a live and a backfill stream over one shared backoff

- **Status:** Accepted
- **Date:** 2026-10-24
- **Area:** Agent sending
- **Source:** Issue #39
- **Supersedes:** —

## Context

After an outage of the backend host the agent holds hours to days of records in its spool (record 0123) and must deliver them all
without a gap (guarantee *No data gaps unless explicitly recorded*, record 0028) while current data still arrives on time, because the
backend tells live from backfilled data by lateness (record 0022, #41). The server has 2 GB of RAM (record 0025) and the link ends in a
home network, so the backlog must be throttled (record 0045). The spool keeps a bounded number of acknowledged ranges above its oldest
unacknowledged record (record 0172); a run of unacknowledged records between acknowledged ones costs one range for as long as it stays.
The spec's challenge showed that opening a new range on every reconnect can exhaust the ranges, after which the spool stops deleting
acknowledged files and drops stored data for its size bound.

## Options considered

1. **One stream, oldest first** — simplest, but current data waits behind the backlog and is classified as backfill.
2. **One stream that alternates current and backlog batches** — current data still waits behind each backlog request, and the throttle
   would slow current data too.
3. **Two streams, live and backfill, with one shared episode and backoff** (chosen) — current data has its own request and is never
   throttled; while the backend does not accept batches only one probe is made at a time, so an unreachable backend costs one attempt
   per wait.
4. **Raise the spool's range cap** — only moves the limit; record 0172 sized it for two streams.
5. **Never jump the live stream forward** — no holes at all, but every outage delays current data by the whole backlog.
6. **Jump the live stream to the newest records only while fewer than half the ranges are open, else resume where it stopped**
   (chosen) — current data first in every normal case; under a backend that flaps that often during one backfill current data is late
   until the backfill catches up, but nothing is lost and the spool keeps deleting files.

## Decision

The sender runs a live and a backfill stream over one shared episode and backoff, throttles only the backfill stream (by the option
`backend.backfill_bandwidth`, counting compressed request bytes), advances a stream's position only after the spool kept the
acknowledgement, jumps the live stream forward only while few acknowledged ranges are open, and pauses the backfill in the memory
pressure mode. The values, log lines and order of events are in [Agent sending](../areas/agent-sending.md); the option is in
[Configuration options](../areas/configuration-options.md).

## Consequences

- A fixed bandwidth limit was rejected in favour of the option, because the home connection and the backend's disk differ between
  installations; the throttle counts what the link carries.
- The backend's lateness check (#41) must allow the send interval plus up to ten back-to-back requests.
- The sender needs the spool's count of open acknowledged ranges, a read-only addition to the spool.
- The agent opens the spool before it builds the sender and the scheduler, so it closes the spool when either of them fails. Both
  failures are defensive only (the loader already rejects every input that could cause them), so the two closes stay without a unit
  test: a hook only to reach them would add production code with no other use. Add a test when a loader change makes either failure
  reachable.
- The pause lines mark a backfill the memory pressure mode holds back, so they depend on records waiting to be backfilled, not on a
  batch already sent: an agent that starts under the mode with a backlog logs the pause before its first batch, one without a backlog
  logs nothing.
- Revisit the range threshold if the server measurement (#44) or real outages show other figures.
