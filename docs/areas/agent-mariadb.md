# Agent MariaDB collector

## Scope

The agent's `mariadb` collector: how it logs in to the local MariaDB server over its Unix socket without a credential, which statements
it sends, how it decides availability, what it keeps of the replies, the metrics it derives and how its protocol client bounds what a
server sends. The backend and the report rely on its records; the account it logs in as is in
[Network and security model](network-and-security-model.md), *MariaDB access*.

## Collector

The collector `mariadb` (interval 30 seconds, pressure interval 10 seconds, timeout 5 seconds, after `systemd` in the job list) records
one `mariadb_status` record per run and, for an `up` run, its `mariadb_*` metrics, all with source `mariadb` and the capture time of the
run's start ([wire format records](wire-format-records.md)). It runs while `mariadb.enabled` is true (the default,
[Configuration options](configuration-options.md)); with `mariadb.enabled: false` it is not scheduled: no record, no log line, no
connection. It starts no program and uses no client library: it speaks MariaDB's client protocol itself.

## What a run does

Each run opens one connection to the Unix socket `mariadb.socket` (default `/run/mysqld/mysqld.sock`), reads the greeting, logs in as
`vandox-agent` (*The login*) and closes the connection at its end with `COM_QUIT`, sent with a write deadline of 250 ms and not waited
for. In between it sends, in this order: one `COM_PING`, whose round trip on the monotonic clock is `ping_latency_ns`; `SHOW GLOBAL STATUS`; `SHOW GLOBAL VARIABLES`; the account check's `SELECT CURRENT_USER()` and `SELECT PRIVILEGE_TYPE FROM information_schema.USER_PRIVILEGES WHERE GRANTEE LIKE '%vandox-agent%'`; and `SHOW FULL PROCESSLIST` only when the status value
`Threads_running` is above 10 (the agent's own statement counts) or the run started in memory pressure mode. No other statement is ever
sent and none takes input. The agent holds at most one connection at a time: a run's connection is closed before it returns, and the
collector is not started while a run is stuck. After the connection the run reads `MemTotal` of `/proc/meminfo` and the TCP tables
`/proc/net/tcp` and `tcp6` as far as the metrics need them.

**Time budget.** The greeting must arrive by 2 seconds after the run's start, everything else by 4 seconds: every read and write on the
connection carries that deadline, and the end of the run's context ends every wait at once. The scheduler's 5-second timeout is only the
backstop for the `/proc` reads: a read that blocks there is abandoned as a `collector_timeout` gap, no second connection is opened while
it is stuck, and no other collector waits.

## Availability

Every run that reaches a verdict writes one `mariadb_status` record with `complete` false (the record holds a selection):

- `down`, and nothing else: connecting fails with `ENOENT` (no socket file: stopped or not installed) or `ECONNREFUSED` (a socket file
  nobody accepts on, or a path that is no socket).
- `not_answering` without `error_code` and without values: connecting fails with `EAGAIN` (listen queue full) or does not finish by 4
  seconds, no greeting by 2 seconds, the login, the ping or any statement gets no answer by 4 seconds, or the server closes or resets the
  connection at any point, also mid-result. A run never writes part of the values.
- `not_answering` with `error_code` and without values: the server sends an error packet in place of its greeting (1040 no free
  connection, 1053 shutting down) or as the answer to the login (1698 or 1045 when `vandox-agent` is missing or not identified via
  `unix_socket`, 1226 at the user's connection limit), any number from 1 to 65535. An error packet with number 0 is recorded without
  `error_code`.
- `up`: the login, the ping, both selection statements and the account check were answered (an error answer to the check counts), and
  the process list too when it was due. The record holds `ping_latency_ns`, `status`, `variables` and, when the process list was read,
  `threads`.

Any other outcome fails the run (*Failures*).

## The login

The greeting is at most 1 KiB and must be protocol 10 with a NUL-terminated version text (never interpreted), the capabilities
`CLIENT_PROTOCOL_41`, `CLIENT_SECURE_CONNECTION`, `CLIENT_PLUGIN_AUTH` and `CLIENT_SESSION_TRACK`, capability bit 0 clear (MariaDB, not
MySQL), and the extended capabilities extended metadata (`0x08`) and cache metadata (`0x10`); anything else fails the run with
`unexpected reply`. The agent answers with the capabilities `0x00aea204` (no `CLIENT_LOCAL_FILES`, `CLIENT_MULTI_STATEMENTS`,
`CLIENT_CONNECT_ATTRS` or `CLIENT_DEPRECATE_EOF`, no TLS), the extended capabilities `0x18`, a maximum packet of 1 MiB, character set 45,
the user `vandox-agent`, **empty authentication data** and the plugin name `mysql_native_password`. The `unix_socket` plugin compares the
user name with the connecting process's OS user, so no password exists and none is ever sent. The server's answer:

- OK: logged in.
- An error packet: `not_answering` with its number.
- An authentication switch request (`0xfe` with a plugin name, at most 1 KiB, else `packet too large`; neither the plugin name nor its
  data is kept or logged): answered once with an empty packet, never a credential; the next packet is the login's answer. A second switch
  request, or any request for more authentication data (`0x01`) after the first, fails the run with `authentication switch refused`.
- A request for more authentication data (`0x01`) of more than 2 bytes before any switch also fails with `authentication switch refused`;
  one of 1 or 2 bytes, an empty packet, a `0xfe` packet without a plugin name and any other first byte fail with `unexpected reply`.

## The selection

Of `SHOW GLOBAL STATUS` the record keeps those of these 14 values the server reports: `Uptime`, `Threads_connected`, `Threads_running`,
`Max_used_connections`, `Aborted_connects`, `Aborted_clients`, `Questions`, `Slow_queries`, `Created_tmp_disk_tables`,
`Innodb_buffer_pool_pages_total`, `Innodb_buffer_pool_pages_free`, `Innodb_buffer_pool_read_requests`, `Innodb_buffer_pool_reads`,
`Memory_used`; of `SHOW GLOBAL VARIABLES` those of these 20, as text: `version`, `max_connections`, `innodb_buffer_pool_size`,
`innodb_log_buffer_size`, `key_buffer_size`, `aria_pagecache_buffer_size`, `query_cache_size`, `tmp_table_size`, `max_heap_table_size`,
`sort_buffer_size`, `read_buffer_size`, `read_rnd_buffer_size`, `join_buffer_size`, `thread_stack`, `binlog_cache_size`, `log_bin`,
`bind_address`, `skip_networking`, `port`, `extra_port`.

- A row's name is read up to 64 bytes and its value up to 1,025 bytes; the rest of a longer cell is discarded by length. Names are
  compared exactly (case-sensitive) with the 34 selected names; no other name is kept or remembered. A selected name twice in one reply
  fails the run (`name sent twice`).
- A status value is kept when it is 1 to 20 ASCII digits within an unsigned 64-bit number; a variable when its value was not cut and is
  at most 1,024 bytes once cleaned (every byte that does not start a valid UTF-8 sequence becomes U+FFFD). Any other value is left out and
  the rest of the record is written.

## Thread snapshot

When the process list is due, `threads` holds every row of `SHOW FULL PROCESSLIST` except the agent's own connection (the connection id of
the greeting), each with `id`, `user`, `host`, `db`, `command`, `state`, `time_seconds`, `info` (the statement text) and `truncated`. Order:
threads whose `command` is not `Sleep` first, then the sleeping ones, each by `time_seconds` descending, then `id` ascending.

- Cells are bounded on their raw bytes before anything is kept: `Id` and `Time` 20 bytes and `Progress` 64 bytes, strict (longer fails the
  run with `cell too long`); `User` and `Host` 512 bytes, `db` and `State` 256, `Command` 32 and `Info` 16,384, cut. `Id` must be 1 to 20
  ASCII digits within 64 bits, anything else, NULL included, fails the run with `unexpected reply`; a `Time` that is not 1 to 20 digits
  reads 0; a NULL text is empty.
- At most 4,096 threads are kept, the first in the order (a later row that ranks before the last kept one takes its place), and only the
  first 64 in the order keep their statement; a kept thread whose statement is dropped gets an empty `info` with `truncated` set.
- Every text is cleaned as the wire format's text rules require: invalid UTF-8 bytes become U+FFFD, the text is cut at a character
  boundary to 1,024 bytes (`user`, `host`, `db`, `command`, `state`) or 16,384 bytes (`info`, `truncated` set when it or its raw cell was
  cut), and control and format characters are kept as sent.
- These texts are chosen by whoever sends SQL to the server (any web-space user). They reach the record only, never a log line or an
  error text; whoever displays them escapes them like log text. They can hold customer data and credentials (`IDENTIFIED BY '...'`, the
  values of an `INSERT`), which are sent to the backend and shown there; `deploy/agent/README.md` says so.

## The record always fits

The collector bounds the record's encoded line by an estimate made before anything is encoded, never less than the encoded size: 6 bytes
per byte of every kept text (the longest JSON escape, `\u00XX`), 21 per number, 8 per map entry, 256 per thread for its keys and punctuation and 1 KiB for the record. Threads
that cannot fit within 1 MiB even without a statement (by the estimate of their raw short texts) are not built. Then, while the estimate is
above 1 MiB, every `info` is cut to 4,096, then 1,024, then 0 bytes (each cut sets `truncated`), and then the thread list is halved from
its end, so idle sleepers go first. The scheduler's record check therefore passes.

## Bounded memory

A run keeps of the replies only one scratch buffer per cell position, the 34 selected values and, for the process list, at most 4,096
thread slots of 1,568 bytes for the short texts and 64 statement buffers of 16 KiB (about 7.2 MiB), reused once full. A packet that is not
a row is read whole only up to its limit (greeting 1 KiB; OK, error, EOF, column count and column definition 64 KiB) and is refused above
it with `packet too large`; a row is read cell by cell. Pinned bounds: a run over 4,200 threads with every kept field at its cap and
statements of 20 KiB allocates at most 32 MiB in total and gives a record line of at most 1 MiB; a status or variables reply of 1,000,000
distinct names allocates at most 4 MiB; a cell of 64 MiB is discarded with at most 4 MiB allocated.

## The client's protocol rules (security area 10)

- A packet has a 3-byte length and a sequence number that must increase by one modulo 256 (else `packet out of sequence`); a payload of
  `0xffffff` bytes continues in the next packet. Length-encoded integers take the prefixes `0xfc`, `0xfd` and `0xfe`; `0xfb` is NULL in a
  row; `0xff` or a length beyond its packet is `malformed packet`.
- A statement must be answered by an error packet or by a column count packet of exactly two bytes: the statement's number of columns and
  the "metadata follows" byte 1 (else `unexpected reply`; another number is `unexpected columns`). Each column definition's name (its fifth
  length-encoded string) must be exactly the expected one (else `unexpected columns`): `Variable_name`, `Value`; `Id`, `User`, `Host`, `db`,
  `Command`, `Time`, `State`, `Info`, `Progress` (so MySQL, without `Progress`, fails the run); `CURRENT_USER()`; `PRIVILEGE_TYPE`. Then an
  EOF packet, the rows and an EOF packet; an EOF with `SERVER_MORE_RESULTS_EXISTS` is `more results refused`.
