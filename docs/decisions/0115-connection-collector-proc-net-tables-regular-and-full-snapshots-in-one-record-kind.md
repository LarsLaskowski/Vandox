# 0115: Connection collector: the /proc/net tables, regular and full snapshots in one record kind

- **Status:** Accepted
- **Date:** 2026-10-18
- **Area:** Agent connections
- **Source:** Issue #33
- **Supersedes:** —

## Context

Issue #33 replaces the `lsof -ni` half of the legacy script: count sockets per state, per process and per remote address, keep an
inventory of listening sockets, report new listeners, and keep a full connection list on anomalies, without running `lsof` (0019) and
without making the memory situation of the 2 GB server worse. The wire format already has `connection_snapshot`, written by the legacy
`lsof` parser, read by the incident's connection peak and chart; its `complete` means "no entry the producer saw was left out", so an
aggregate-only snapshot cannot be told from a full one by it. A local user can open as many sockets and listeners as the limits allow.
Wire format 1.0 was released with v0.1.0. The other decisions of the collector have records of their own: the new-listener check
([0143](0143-new-listener-check-against-the-last-inventory-bounded-privileged-ports-first.md)), the socket owners
([0144](0144-socket-owners-from-fdinfo-on-the-socket-mount-the-agent-learns-at-its-start.md),
[0145](0145-fdinfo-lines-accepted-in-the-range-the-kernel-prints-them.md)), the socket UID
([0146](0146-socket-uid-from-the-tables-uid-column-lowest-uid-and-mixed-uids-per-endpoint.md)) and changed listeners
([0147](0147-a-known-endpoint-whose-socket-uid-changes-is-reported-as-a-changed-listener.md)).

## Options considered

1. **Netlink socket diagnostics (`sock_diag`)** instead of the text tables — faster on very many sockets and filterable in the kernel,
   but the collector would open and query a netlink socket every run, which the agent's collectors do not (0111, 0113), and it still
   needs the descriptors for the owners. Rejected.
2. **The four `/proc/net` tables of the agent's network namespace** (chosen) — what the issue names; streamed line by line through the
   replaceable file system, so tests run on real captured tables.
3. **A full connection list every run** — simplest, but about 4,096 entries of up to 1 KiB each every minute for data that matters
   only around an anomaly. Rejected for a **regular** snapshot (aggregates and inventory) every run and a **full** one on the first run,
   under memory pressure and on a new listener.
4. **New listeners as a record kind of their own (`listener_event`)** or as a new `kernel_event` type — a separate record per listener,
   but a new kind in both languages, the storage and every reader, and the event would lose the full snapshot of the same moment;
   `kernel_event` is about the kernel. Rejected for **additive fields of `connection_snapshot`**, whose absence describes imported and
   older snapshots correctly (full, aggregates complete, no check made).
5. **Reusing `complete` for "regular or full"** — no wire change, but it would change what `complete` means for the readers that rely
   on it. Rejected (spec challenge).
6. **Keeping wire format 1.0** as 0084 did for an unreleased format — no version change, but 1.0 is released now and the format's rule
   calls an added field a minor change. Rejected: the version becomes **1.1**; both decoders already accept a newer minor and ignore
   unknown keys.

## Decision

A collector `connections` reads the four `/proc/net` tables within fixed caps and records one `connection_snapshot` per run, regular
or full, with the outcome of the new-listener check in the same record, in wire format 1.1. The rules and caps are in
[Agent connections](../areas/agent-connections.md), the fields in [Wire format records](../areas/wire-format-records.md)
(*`connection_snapshot`*).

## Consequences

- The live memory of a run is bounded by fixed caps; the garbage grows with the descriptors read, reclaimed by the runtime.
- The agent's totals include sockets without an owner (`TIME_WAIT`), which `lsof -ni` never listed, and the incident's connection peak
  is taken over every source in its window, so agent snapshots win it against imported ones (accepted, [Incidents](../areas/incidents.md)).
- IPv4-mapped addresses stay as the kernel reports them, so one peer can be two `remotes` entries.
- The backend must be upgraded to read 1.1 before agents send it; a 1.0 backend reads the new fields as absent.
