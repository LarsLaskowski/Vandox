# 0217: The members of service records that tell a restart are kept in a covering partial index

- **Status:** Proposed
- **Date:** 2026-11-15
- **Area:** Time-series queries
- **Source:** Issue #47
- **Supersedes:** —

## Context

The one-year query of [`BENCHMARKS.md`](../BENCHMARKS.md) (*Time-series query*) must answer in under one second on the DS918+; the
first build took 2,386 ms there. After the reductions inside the existing schema the development host still needs about 140 to 170 ms of
its 230 to 300 ms for the restart markers: every `service_state` record of the range (87,600 a year for ten units) is fetched and its
JSON payload parsed to find the few that changed (record 0215), and about 90 ms of that is SQLite fetching and parsing alone. The DS918+
is about 3.2 times slower. The payload is the backend's own serialization of the decoded agent record, so it is always valid JSON.

## Options considered

1. **Parse fewer members** — SQLite parses the whole document once per row whatever the number of members read; no gain.
2. **Find the unit by text search in the payload** — no parse, but a forged payload can place the text anywhere; the rule must read
   parsed values.
3. **A table of service changes written at ingest** — reads only the changes, but adds logic to the write path and the retention removal
   and a second place for the restart rule.
4. **Connection pooling, larger reader caches, or reads in parallel within one query** — store-wide changes, or a gain the benchmark
   overstates because it has no views' gate; none removes the parse.
5. **A covering partial index on the members the rule reads** (chosen) — the reads of service records answer from the index without
   fetching or parsing a payload; ingest computes the entries of `service_state` records only.

## Decision

Option 5: schema step 15 adds `records_service_state` on `records(captured_at, json_extract(data, '$.unit'),
json_extract(data, '$.restarts'), json_extract(data, '$.active_enter_at'), json_extract(data, '$.active_state'), boot_id, kind) WHERE
kind = 'service_state'`, and the three service reads of the time-series query name it with `INDEXED BY records_service_state` and use
exactly these expressions, so SQLite answers them from the index. `kind` is a column of the index because SQLite 3.53 counts a column
of the partial index's `WHERE` as read from the table, so without it the index is never covering; without `INDEXED BY` the planner
takes `records_kind_time`.
The schema and the step are in [Storage](../areas/storage.md).

## Consequences

- Each stored `service_state` record costs one index entry (about 100 bytes, at most about the size of its payload) and one parse at write time; other records cost only the
  check of `kind`.
- The upgrade to step 15 scans the `records` table once to build the index; the writer is busy for that time, and agents keep their
  records in the spool. On the benchmark database (1.2 million records) the build took 243 to 282 ms on the
  development host (`BENCHMARKS.md`).
- A `service_state` payload that is not valid JSON would make its insert fail, and the upgrade to step 15 (`store: migration 15: malformed JSON`, the step rolled back, version 14 kept); payloads are only written by the backend's serializer (`BatchWriter` through `PayloadJson`), so neither occurs.
- The one-year query fell from 306 to 468 ms to about 218 ms on the development host (median of
  three runs: 289, 216, 218 ms); the DS918+ figure is the Product Manager's run.
- `INDEXED BY` makes a service read fail with an error, not run slowly, if the index is missing; step 15 always creates it before a
  read runs.
- The restart rule (record 0215) is unchanged; if it ever reads another member, the index gains it in a new step, or the read loses
  its speed.
