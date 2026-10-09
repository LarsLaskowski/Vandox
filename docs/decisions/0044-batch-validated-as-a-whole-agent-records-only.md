# 0044: A batch is valid only as a whole, carries only agent records and is bounded by format limits

- **Status:** Accepted
- **Date:** 2026-10-04
- **Area:** Wire format
- **Source:** Issue #10
- **Supersedes:** —

## Context

The ingest API decodes batches from the agent, an external input (`.squad/project.md`, *Security areas* 1 and 10). The same record model is
produced by the log importer and by the backend itself (gap records, 0028), and the backend adds the receive time that the live/backfill
classification depends on (0022). A batch is streamed (0042), so records are decoded before the gzip checksum at the end has been verified.
A compromised or buggy agent holding a valid token must not exhaust the backend's memory, make its records look like backend or import data,
or choose the receive time.

The per-field bounds (lists and maps up to 4096 entries, texts up to 16 KiB) do not add up to a bounded line: a valid MariaDB status or process
snapshot encodes to several MiB. Such a record has to be caught where it is created, not when a whole batch fails to encode during an outage,
which is exactly when the data matters most.

## Options considered

1. **Records usable as soon as they are decoded; origin and receive time on the wire** — a truncated stream can leave half a batch stored, and the agent controls fields that only the backend may set.
2. **Batch accepted only as a whole; origin implied by the transport; fixed limits on every size** (chosen).
3. **Per-record skip of invalid records** — keeps the valid part of a damaged batch, but makes resends non-idempotent and hides agent bugs.

Records that are valid but larger than a line may be:

- **Raising the line limit until everything fits** — the worst case is far beyond 100 MiB. Rejected.
- **Bounding the encoded size in the model** — the model would have to duplicate the encoder. Rejected.
- **A per-record size check that producers call at creation, plus reduction markers in the payload** (chosen).
- **Splitting a large snapshot into several records** — the backend would reassemble them, and a lost part would look like a complete snapshot. Rejected.

Zone of an IPv6 address: **reject any zone and let the producer strip it** (chosen), since a zone names an interface of the agent host and means
nothing to the backend. Text from the agent in error messages: **quoted and cut** (chosen) instead of raw, so a key with a newline or markup
cannot reach a log.

Allocation amplification of the standard JSON decoder (list elements are allocated before validation can reject a list over the bound; a
1 MiB line of empty objects allocates about 270 MiB and peaks at about 160–170 MB heap, from a body of about 1 KB compressed):

- **Lower the line limit** — the peak shrinks only fourfold at best, and the largest valid kinds leave almost no margin. Rejected.
- **Count list elements before decoding** — a second hand-written scan over hostile input that doubles the parsing cost. Rejected for now; it can be added without a format change.
- **Accept the residual and make the ingest API bound concurrent decoders per agent and in total** (chosen), sized from the per-decoder peak.

## Decision

Option 2 with the per-record size check at the producer. Limits, the producer duty, the batch rules and the consumer duties are in the
[Wire format](../areas/wire-format.md) area (*Limits*, *Batch rules and trust*, *Error text and consumer duties*).

## Consequences

- A resent or truncated batch never leaves a partial result, which keeps deduplication by (agent ID, sequence number) simple (0045).
- The agent cannot pose as the importer or the backend, and cannot set the receive time that 0022 relies on; the header's live/backfill mode stays a hint.
- An oversized record is found by the producer at creation time, with the record identified; a snapshot reduced for size is visibly incomplete, so the analysis does not mistake it for the whole picture (spirit of 0028).
- Memory per decoded line is bounded but far above the line size. This is accepted because only an agent with a valid token reaches the decoder (security area 1), lines are decoded one at a time, and the ingest API limits concurrent decoders.
- Errors wrapped from the platform libraries still carry raw agent text; a consumer logs or displays decode errors only through the display sanitization of security area 12.
- A batch larger than the limits is invalid for every backend of major version 1; the agent's batching must stay below them. Raising a default is a minor change, lowering one a major change (0043).
