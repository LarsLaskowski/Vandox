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

## Decision

Option 2:

- `wire.Decoder.Next` returns `io.EOF` only after the last record, the gzip checksum and the batch-level
  rules (at least one record, at most `MaxRecords`, sequence numbers strictly increasing) have passed. Any
  error is sticky. A consumer must not commit a batch before `io.EOF`.
- Every decoded record has `model.OriginAgent` and a sequence number greater than 0; the envelope has no
  `origin` or receive-time field, and keys with those names are ignored like any unknown key.
  `wire.EncodeBatch` refuses records whose origin is not `agent`. Gap causes that only the backend can
  detect (`sequence_missing`, `no_data`) are invalid in agent records.
- The decoder enforces `wire.Limits`: at most `MaxLineBytes` per line (default 1 MiB), `MaxBatchBytes`
  decompressed bytes per batch (default 16 MiB, the bound against gzip bombs) and `MaxRecords` records
  (default 20 000). The model bounds every list and map to `model.MaxItems` (4096) entries and every
  string to a documented byte length.

## Consequences

- A resent or truncated batch never leaves a partial result, which keeps deduplication by (agent ID,
  sequence number) in #40 simple.
- The agent cannot pose as the importer or the backend, and cannot set the receive timestamp that 0022
  relies on. The header's live/backfill mode stays a hint only (0022).
- Memory per decoded line is bounded but not equal to the line size: `encoding/json` allocates list
  elements before validation can reject a list longer than `MaxItems`, so a hostile 1 MiB line of empty
  objects costs tens of MiB transiently on the backend host. This is accepted because only an agent holding
  a valid token reaches the decoder (security area 1) and lines are decoded one at a time; consumers stream
  records instead of collecting a whole batch.
- A batch larger than the limits is invalid for every backend of major version 1; the agent's batching
  (#39) has to stay below them. Raising a default is a minor change, lowering one is a major change (0043).