- An answer `0xfb` (a local-file request) is `local file request refused`, and nothing is read or sent for it; an OK packet or any other
  first byte is `unexpected reply`. The server's error message is skipped by its length and never read into a text.

## Account check

A login alone does not prove that the agent got the installed account: an anonymous `''@'localhost'` can accept it, and without `PROCESS`
the process list shows only the agent's own connection. Every `up` run therefore asks the two questions of *What a run does*; neither needs
a privilege, and an account sees only its own rows of `USER_PRIVILEGES`. The check passes when `CURRENT_USER()` answers exactly one row
`vandox-agent@localhost` (the cell read up to 1,024 bytes; a cut one does not match) and one row of the privileges is `PROCESS` (read up to
64 bytes). When it fails, the record is written as usual and the warning `mariadb user not as installed` is logged at the first such run
after the agent started or after a run whose check passed. When the server answers either question with an error, the check has no
result: the record is written, nothing is logged, the state does not change, and the agent closes the connection after that error, so a
process list due in that run is not read and the record has no `threads`. No answer in time is `not_answering`.

## Metrics

An `up` run writes after its `mariadb_status` record these `metric` records (names and units in the
[wire format records](wire-format-records.md), *Metric names*), each only when its inputs are there:

- `mariadb_memory_max_bytes`: *global buffers* + `max_connections` × *per-connection buffers*. Global: `innodb_buffer_pool_size` +
  `innodb_log_buffer_size` + `key_buffer_size` + `aria_pagecache_buffer_size` + `query_cache_size` + the smaller of `tmp_table_size` and
  `max_heap_table_size`; per connection: `sort_buffer_size` + `read_buffer_size` + `read_rnd_buffer_size` + `join_buffer_size` +
  `thread_stack`, plus `binlog_cache_size` when `log_bin` is `ON`. A variable is a number when it is 1 to 20 ASCII digits within 64 bits.
  A missing summand counts as 0 (so a missing `tmp_table_size` or `max_heap_table_size` makes the smaller 0); a missing or non-numeric
  `innodb_buffer_pool_size` or `max_connections`, or a present summand that is not a number (`binlog_cache_size` only while `log_bin` is
  `ON`), leaves both memory metrics out without a log line. Sum and product are exact in 128 bits. On the captured server the maximum
  is 898,035,712 bytes.
