# 0143: New listeners are found against the last recorded inventory, bounded, with the privileged ports first

- **Status:** Accepted
- **Date:** 2026-10-18
- **Area:** Agent connections
- **Source:** Issue #33
- **Supersedes:** —

## Context

Issue #33 asks the connection collector
([0115](0115-connection-collector-proc-net-tables-regular-and-full-snapshots-in-one-record-kind.md)) to report new listeners. A
local user can open as many listeners as the limits allow, so whatever the agent remembers about listeners must be bounded, and
a flood of unprivileged listeners must not hide one on a privileged port, where a system service or an intruder with root would
listen. UDP clients and resolvers open short-lived unconnected sockets on ephemeral ports, which look like listeners in the tables.

## Options considered

1. **Compare against an unbounded set of every listener seen** — no misses, but memory grows with what a local user opens.
   Rejected.
2. **Compare against the last recorded inventory, bounded at the wire format's list limit, with the privileged ports (1–1023)
   listed first** (chosen) — compared alone when an inventory was cut, and no comparison after a skipped line or a changed set of
   tables; each snapshot says which comparison was made.
3. **Read `ip_local_port_range`** for the ephemeral UDP sockets that are not reported — exact per server, but one more file and its
   failure modes. Rejected for the kernel's default range, fixed.

## Decision

The collector compares each run's listener inventory with the previous successful run's, held in memory and bounded like the
inventory, compares only the privileged ports when an inventory was cut, and states in each snapshot which comparison it made. The
rules are in [Agent connections](../areas/agent-connections.md) (*The listener check*).

## Consequences

- A run that passes its deadline keeps no state, so a late return cannot make a listener known without a report; the window between
  the last check and the return is a stated residual.
- A listener that opens while the agent is stopped is not reported by the agent; the backend compares across a start
  ([0116](0116-listeners-across-agent-starts-compared-when-stored.md)).
- A local user who keeps more listeners open than the cap hides new listeners on ports from 1024 up for as long, visible as
  `listener_check` `privileged`; a user who keeps the collector stalled suppresses the check while runs are gaps.
- Short-lived TCP listeners such as passive FTP are reported when a run sees them; a server whose local port range differs from the
  default reports UDP sockets outside the fixed range.
