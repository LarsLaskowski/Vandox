# 0044: A batch is valid only as a whole, carries only agent records and is bounded by format limits

- **Status:** Proposed
- **Date:** 2026-10-04
- **Source:** Issue #10
- **Supersedes:** —

## Context

The ingest API (#40) decodes batches from the agent, an external input (`.squad/project.md`, *Security
areas* 1 and 10). The same record model is also produced by the log importer and by the backend itself
(gap records it detects, 0028), and the backend adds the receive timestamp that the live/backfill
classification depends on (0022). A batch is streamed (0042), so records are decoded before the gzip
checksum at the end of the stream has been verified. A compromised or buggy agent holding a valid token must
not be able to exhaust the backend's memory, to make its records look like backend or import data, or to
choose the receive timestamp.

The per-field bounds of the model (lists and maps up to 4096 entries, texts up to 16 KiB) do not add up to
a bounded line: a valid MariaDB status with 500 threads of 16 KiB query text, or a process snapshot with
long command lines, encodes to several MiB. Such a record has to be caught where it is created, not when a
whole batch fails to encode during an outage — exactly when the data matters most.

## Options considered

1. **Records usable as soon as they are decoded; origin and receive time on the wire** — simplest; a
   truncated or corrupted stream can leave half a batch stored, and the agent controls fields that only
   the backend may set.
2. **Batch accepted only as a whole; origin implied by the transport; fixed limits on every size** — the
   consumer commits only after the decoder reports the end of a fully verified stream; the wire envelope
   has no `origin` and no receive timestamp, so a decoded record is always an agent record; limits bound
   memory per line and per batch.
3. **Per-record skip of invalid records** — keeps the valid part of a damaged batch; makes resends
   non-idempotent (which part was stored?) and hides agent bugs.

For records that are valid in the model but larger than a line may be:

- **a. Raise `MaxLineBytes` until every valid record fits** — the worst case (4096 entries of 16 KiB, with
  JSON escaping) is far beyond 100 MiB, more than the 2 GB server (0025) or the backend should hold for
  one line. Rejected.
- **b. Bound the encoded size in the model** — the model has no JSON code (0042) and would have to
  duplicate the encoder to measure it. Rejected; the cheap part is done in the model, see below.
- **c. A per-record size check in the wire package that producers call when they create a record, plus
  reduction markers in the payloads** — the size is measured by the encoder that writes the line; a
  producer can shorten or cut a record and say so in the data. Chosen.
- **d. Split a large snapshot into several records** — the backend would have to reassemble them, and a
  lost part would look like a complete snapshot. Rejected.

## Decision

Option 2 with c:

- `wire.Decoder.Next` returns `io.EOF` only after the last record, the gzip checksum and the batch-level
  rules (at least one record, at most `MaxRecords`, sequence numbers strictly increasing) have passed. Any
  error is sticky. A consumer must not commit a batch before `io.EOF`.
- Every decoded record has `model.OriginAgent` and a sequence number greater than 0; the envelope has no
  `origin` or receive-time field, and keys with those names are ignored like any unknown key.
  `wire.EncodeBatch` refuses records whose origin is not `agent`. Gap causes that only the backend can
  detect (`sequence_missing`, `no_data`) are invalid in agent records.
- The decoder enforces `wire.Limits`: at most `MaxLineBytes` per line (default 1 MiB), `MaxBatchBytes`
  decompressed bytes per batch (default 16 MiB, the bound against gzip bombs) and `MaxRecords` records
  (default 20 000). The model bounds every list and map to `model.MaxItems` (4096) entries, metric labels
  to `model.MaxLabels` (32), and every string to a documented byte length.
- Per-record size: `wire.CheckRecord` validates one agent record and returns its encoded line length;
  a record whose line exceeds the default `MaxLineBytes` yields a `*wire.RecordSizeError` that wraps
  `wire.ErrRecordTooLarge` (itself wrapping `wire.ErrLimitExceeded`). `wire.EncodeBatch` reports the same
  error with the index of the offending record and writes nothing.
- Producer contract (documented in `docs/WIRE_FORMAT.md`): a producer of agent records calls
  `wire.CheckRecord` when it creates a record, before the record enters the spool (#38), so an oversized
  record never blocks a batch. On `ErrRecordTooLarge` it reduces the record and marks the reduction in the
  data, never silently: first long texts are shortened with `truncated` set (`ProcessSample.cmdline`,
  `MariaDBThread.info`), then entries are left out and `complete` is set to false (`ProcessSnapshot`,
  `ConnectionSnapshot`, `MariaDBStatus`). The kinds `metric`, `service_state`, `kernel_event`, `log_line`
  and `gap` fit the default line limit at their maximum field sizes even when every character is escaped,
  so they never need reducing.

## Consequences

- A resent or truncated batch never leaves a partial result, which keeps deduplication by (agent ID,
  sequence number) in #40 simple (0045).
- The agent cannot pose as the importer or the backend, and cannot set the receive timestamp that 0022
  relies on. The header's live/backfill mode stays a hint only (0022).
- A record that is too large is found by the producer at creation time, with the record identified; a
  snapshot reduced for size is visibly incomplete, so the analysis does not mistake it for the whole
  picture (spirit of 0028).
- Memory per decoded line is bounded but not equal to the line size: `encoding/json` allocates list
  elements before validation can reject a list longer than `MaxItems`, so a hostile 1 MiB line of empty
  objects costs tens of MiB transiently on the backend host. This is accepted because only an agent holding
  a valid token reaches the decoder (security area 1) and lines are decoded one at a time; consumers stream
  records instead of collecting a whole batch.
- A batch larger than the limits is invalid for every backend of major version 1; the agent's batching
  (#39) has to stay below them, using the size `wire.CheckRecord` returns. Raising a default is a minor
  change, lowering one is a major change (0043).
