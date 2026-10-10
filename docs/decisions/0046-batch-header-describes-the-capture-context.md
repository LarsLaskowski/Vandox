# 0046: The batch header's boot ID and clock offset describe when the records were captured, not when they were sent

- **Status:** Accepted
- **Date:** 2026-10-04
- **Area:** Wire format
- **Source:** Issue #10
- **Supersedes:** —

## Context

The batch header carries the agent's boot ID and its clock offset, if known. The spool holds at least 7 days across reboots (0045), so a
backfill batch is often sent in a later boot than the one its records were captured in, and with a different clock offset. The backend
corrects capture times by the offset to tell live from backfilled data (0022) and has to know which boot and offset a capture time belongs to.
Changing the meaning of a header field later needs a new major version (0043), so the meaning has to be fixed in format 1.0.

## Options considered

1. **The header describes the moment of sending** — trivial for the agent, but for a backfill batch boot ID and offset belong to a different moment than the capture times, so records are attributed to the wrong boot.
2. **The header describes the capture of every record in the batch; the agent batches accordingly** (chosen) — one value per batch keeps lines small; the agent keeps boot ID and offset with the spooled records and starts a new batch when either changes.
3. **Optional boot ID and offset on every record** — most flexible, but repeats the same values on almost every line and adds validation surface to every record.

## Decision

Option 2. The meaning of `boot_id` and `clock_offset_ns` and the batching rule for the producer are in the [Wire format](../areas/wire-format.md) area (*Capture context*). The
decoder cannot verify the rule; it is a producer contract.

## Consequences

- Backfilled capture times can be corrected with the offset that applied at capture, and every record is attributed to the right boot.
- A reboot or an offset refresh ends the current batch, so batches can be smaller than the limits allow.
- The offset is supplied by the agent like `captured_at` itself and is no more trusted than the agent's clock, on which 0022 already depends; the backend may prefer its own measurement.
- Per-record boot ID or offset can still be added as optional fields in a minor version (0043) if a producer ever needs mixed batches.
