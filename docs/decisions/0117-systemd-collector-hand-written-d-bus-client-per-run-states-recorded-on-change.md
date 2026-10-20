# 0117: systemd collector: a hand-written D-Bus client with one connection per run, unit states recorded on change, after a lost sample and hourly

- **Status:** Accepted
- **Date:** 2026-10-20
- **Area:** Agent
- **Source:** Issue #34
- **Supersedes:** —

## Context

#34 asks for the state of the important services of the Plesk server, read over D-Bus, from a configurable unit list
with a Plesk default, with state changes as timestamped events and one warning for missing units. The agent may not
start programs (0019), must never block on a hung source (0029), records every lost sample as a gap (0028) and must not
make the memory of a 2 GB server worse (0025). The system bus is reachable by every local user, and its default policy
lets any of them send a signal to any connection, up to 32 MiB and with file descriptors; so whatever reads the bus reads
input a local user chooses. The Go standard library has no D-Bus client. A capture of the real replies of systemd 249
on the server (`testdata/systemd/capture-systemd249.pcap`) shows that an unprivileged user can make every call needed.

## Options considered

1. **How to read the states.** `systemctl show` — a program per run, against 0019; the files under `/run/systemd` —
   undocumented and incomplete (no `NRestarts`); **systemd's D-Bus interface on the system bus** (chosen), the
   documented interface `systemctl` itself uses.
2. **D-Bus client.** `github.com/godbus/dbus/v5` (or `coreos/go-systemd/dbus` on top of it) — a dependency whose reader
   accepts messages up to 128 MiB and buffers each whole, negotiates file-descriptor passing, starts a goroutine per
   incoming method call and panics on some malformed bus signals (read at v5.2.4), none of which the agent can bound;
   **a minimal client in the agent** (chosen): SASL `EXTERNAL`, no file-descriptor passing and reads only through the
   connection's plain `Read`, so the kernel closes any descriptor sent to it; a fixed socket path, never an environment
   variable; the methods it calls; header fields decoded only with their fixed basic types; a decoder for exactly the
   reply signatures it expects; and every message it did not ask for discarded by length without decoding, as 0110 did
   for the notification protocol.
3. **Connection.** One connection for the agent's lifetime — a reader goroutine, a queue for whatever arrives between
   runs and a reconnect path after `daemon-reexec` or a bus restart; **one connection per run, closed at its end**
   (chosen): nothing arrives between runs, the run's deadline bounds the connection, and a broken bus heals at the
   next run.
4. **Reading a unit.** `GetAll` per unit — one call, but a reply of about a hundred properties of every D-Bus type,
   which needs a general variant decoder; `LoadUnit` and a `Get` per property — seven round trips per unit (about 1,800
   at the cap inside a 5-second timeout) and active and sub state read at different moments, a pair that may never have
   existed; **`ListUnitsByNames` for many units at once (primary name, load, active and sub state in one consistent
   entry, aliases resolved) and a `Get` of `ActiveEnterTimestamp` and `NRestarts` per loaded unit** (chosen), the
   reply layout of the captured `ListUnitsByPatterns` reply and the captured `Get` replies, so the decoder handles one
   struct array and `t` and `u` in a variant; a not-found, inactive unit needs no `Get`.
5. **Noticing changes.** Subscribing to `PropertiesChanged` — a persistent connection and match rules that make the bus
   deliver every unit's broadcasts to the agent; **polling at the collector's interval** (chosen), 10 seconds under
   memory pressure, with `NRestarts` and `ActiveEnterTimestamp` revealing a restart between two runs.
6. **What to record.** Every unit every run — about 1,300 identical records per hour for the default list; only
   changes — a state lost with a dropped sample is never recorded, and a quiet unit leaves no record in a time window;
   **changes, every unit after a start and after a lost sample, and every unit again once its last record is an hour
   old** (chosen).
7. **PHP-FPM units in the default list.** Their names carry the PHP version, so a list of names goes stale; **entries
   may be patterns with `*` and `?`, matched by systemd (`ListUnitsByPatterns`) against the loaded units** (chosen),
   capped at 256 tracked units.
8. **Unit types.** Every type — `NRestarts` exists only for services; **services only** (chosen), an entry without a
   suffix read as `.service` as `systemctl` does.

## Decision

The collector `systemd` reads the configured service units every run over its own short-lived connection to the system
bus with a minimal hand-written D-Bus client that decodes only the replies it asked for, and records a unit's state on a
change, after a start or a lost sample, and hourly. The rules are in the [Agent](../areas/agent.md) area (*Services*),
the option in [Configuration and secrets](../areas/configuration-and-secrets.md) and the record in the
[wire format](../areas/wire-format.md) (`service_state`).

## Consequences

- No new dependency and no new right; the agent's unit (#42) must keep `AF_UNIX` and the system bus socket reachable.
- A unit's records are at most an hour plus one interval apart; a state that comes and goes between two runs without
  changing `NRestarts` or `ActiveEnterTimestamp` is not seen, and the second of a failure comes from the journal (#37).
- Reading a unit systemd has not loaded (a not-found one) makes systemd load and unload it for the call, and so
  broadcast `UnitNew` and `UnitRemoved` on the bus (seen in the capture); with `ListUnitsByNames` and no `Get` for it
  that is one pair per missing unit and run.
- A pattern match that systemd lists as not-found (a stub any local user can make it load) is not tracked.
- A local user who learns the agent's unique name during a run can flood that connection with signals; the run
  discards them until its deadline and becomes a gap, visible as such, and no memory grows.
- Self-healing (#69) can read the same states; a need for instant notice of a change would revisit option 5.
