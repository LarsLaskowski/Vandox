# 0171: A fresh spool starts its sequence numbers above a clock bound

- **Status:** Accepted
- **Date:** 2026-10-23
- **Area:** Agent spool
- **Source:** Issue #38
- **Supersedes:** 0045 (amends it: a lost spool no longer needs a new agent ID, because a fresh spool's sequence numbers start above the clock bound)

## Context

Records need a strictly increasing sequence number that is never reused (0045); the backend deduplicates by it. The counter lives in
the spool (record 0123), which can be lost (a wiped directory, a reinstall) or rolled back (an unclean stop that loses its last
writes). Under 0045 a lost counter needed a new agent ID.

## Options considered

1. **Refuse to start** — monitoring stops until someone wipes the agent ID.
2. **A counter epoch in the batch header** — a wire minor version and backend work in #40 and #41.
3. **The clock bound** (chosen) — microseconds since the epoch plus a margin, used on a fresh spool and after an unclean stop in an
   earlier boot; safe unless the clock was set back, at the price of a large first number and a jump after a crash.

## Decision

Option 3: on a fresh spool and after an unclean stop in an earlier boot, the next sequence number is at least the clock bound. The
bound and when it applies are in [Agent spool](../areas/agent-spool.md).

## Consequences

- An agent's first sequence number is large, and an unclean stop in an earlier boot leaves a jump that the backend reports as
  `sequence_missing`; numbers below an agent's first are never reported.
- A clock set back before a reinstall can make new numbers collide with stored ones (stated residual).
