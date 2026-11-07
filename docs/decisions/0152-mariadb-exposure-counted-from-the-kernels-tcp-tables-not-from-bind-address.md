# 0152: MariaDB's exposure is counted from the kernel's TCP tables, not from bind_address

- **Status:** Accepted
- **Date:** 2026-10-21
- **Area:** Agent MariaDB collector
- **Source:** Issue #35
- **Supersedes:** —

## Context

#35 asks whether MariaDB listens on a public address. The MariaDB collector
([0118](0118-mariadb-collector-hand-written-client-captured-statements.md)) reads the server's variables, `bind_address`, `port` and
`extra_port` among them, and the connection collector already reads the kernel's TCP tables
([0115](0115-connection-collector-proc-net-tables-regular-and-full-snapshots-in-one-record-kind.md)).

## Options considered

1. **Interpreting `bind_address`** — host names need a lookup, MariaDB 10.11 and later take lists, and a socket opened another way is
   missed.
2. **The listening sockets in the kernel's TCP tables on MariaDB's `port` and non-zero `extra_port`** (chosen), every address that is
   not loopback counted, read through the connection collector's table reader.

## Decision

The collector counts as exposed every non-loopback TCP listener on MariaDB's ports that the kernel's tables show, and does not
interpret `bind_address`. The rules are in [Agent MariaDB collector](../areas/agent-mariadb.md).

## Consequences

- A listener of another program on MariaDB's port counts as exposed; telling owners apart needs `CAP_SYS_PTRACE` and is the
  connection collector's job.
- A table the connection collector's reader refuses leaves the exposure count out of that sample.
