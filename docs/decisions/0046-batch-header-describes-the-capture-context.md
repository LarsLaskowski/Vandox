# 0046: The batch header's boot ID and clock offset describe when the records were captured, not when they were sent

- **Status:** Proposed
- **Date:** 2026-10-04
- **Source:** Issue #10
- **Supersedes:** —

## Context

Issue #10 puts the agent's boot ID and its clock offset, "if known", into the batch header. The spool holds
at least 7 days across reboots (0045), so a backfill batch is often sent in a later boot than the one its
records were captured in, and with a different clock offset (NTP corrected the clock, or the clock was
wrong before a reboot). #41 corrects capture times by the clock offset to tell live from backfilled data
(0022) and has to know which boot and which offset a capture time belongs to. Changing the meaning of a
header field later needs a new major version (0043), so the meaning has to be fixed in format 1.0.

## Options considered

1. **The header describes the moment of sending** — trivial for the agent. Cons: for a backfill batch the
   boot ID and the offset belong to a different moment than the records' capture times; #41 cannot correct
   them, and records captured before a reboot are attributed to the wrong boot.
2. **The header describes the capture of every record in the batch; the agent batches accordingly** —
   one value per batch keeps the lines small and the rule simple. Cons: the agent has to keep boot ID and
   offset with the spooled records and start a new batch when either changes.
3. **Optional boot ID and offset on every record** — most flexible. Cons: repeats the same two values on
   almost every line, adds validation surface to every record, and still needs a rule for records without
   them.

## Decision

Option 2. `boot_id` in the header is the boot during which **every** record of the batch was captured.
`clock_offset_ns` is the agent's estimate of its system clock minus the reference time of its time
synchronization (positive: the agent's clock is ahead), valid for the capture times of every record in the
batch; the corrected capture time is `captured_at − clock_offset_ns`. It is omitted when the agent has no
estimate; omitted means unknown, never 0. Batching rule for the agent (#38, #39): the boot ID and the
current offset estimate are kept with each spooled record, and a batch contains only records with the same
boot ID and the same offset estimate (or all without one); a change of either starts a new batch. How
often the agent refreshes its estimate is left to #39. A gap record is captured when the agent records
it, so a gap spanning a reboot belongs to the boot in which it was recorded. The decoder cannot verify this
rule; it is a producer contract, stated in `docs/WIRE_FORMAT.md`.

## Consequences

- #41 can correct backfilled capture times with the offset that applied at capture, and attribute every
  record to the right boot.
- A reboot or an offset refresh ends the current batch, so batches can be smaller than the limits allow.
- The offset is supplied by the agent, like `captured_at` itself; it is no more trusted than the agent's
  clock, on which 0022 already depends. The backend may prefer its own measurement (#41).
- Per-record boot ID or offset can still be added as optional fields in a minor version (0043) if a
  producer ever needs mixed batches; they would override the header for that record.
