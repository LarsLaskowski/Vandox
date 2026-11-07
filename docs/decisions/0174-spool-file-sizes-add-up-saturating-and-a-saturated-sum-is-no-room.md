# 0174: Spool file sizes add up saturating, and a saturated sum is no room

- **Status:** Accepted
- **Date:** 2026-10-23
- **Area:** Agent spool
- **Source:** Issue #301
- **Supersedes:** —

## Context

The spool's size bound adds the sizes of its files (record 0123). Issue #301 showed that a planted file, such as a sparse file of
enormous apparent size, could overflow the sum. The largest file a real spool writes depends on the `spool.max_size` of the run that
wrote it, which a start after a lowered cap does not know.

## Options considered

1. **A data file larger than the sealing size plus the largest hand-over is damage** — a bound from the current cap deletes good
   records after the cap was lowered, and a fixed bound needs a ceiling of its own in the spool.
2. **Sizes add up saturating at 2^63 - 1, and a saturated sum is no room** (chosen) — every file a real spool writes keeps today's
   behavior; a sum that cannot be represented fails closed, so the size bound drops the oldest files until the sum fits again.

## Decision

Option 2. The rule, and the matching saturation of the spool size metric, are in [Agent spool](../areas/agent-spool.md).

## Consequences

- A planted oversized file is dropped like any other oldest file, with a gap, instead of wrapping the sum.
- No size of a legitimate file is refused, whatever the cap of the run that wrote it.
