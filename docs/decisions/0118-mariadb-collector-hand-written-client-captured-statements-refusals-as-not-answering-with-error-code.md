# 0118: MariaDB collector: a hand-written client over the local socket, the captured statements, a refusal recorded as `not_answering` with its error number

- **Status:** Proposed
- **Date:** 2026-10-21
- **Area:** Agent
- **Source:** Issue #35
- **Supersedes:** —

## Context

#35 asks for MariaDB's health and memory: whether it answers, its connections, load, buffer pool, memory and theoretical maximum,
the long-running statements when threads pile up, and whether it listens on a public address. Access is fixed by 0013 (the user
`vandox-agent` through `unix_socket`, only `PROCESS`); a hung database must never block the agent (0029), a lost sample is a gap (0028),
and the agent must not make the memory of a 2 GB server worse (0025). The Go standard library has no MariaDB client. The process list
carries the text of every running statement, which any web-space user of the server chooses, so the agent reads input a local user
controls. The `mariadb_status` record kind exists since format 1.0 (released with v0.1.0) with the availabilities `up`, `down` and
`not_answering`. The server runs MariaDB 12.3.3 (`12.3.3-MariaDB-ubu2204`, the mariadb.org package for Ubuntu 22.04), not the 10.6 the
issue implied. The Product Manager supplied two plaintext captures of a real `unix_socket` login as `vandox-agent` on that server with
the statements the collector needs (`testdata/mariadb/`); there is no capture of the error cases (a refused login, the error before the
greeting when the server is full), and the Product Manager has none.

## Options considered

1. **Protocol client.** `github.com/go-sql-driver/mysql` behind `database/sql` — a dependency with a connection pool and goroutines
   that outlive a run, which reads every packet whole up to its `maxAllowedPacket` (so one statement text of megabytes is held in
   memory before the agent can cut it), and whose `LOAD DATA LOCAL` handling and authentication plugins are code the agent does not
   need; **a minimal client in the agent** (chosen): one plain connection, the greeting, a login with empty authentication data, `COM_PING`,
   `COM_QUERY` of fixed statements and `COM_QUIT`, text result sets read field by field with every cell bounded before it is kept and
   the rest discarded by length, no `CLIENT_LOCAL_FILES`, `CLIENT_MULTI_STATEMENTS` or TLS, and every reply that differs from the
   expected layout refused — as 0117 chose for D-Bus.
2. **Connection.** One connection for the agent's lifetime — a session that holds server memory between runs and needs a reconnect
   path; **one connection per run, closed at its end** (chosen): the run's deadline bounds it, and with `MAX_USER_CONNECTIONS 2` one
   connection stays free.
3. **Statements.** Filtered queries (`SHOW GLOBAL STATUS WHERE ...`, `information_schema` with `LEFT(INFO, ...)`) — smaller replies,
   but no capture pins their answers; **the captured statements verbatim** (chosen): `SHOW GLOBAL STATUS` (571 rows, about 17 KB),
   `SHOW GLOBAL VARIABLES` (696 rows, about 24 KB), `SHOW FULL PROCESSLIST` only when a snapshot is due, `SELECT CURRENT_USER()` and the
   `USER_PRIVILEGES` query of the account check; the client keeps only the selected names and bounds every cell itself.
4. **Capabilities.** The client's capability bits decide the layout of the server's replies, and the captures pin only the layout
   of the captured client. **The client sends the captured client's format-relevant bits** (chosen): `CLIENT_PROTOCOL_41`,
   `CLIENT_SESSION_TRACK`, `CLIENT_PLUGIN_AUTH` with length-encoded data, no `CLIENT_DEPRECATE_EOF` (so result sets end in EOF
   packets, as captured) and MariaDB's extended metadata (an extra field in every column definition, as captured); it leaves out the
   bits it must not or need not have (`CLIENT_LOCAL_FILES`, `CLIENT_MULTI_STATEMENTS`, `CLIENT_CONNECT_ATTRS`, progress reports).
5. **A login the server refuses.** A gap — loses the "too many connections" state an outage analysis needs; a new availability
   `refused` — a 1.0 decoder rejects an unknown value, so it is not a minor change under 0043; **`not_answering` with the new optional
   field `error_code`** (chosen): additive, and a reader that ignores the field still reads a true statement.
6. **Statement text in memory.** Every thread's statement cut to 16 KiB — up to 64 MiB for 4,096 threads; **at most 4,096 threads, and
   the statement text of only the first 64 in the snapshot's order (working before sleeping, longest-running first), 16 KiB each**
   (chosen), about 1 MiB, the most a record line can carry anyway.
7. **Public address.** Interpreting `bind_address` — host names need a lookup, MariaDB 10.11 and later take lists, and a socket
   opened another way is missed; **the listening sockets in the kernel's TCP tables on MariaDB's `port` and non-zero `extra_port`**
   (chosen), every address that is not loopback counted, read through the connection collector's table reader.
8. **Test data for what was not captured.** Wait for captures of the error cases — the Product Manager has none, and provoking them on
   the production server (filling `max_connections`, dropping the user) is an outage; **build those replies from the protocol
   documentation, marked in the tests, with the residual risk stated here** (chosen).

## Decision

The collector `mariadb` reads the server every run over its own short-lived connection with a minimal hand-written client that sends
only the fixed, captured read-only statements and bounds every reply before it keeps it; a login refused with an error packet is
recorded as `not_answering` with `error_code`. The rules are in the [Agent](../areas/agent.md) area (*MariaDB*), the options in
[Configuration and secrets](../areas/configuration-and-secrets.md), the record and the metrics in the [wire format](../areas/wire-format.md).

## Consequences

- No new dependency and no new right; the unit must keep `AF_UNIX` and the socket's directory reachable.
- **Residual risk of the uncaptured replies.** The error packet the server sends instead of the greeting (1040, 1053), the error
  numbers of a refused `unix_socket` login (1698 or 1045), the answer to `COM_PING`, the authentication switch request and the replies
  to the agent's own capability set (which differs from the captured client's in bits that, by the protocol documentation, change no
  reply to these statements) are built from the documentation. A server that answers otherwise makes the run fail as a gap with a
  fixed reason, never a wrong value; the first real run on the server, or a capture of the agent itself, settles it.
- MariaDB 10.6 to 12.x share the protocol and the statements, but only 12.3.3 is captured; MySQL is not supported (its process list
  has no `Progress` column, which fails the run).
- A server that asks for another authentication method gets no credential, so a user with a password fails every run as a gap.
- The full status and variable lists are read every run (about 40 KB from a local socket every 30 seconds) for the sake of the
  capture; a filtered query would need a new capture.
- A listener of another program on MariaDB's port counts as exposed; telling owners apart needs `CAP_SYS_PTRACE`.