- `mariadb_memory_max_ram_percent`: 100 × that maximum / `MemTotal` (kB times 1,024, read with the `system` collector's bounds); left out
  alone when `/proc/meminfo` cannot be read or `MemTotal` is 0.
- `mariadb_innodb_buffer_pool_used_percent`: 100 × (`Innodb_buffer_pool_pages_total` − `Innodb_buffer_pool_pages_free`) /
  `Innodb_buffer_pool_pages_total`, from the first sample on; left out when either is missing, the total is 0 or the free pages exceed it.
- `mariadb_questions_per_second` (the increase of `Questions` over the seconds between the two runs' starts) and
  `mariadb_innodb_buffer_pool_read_hit_percent` (100 × (1 − increase of `Innodb_buffer_pool_reads` / increase of
  `Innodb_buffer_pool_read_requests`)). Both compare with the previous run and are left out on the first `up` run, when the previous run
  was not `up` or failed, when the previous sample was lost ([Agent](agent.md), *Scheduling*), after a restart (*Restarts*), when
  `Uptime` is missing, when the time between the runs' starts is not positive, and, each for itself, when its counter is missing in one
  sample or fell; the hit ratio also when the increase of the read requests is 0 or smaller than that of the reads.
- `mariadb_exposed_listeners` (no unit): the listening sockets in `/proc/net/tcp` and `tcp6` on MariaDB's `port` and, when not 0,
  `extra_port`, whose local address is not loopback once an IPv4-mapped address is unmapped. `127.0.0.0/8`, `::1` and
  `::ffff:127.0.0.0/104` are loopback; wildcards `0.0.0.0` and `::`, private, Tailscale, public and link-local addresses and the
  IPv4-compatible `::127.0.0.1` count, as does another program's listener on the port. A missing `tcp6` is skipped. The metric is left
  out, and its log state kept, when `port` is missing or not 1 to 65535, `extra_port` is present and not 0 to 65535, `tcp` is missing, or
  a table cannot be read or holds a line the connection collector's table rules refuse ([Agent connections](agent-connections.md),
  *Tables*). `bind_address` is not interpreted.

## Restarts

The server's start time is the run's start minus `Uptime` seconds. When it lies more than 5 seconds after the start time of the previous
`up` run of this agent run that had an `Uptime` (whatever runs lie between), the server restarted, also when systemd's state does not show
it: the warning `mariadb restarted` with `uptime_seconds` is logged and the rates are left out for that run. A start time that moves by 5
seconds or less, or backwards, is no restart.

## Logging

With `collector=mariadb`, no line per run while nothing changes, and only fixed texts with counts, error numbers and byte values (in
decimal) as attributes: never a statement, user, host, database name, server message, the value of `bind_address` or the socket path.

| Line | Attributes | When |
| ---- | ---------- | ---- |
| warning `mariadb down` | — | the first `down` run after the agent started or after a run that was not `down` |
| warning `mariadb not answering` | — | the first `not_answering` run without `error_code` after a run in another state |
| warning `mariadb user missing or not allowed` | `error_code`, `fix` = `run deploy/agent/mariadb-user.sql as the database administrator` | the first run refused with 1045 or 1698 after a run in another state |
| warning `mariadb login refused` | `error_code` | the first run refused with any other number after a run in another state |
| info `mariadb answering again` | — | the first `up` run after a run that was `down` or `not_answering` |
| warning `mariadb user not as installed` | `fix` (the same text) | *Account check* |
| warning `mariadb restarted` | `uptime_seconds` | *Restarts* |
| warning `mariadb memory maximum above RAM` | `max_bytes`, `ram_bytes` | the first `up` run whose maximum exceeds `MemTotal` after the agent started or after a run below it; a run without both values changes nothing |
| warning `mariadb listens on a non-loopback address` | `count` | the first run with a count above 0 after the agent started or after a run with 0 |
| info `mariadb listens on loopback only` | — | the first run with a count of 0 after a run above 0 |

The state of the first five lines is the availability with the error number: the agent starts out assuming `up`, so a first `up` run logs
nothing, and a refusal with another number (1040, then 1053) is a new episode. A failed run changes none of these states. A missing user is
reported at the first run after the start that reaches the server, and again when the user disappears while the agent runs.

## Failures

These fail the run: no record, a gap with cause `unknown`, and the scheduler logs `collector failed` once and `collector recovered`:

- connecting fails with any other error, for example `EACCES` when the unit hides the socket (`mariadb: connect: permission denied`), or
  with no OS error number (`mariadb: connect: failed`);
- a reply the client refuses (*The login*, *The selection*, *Thread snapshot*, *The client's protocol rules*);
- an error packet in answer to the ping or the status, variables or process list statement (`mariadb: <step>: server error <n>`).

The error texts are `mariadb: connect: <OS error text>` (never the path) or `mariadb: connect: failed`, and `mariadb: <step>: <reason>`
with the step `greeting`, `login`, `ping`, `status`, `variables`, `processlist` or `account` and the reason `malformed packet`, `packet out of sequence`, `packet too large`, `unexpected reply`, `unexpected columns`, `cell too long`, `local file request refused`, `more results refused`, `authentication switch refused`, `name sent twice` or `server error <n>`. The reasons `no answer in time` and `connection closed`
end a run as `not_answering`, not as a failure. No error text holds a byte the server sent.

## Residuals (record 0118)

- The replies not captured are built from the protocol documentation: the error packet in place of the greeting, the error numbers of a
  refused `unix_socket` login, the answer to `COM_PING`, the authentication switch and the replies to the agent's own capability set. A
  server that answers otherwise fails the run as a gap with a fixed reason, never with a wrong value.
- Only MariaDB 12.3.3 (`12.3.3-MariaDB-ubu2204`) is captured; MariaDB from 10.6 on is served by the same rules but not captured, and MySQL
  is not supported.
- The full status and variable lists, about 40 KB, are read every run, and the account check costs two small statements per `up` run.
- A listener of another program on MariaDB's port counts as exposed; telling owners apart is the connection collector's job.
- The process list shows what `PROCESS` lets the account see: the statements of every connection, with the customer data and credentials
  they may hold.

## Related decisions

- [0013](../decisions/0013-mariadb-access-via-unix-socket-process-privilege.md) — a `unix_socket` user with only `PROCESS`.
- [0118](../decisions/0118-mariadb-collector-hand-written-client-captured-statements-refusals-as-not-answering-with-error-code.md) — a
  minimal protocol client of its own over one connection per run, the captured statements and layout bits, a refused login recorded as
  `not_answering` with its error number, statement texts of only the first 64 threads, exposure counted from the kernel's TCP tables.

## Not here

- The account `vandox-agent`, its one source `deploy/agent/mariadb-user.sql` and its removal:
  [Network and security model](network-and-security-model.md), *MariaDB access*; creating and dropping it during installation:
  [Agent installation](agent-installation.md); what the unit must leave reachable: [Agent rights](agent-rights.md).
- Scheduling, the memory pressure mode and lost samples: [Agent](agent.md).
- The `mariadb_status` fields and metric names: [Wire format records](wire-format-records.md); `mariadb.socket` and `mariadb.enabled`:
  [Configuration options](configuration-options.md).

## Implementation

`internal/mariadb` (collector and protocol client; fixture `testdata/mariadb/`, two captures of MariaDB 12.3.3's replies to a
`unix_socket` login; it uses `sysstats.MemTotal`, `connstats.TCPListeners` and `procstats.CleanText`;
`TestMariadb_ImportsOnlyAllowedPackages` pins its imports and its only dial, `TestCollect_WorstCaseProcessListIsBounded`,
`TestCollect_LongStatusAndVariablesRepliesAreBounded` and `TestPacketReader_DiscardsBeyondTheLimitWithBoundedMemory` the memory bounds,
`TestThreads_TextIsCleanedAndNeverLogged` the log texts). `deploy/agent/mariadb-user.sql` and `mariadb-user-remove.sql` hold the account.
