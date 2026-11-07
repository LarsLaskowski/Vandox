# 0118: MariaDB collector: a hand-written client over the local socket that sends only the captured statements

- **Status:** Accepted
- **Date:** 2026-10-21
- **Area:** Agent MariaDB collector
- **Source:** Issue #35
- **Supersedes:** —

## Context

#35 asks for MariaDB's health and memory: whether it answers, its connections, load, buffer pool, memory and theoretical maximum,
the long-running statements when threads pile up, and whether it listens on a public address. Access is fixed by 0013 (the user
`vandox-agent` through `unix_socket`, only `PROCESS`); a hung database must never block the agent (0029), a lost sample is a gap (0028),
and the agent must not make the memory of a 2 GB server worse (0025). The Go standard library has no MariaDB client. The process list
carries the text of every running statement, which any web-space user of the server chooses, so the agent reads input a local user
controls. The server runs MariaDB 12.3.3 (`12.3.3-MariaDB-ubu2204`, the mariadb.org package for Ubuntu 22.04), not the 10.6 the
issue implied. The Product Manager supplied two plaintext captures of a real `unix_socket` login as `vandox-agent` on that server with
the statements the collector needs (`testdata/mariadb/`); there is no capture of the error cases (a refused login, the error before the
greeting when the server is full), and the Product Manager has none. How a refused login is recorded is decided in
[0151](0151-a-refused-mariadb-login-is-recorded-as-not-answering-with-its-error-code.md), how exposure is detected in
[0152](0152-mariadb-exposure-counted-from-the-kernels-tcp-tables-not-from-bind-address.md).

## Options considered

1. **Protocol client.** `github.com/go-sql-driver/mysql` behind `database/sql` — a dependency with a connection pool and goroutines
   that outlive a run, which reads every packet whole up to its `maxAllowedPacket` (so one statement text of megabytes is held in
   memory before the agent can cut it), and whose `LOAD DATA LOCAL` handling and authentication plugins are code the agent does not
   need; **a minimal client in the agent** (chosen) that speaks only the commands it needs, bounds every cell before it keeps it and
   discards the rest by length, asks for no local files, multiple statements or TLS, and refuses every reply that differs from the
   expected layout — as 0117 chose for D-Bus.
2. **Connection.** One connection for the agent's lifetime — a session that holds server memory between runs and needs a reconnect
   path; **one connection per run, closed at its end** (chosen): the run's deadline bounds it, and with the account's connection limit
   of 2 one connection stays free.
3. **Statements.** Filtered queries (`SHOW GLOBAL STATUS WHERE ...`, `information_schema` with `LEFT(INFO, ...)`) — smaller replies,
   but no capture pins their answers; **the captured statements verbatim** (chosen); the client keeps only the selected names and
   bounds every cell itself.
4. **Capabilities.** The client's capability bits decide the layout of the server's replies, and the captures pin only the layout
   of the captured client. **The client sends the captured client's format-relevant bits** (chosen) and leaves out the bits it must
   not or need not have; the test fake refuses a handshake without the layout bits, so the replay pins the layout the agent really
   asks for.
5. **Statement text in memory.** Every thread's statement kept up to the per-text limit — up to 64 MiB for the thread cap; **only the
   statements of the first threads in the snapshot's order (working before sleeping, longest-running first)** (chosen), about 1 MiB,
   the most a record line can carry anyway.
6. **Test data for what was not captured.** Wait for captures of the error cases — the Product Manager has none, and provoking them on
   the production server (filling `max_connections`, a shutdown) is an outage; **build those replies from the protocol documentation,
   marked in the tests, with the residual risk stated here** (chosen). The harmless ones (a ping, a login refused as another OS user or
   for a missing user, the user's connection limit) are requested as captures and replace the built replies when they arrive.

## Decision

The collector `mariadb` reads the server every run over its own short-lived connection with a minimal hand-written client that sends
only the fixed, captured read-only statements and bounds every reply before it keeps it. The rules are in
[Agent MariaDB collector](../areas/agent-mariadb.md), the options in [Configuration options](../areas/configuration-options.md), the
record and the metrics in [Wire format records](../areas/wire-format-records.md).

## Consequences

- No new dependency and no new right; the unit must keep `AF_UNIX` and the socket's directory reachable.
- **Residual risk of the uncaptured replies.** The error packet the server sends instead of the greeting, the error numbers of a
  refused `unix_socket` login, the answer to `COM_PING`, the authentication switch request and the replies to the agent's own
  capability set (which differs from the captured client's in bits that, by the protocol documentation, change no reply to these
  statements) are built from the documentation. A server that answers otherwise makes the run fail as a gap with a fixed reason, never
  a wrong value; the first real run on the server, or a capture of the agent itself, settles it.
- MariaDB 10.6 to 12.x share the protocol and the statements, but only 12.3.3 is captured; MySQL is not supported.
- A server that asks for another authentication method gets no credential; the client never answers a request for more
  authentication data, so no credential leaves the agent.
- A command that fails closes the connection, because what is left of its reply cannot be told apart from the next one.
- The statement texts of the process list can hold customer data and credentials; they are stored and sent to the backend, which
  shows them escaped. That is the consequence 0013 accepted; `deploy/agent/README.md` says so.
- The full status and variable lists are read every run (about 40 KB from a local socket every 30 seconds) for the sake of the
  capture; a filtered query would need a new capture.
