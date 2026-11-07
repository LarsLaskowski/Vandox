# 0147: A known endpoint whose socket UID changes is reported as a changed listener

- **Status:** Accepted
- **Date:** 2026-10-18
- **Area:** Agent connections
- **Source:** Issue #305
- **Supersedes:** —

## Context

Issue #305 asks to report a known endpoint whose socket UID changes, without flooding for endpoints that several users share by
design. The comparison keys of the new-listener check
([0143](0143-new-listener-check-against-the-last-inventory-bounded-privileged-ports-first.md)) hold no UID
([0146](0146-socket-uid-from-the-tables-uid-column-lowest-uid-and-mixed-uids-per-endpoint.md), option 4), so a service on an
unprivileged port that stops while another user binds its endpoint before the next run is never reported. The kernel lets sockets of
two UIDs share an endpoint only when both are bound to different devices, both UDP sockets set `SO_REUSEADDR`, or a holder of
`CAP_CHOWN` gives one away (`SO_REUSEPORT` needs one UID; measured on 6.18.44, squad record of #305).

## Options considered

1. **The UID in the inventory key** — rejected for the reasons of 0146, option 4.
2. **A change list beside `new_listeners`** (chosen): the previous inventory keeps each key's lowest UID and flag, and a compared
   endpoint whose lowest UID differs or whose `mixed_uids` turns true is listed once with its previous UID and makes the run full,
   under the new-listener rules.
3. **Reporting a flag that turns false** — rejected: a user who leaves an endpoint is no signal.
4. **Reporting every run while an endpoint is mixed** — rejected as the flood the issue names.
5. **The changed endpoints inside `new_listeners` with a marker** — no new list, but a reader counting new listeners (the backend's
   marks, the report's "new") would count endpoints that are not new. Rejected.

## Decision

A compared endpoint whose lowest socket UID changes, or whose mixed state begins, is reported in `changed_listeners` under the rules
of the new-listener check. The rules are in [Agent connections](../areas/agent-connections.md) (*The listener check*) and the field in
[Wire format records](../areas/wire-format-records.md) (*`connection_snapshot`*).

## Consequences

- A takeover of a known endpoint is reported only through its lowest UID and flag: a third UID that replaces a second while the
  lowest stays is not seen, and a shared endpoint whose second user comes and goes is reported each time the mixed state begins
  again, at most once per run.
- The residuals of the new-listener check (`privileged` under a listener flood, a `none` run as the base) hold for it.
