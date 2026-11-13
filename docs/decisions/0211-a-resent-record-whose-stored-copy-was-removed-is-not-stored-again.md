# 0211: A resent agent record whose stored copy was removed is not stored again

- **Status:** Proposed
- **Date:** 2026-11-13
- **Area:** Storage writing
- **Source:** Issue #46
- **Supersedes:** —

## Context

A resent record is stored once because the insert does nothing on a conflict of the unique index on (agent ID, sequence number)
(record 0045, [Storage writing](../areas/storage-writing.md)). Once retention removes the stored copy, nothing conflicts any more: the
resend would be stored a second time, counted as an arrival by the agent timeline and folded into its rollup buckets twice (record
0208). The agent resends after a lost acknowledgement, and a compromised agent may resend on purpose.

## Options considered

1. **Keep a tombstone row per removed record** — exact, but the tombstones grow like the records retention removes.
2. **Decide from the agent's stream** (chosen) — a number between the stream's floor and its highest number that lies in no sequence
   gap still waiting for it (open or final) was received before; the stream row and the gap rows already hold this.
3. **Accept the duplicate** — wrong counts and averages, against the spec's criterion AC3.

## Decision

The batch writer skips an agent record whose number lies between the stream's `floor_seq` and `max_seq` and in no sequence gap that is
not closed, before its insert: the record is acknowledged with its batch and neither stored nor folded. The rules are in
[Storage writing](../areas/storage-writing.md).

## Consequences

- The check costs a gap lookup only for numbers at or below the highest one, which the agent sends only on a resend or a backfill
  into a gap.
- A number in a closed gap was filled and is a resend; a number in an open or final gap still fills it, as today.
- A stream row must therefore never be removed while its agent has records (already a rule of [Storage](../areas/storage.md)).
