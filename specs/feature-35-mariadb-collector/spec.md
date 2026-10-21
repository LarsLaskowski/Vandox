# Spec: MariaDB collector

Source: Issue #35
Status: Approved by Product Manager

## Problem

MariaDB is the service the monitored server loses most often when it runs out of memory (`docs/ARCHITECTURE.md`, *Monitored
server*), yet nothing Vandox records today says how the database itself was doing before it died. The agent records the
memory of the `mariadbd` program (process collector) and the state of `mariadb.service` (systemd collector), but not whether the
database answered, how many connections it held, what it was running, how much memory its configuration allows it to take, or
whether it listens on an address the internet can reach. The wire format has carried a `mariadb_status` record since v0.1.0, but no
producer writes it. Without it, an outage reconstruction cannot tell "the database was down" from "the database was running but
stuck", cannot show the long-running queries that piled up before an OOM kill, and cannot say that the configured maximum memory of
MariaDB alone was larger than the server's 2 GB.

The agent must get this view without a database password, without access to table data (record 0013), and without ever being held
up by a database that does not answer (record 0029): a hanging MariaDB is exactly the situation the data is needed for.

## Scenarios

All records carry source `mariadb` and the capture time of the run's start; every log line named here carries `collector=mariadb`.
"The selection" means the status values and variables listed in S1.

- S1: Given MariaDB runs and the MariaDB user `vandox-agent` exists as `deploy/agent/mariadb-user.sql` creates it, when the collector
  `mariadb` runs (every 30 seconds), then the agent connects to the local socket as `vandox-agent` (no password) and writes one
  `mariadb_status` record with `availability` `up`, `ping_latency_ns` (the round trip of one ping), `complete` false, no `threads`, a
  `status` map with those of these status values the server reports — `Uptime`, `Threads_connected`, `Threads_running`,
  `Max_used_connections`, `Aborted_connects`, `Aborted_clients`, `Questions`, `Slow_queries`, `Created_tmp_disk_tables`,
  `Innodb_buffer_pool_pages_total`, `Innodb_buffer_pool_pages_free`, `Innodb_buffer_pool_read_requests`, `Innodb_buffer_pool_reads`,
  `Memory_used` — and a `variables` map with those of these variables the server reports, as text: `version`, `max_connections`,
  `innodb_buffer_pool_size`, `innodb_log_buffer_size`, `key_buffer_size`, `aria_pagecache_buffer_size`, `query_cache_size`,
  `tmp_table_size`, `max_heap_table_size`, `sort_buffer_size`, `read_buffer_size`, `read_rnd_buffer_size`, `join_buffer_size`,
  `thread_stack`, `binlog_cache_size`, `log_bin`, `bind_address`, `skip_networking`, `port`, `extra_port`. No log line is written while the
  database stays up.
- S2: Given MariaDB is stopped (its socket file is gone, or nothing accepts on it), when the collector runs, then it writes a
  `mariadb_status` record with `availability` `down` and nothing else but `complete` false, and logs the warning `mariadb down` at the
  first such run after the agent started or after the database was in another state.
- S3: Given MariaDB accepts the connection but does not answer in time, when the collector runs, then within 4 seconds of the run's
  start it writes a `mariadb_status` record with `availability` `not_answering` (no ping latency, no `error_code`, no values) and
  logs the warning
  `mariadb not answering` at the first such run in a row; every other collector keeps its schedule.
- S4: Given MariaDB sends an error packet in place of its greeting (no free connection: 1040; shutting down: 1053) or answers the
  login with an error (the user `vandox-agent` was dropped, for example by a Plesk update: 1698 or 1045; the user's connection
  limit: 1226), when the collector runs, then it writes a `mariadb_status` record with `availability` `not_answering` and the new
  optional field `error_code` (MariaDB's error number) and no values, and logs once per episode: for 1045 and 1698 the
  warning `mariadb user missing or not allowed` with `error_code` and `fix` = `run deploy/agent/mariadb-user.sql as the database
  administrator`, for any other number the warning `mariadb login refused` with `error_code`. This is the start-up check of the
  user: the first run after the agent starts that reaches the database reports a missing user, and so does the first run after the
  user disappears while the agent runs.
- S5: Given the database was down, not answering or refusing, when a later run gets an `up` sample, then the agent logs the info
  line `mariadb answering again` once.
- S6: Given MariaDB reports more than 10 running threads (`Threads_running`, the agent's own statement included), or the agent is
  in memory pressure mode, when the collector runs, then the `up` record also holds `threads`: every connection of the server's
  process list except the agent's own — threads whose `command` is not `Sleep` first, then the sleeping ones, each group
  longest-running first (`time_seconds` descending, then `id` ascending) — each with `id`, `user`, `host`, `db`, `command`, `state`,
  `time_seconds` and `info` (the statement's full text, cut to 16 KiB with `truncated` set when cut).
- S7: Given the variables of the theoretical maximum are reported, when the collector writes an `up` record, then it also writes the
  metric `mariadb_memory_max_bytes` (unit `bytes`) with *global buffers* + `max_connections` × *per-connection buffers*, where the
  global buffers are `innodb_buffer_pool_size` + `innodb_log_buffer_size` + `key_buffer_size` + `aria_pagecache_buffer_size` +
  `query_cache_size` + the smaller of `tmp_table_size` and `max_heap_table_size`, and the per-connection buffers are
  `sort_buffer_size` + `read_buffer_size` + `read_rnd_buffer_size` + `join_buffer_size` + `thread_stack` + `binlog_cache_size` (the
  last only when `log_bin` is `ON`); and the metric `mariadb_memory_max_ram_percent` (unit `percent`) = 100 × that maximum /
  `MemTotal` of `/proc/meminfo`. When the maximum is larger than `MemTotal`, the warning `mariadb memory maximum above RAM` with
  `max_bytes` and `ram_bytes` is logged at the first such run after the agent started and again after a run in which it was not.
- S8: Given two consecutive `up` samples of the same server lifetime (the server's start time, the run's start minus `Uptime`, moved
  forward by no more than 5 seconds; see E11), when the second one is written, then
  the agent also writes `mariadb_questions_per_second` (unit `per_second`: the increase of `Questions` over the seconds between the
  two runs' starts), `mariadb_innodb_buffer_pool_used_percent` (unit `percent`: 100 × (`Innodb_buffer_pool_pages_total` −
  `Innodb_buffer_pool_pages_free`) / `Innodb_buffer_pool_pages_total`; this one also on the first sample) and
  `mariadb_innodb_buffer_pool_read_hit_percent` (unit `percent`: 100 × (1 − increase of `Innodb_buffer_pool_reads` / increase of
  `Innodb_buffer_pool_read_requests`)).
- S9: Given MariaDB listens for TCP on an address that is not a loopback address (bind address empty, `*`, `0.0.0.0`, `::` or a
  public address of the server), when the collector writes an `up` record, then it writes the metric `mariadb_exposed_listeners` with
  the number of listening TCP sockets on MariaDB's `port` (and on `extra_port` when it is not 0) whose local address is not loopback, read from the kernel's TCP tables (0
  when there is none), and logs the warning `mariadb listens on a non-loopback address` with `count` at the first run that finds one
  after the agent started or after a run that found none; the info line `mariadb listens on loopback only` follows when the count is
  0 again.
- S10: Given an operator installs the agent, when they read `deploy/agent/README.md`, then it names `deploy/agent/mariadb-user.sql`,
  says that it is run once as the database administrator and may be run again (for example after a Plesk update dropped the user),
  what it creates (the user `vandox-agent`@`localhost` identified via `unix_socket`, `MAX_USER_CONNECTIONS 2`, only the privilege
  `PROCESS`), what that privilege lets the agent see and not see, and that the agent needs no group, capability or password for it.
- S11: Given `mariadb.enabled: false` in the agent's configuration, when the agent runs, then the collector `mariadb` does not run:
  no record, no log line, no connection to the socket.
- S12: Given the login succeeded, when it is the first `up` run after the agent started or after a run with an `error_code`, then the
  agent also asks the server which account it was authenticated as and whether that account holds `PROCESS` (fixed, read-only
  questions that need no privilege); when the account is not `vandox-agent`@`localhost` (for example an anonymous `''@'localhost'`
  accepted the login) or `PROCESS` is missing (the process list would show only the agent's own connection, so an empty `threads`
  would look normal), it logs the warning `mariadb user not as installed` with `fix` = `run deploy/agent/mariadb-user.sql as the
  database administrator` once until the next such check, and keeps collecting what it can.

## Errors and edge cases

- E1: MariaDB is not installed or was stopped cleanly, so the socket file does not exist: `down` every run, the warning `mariadb
  down` once per episode (S2); setting `mariadb.enabled: false` silences it.
- E2: MariaDB was killed (OOM) and left its socket file, or the configured path is a file that is not a socket: the connection is
  refused, which is `down` as well (S2), never a failed run.
- E3: MariaDB accepts the connection, or leaves it pending in a full queue, but sends no greeting within 2 seconds of the run's
  start, or closes the connection before a usable answer: `not_answering` without `error_code` (S3). An error packet that arrives in place of the greeting is not "no greeting": it
  is `not_answering` with its error number (S4).
- E4: The greeting and login succeed but the ping or a statement gets no answer by 4 seconds after the run's start, or the server
  closes the connection in the middle (it was killed while answering): `not_answering` with no values at all, never a record with part of the values (S3).
- E5: A read the collector cannot interrupt hangs (for example `/proc/meminfo` or the TCP tables under memory pressure): the scheduler
  abandons the run at its 5-second timeout and records a gap with cause `collector_timeout` and collector `mariadb`; the collector is
  not started again while the run is stuck, so the agent never holds a second MariaDB connection, and no other collector waits.
- E6: The user `vandox-agent` exists but is not identified via `unix_socket` (it has a password), or the agent runs as another OS
  user: MariaDB refuses the login with 1698 or 1045, which is `not_answering` with that `error_code` and the warning `mariadb user
  missing or not allowed` (S4). A server that asks the agent to switch to another authentication method (the user has a password
  of another plugin) gets no credential: the run fails as in E14 with the error text `mariadb: login: authentication switch
  refused`.
- E7: MariaDB is at `max_connections` and sends 1040 before any greeting, is shutting down and sends 1053, or the user's
  `MAX_USER_CONNECTIONS` is reached at login (1226): `not_answering` with that `error_code` and the warning `mariadb login refused`
  (S4), never a failed run or a gap. Any other error number from 1 to 65535, before the greeting or at the login, is recorded the
  same way.
- E8: More than 4,096 connections, or statements of megabytes, while a thread snapshot is due: the record keeps at most 4,096
  threads in the order of S6 (working threads before sleeping ones), each `info` cut to 16 KiB with `truncated`, and only the first
  64 threads of that order keep their `info` at all (the others get an empty `info` with `truncated` set when they had one), so the
  agent never holds more than 1 MiB of statement text; if the record is
  still too large for the wire format, the `info` texts are shortened further and then threads are left out from the end of that
  order, so idle sleepers go first (S6).
- E9: A statement text, user, host or database name holds control characters, format characters or invalid UTF-8 (any web-space user
  can send such SQL): it is stored as the wire format's text rules prescribe (invalid bytes as U+FFFD), and none of it ever appears
  in the agent's log (S6).
- E10: A variable of the maximum is missing (a server without Aria or the query cache) or not a plain number: a missing summand
  counts as 0, but a missing or non-numeric `innodb_buffer_pool_size` or `max_connections`, or any non-numeric summand, leaves both
  memory metrics out without a log line; the sum and product are computed exactly, so no setting can overflow it into a wrong value.
  `/proc/meminfo` that cannot be read leaves out only `mariadb_memory_max_ram_percent` (S7).
- E11: MariaDB restarted (its start time, the run's start minus `Uptime`, lies more than 5 seconds after the start time of the
  previous `up` sample of this agent run, whether or not samples that were not `up` lie between them), also while systemd restarted
  it without the service state showing it: the warning `mariadb restarted` with `uptime_seconds` is logged, and the rates of S8 are
  left out for that sample. A start time that moves by 5 seconds or less is the jitter of whole seconds and run starts, not a
  restart. The rates are also left out on the first sample, after a sample that was not `up`, when a counter fell, when the
  time between the runs is not positive, when the increase of `Innodb_buffer_pool_read_requests` is 0 or smaller than that of
  `Innodb_buffer_pool_reads`, and `mariadb_innodb_buffer_pool_used_percent` when `Innodb_buffer_pool_pages_total` is 0 or missing.
- E12: Listener forms: `127.0.0.0/8`, `::1` and the IPv4-mapped loopback `::ffff:127.0.0.0/104` are loopback; the wildcard `0.0.0.0`
  and `::`, any other address (private, Tailscale, public, link-local, the IPv4-compatible `::127.0.0.1`) count as exposed. A kernel without IPv6 has no `tcp6` table: only `tcp`
  is read. A listening socket of another program on MariaDB's port counts as well. When the tables cannot be read, or `port` is
  missing or not a number from 1 to 65535 (or `extra_port` is present but not a number from 0 to 65535), the metric is left out and
  the log state does not change (S9).
- E13: `mariadb.socket` that is not an absolute, clean path, is longer than 107 bytes (the limit of a Unix socket address) or holds a
  control or format character stops the agent at start-up with a configuration error that names the key and never the value;
  `mariadb.enabled` that is not a boolean likewise (S11).
- E14: A reply the agent cannot accept — a protocol violation, a value or packet larger than the agent's bounds, a status name sent
  twice, a statement that answers with an error after a good login (an error packet before or in answer to the login is S4, not
  this case): the run fails, the sample is a gap with cause `unknown`, and the
  scheduler logs `collector failed` once (and `collector recovered`) with an error text that holds a fixed reason and never text the
  server sent; the agent's memory for the run stays within its fixed bound whatever the server sends.
- E15: The agent may not use the socket (permission denied, for example a unit setting that hides `/run/mysqld`): a failed run as in
  E14 with the error text `mariadb: connect: permission denied`, not `down`.
- E16: A status value that is not a plain unsigned 64-bit number, or a variable value longer than 1,024 bytes: that entry is left out
  of its map; the rest of the record is written (S1).
- E17: The server answers a question of the account check of S12 with an error: unlike E14 the run does not fail; its record is
  written as for any other `up` run, no warning is logged, and the check is repeated at the next `up` run. No answer in time is E4
  as for every statement.

## Non-functional requirements

- **Schedule.** Collector name `mariadb`, interval 30 seconds, pressure interval 10 seconds, timeout 5 seconds, placed after `systemd`
  in the job list. The collector itself decides `not_answering` by 4 seconds after the run's start (greeting within 2 seconds), so the
  scheduler's timeout is only the backstop for reads it cannot interrupt (records 0029, 0110).
- **Load on the server.** At most one MariaDB connection at a time, opened by a run and closed at its end; with the user's
  `MAX_USER_CONNECTIONS 2` one connection stays free for an administrator. Per run a ping and a fixed handful of statements; the
  process list only when S6 calls for it. The agent's memory per run is bounded independently of what the server sends; the plan
  fixes the bound.
- **Security.** The agent authenticates only as the OS user it runs as, through `unix_socket`; no password or other credential is
  read, stored or sent, and Plesk's administrator account is never used (record 0013). It sends only fixed, read-only statements
  that take no input. It needs no new group, capability or system call; the systemd unit must keep `AF_UNIX` and must not hide
  `/run/mysqld` or the configured socket. Statement texts, user, host and database names of the process list are client-chosen text:
  they reach the record only, never a log line or an error text, and whoever displays them escapes them like log text. The values
  of `bind_address` and of the socket option are never logged.
- **Configuration** (agent, new section `mariadb`): `mariadb.enabled` (default `true`) and `mariadb.socket` (default
  `/run/mysqld/mysqld.sock`, the socket of Ubuntu's MariaDB package, which Plesk uses). The 10-thread threshold of S6 and the time
  budget are fixed, not options, like the pressure thresholds of record 0113.
- **Compatibility.** The target is the server's MariaDB 12.3 (`12.3.3-MariaDB-ubu2204`, the mariadb.org package for Ubuntu 22.04,
  captured under `testdata/mariadb/`). MariaDB from 10.6 on is supported by the same rules but not captured: the client reads no
  version from the greeting (so the `5.5.5-` prefix of 10.x greetings does not matter) and refuses any reply that differs from
  the captured layout as a failed run, never a wrong value. A server that lacks a listed value gives records without it; MySQL is
  not supported (its process list has no `Progress` column, which fails the run). The wire format gets one additive optional field, `mariadb_status.error_code` (1 to 65535, only with
  `not_answering`), which is a minor change under record 0043: a decoder of 1.0 ignores the unknown key and reads the record as
  `not_answering`, which stays true. The availability values are unchanged. Format 1.1 is unreleased (v0.1.0 shipped 1.0), so the
  field joins 1.1 in place as the fields of records 0115 and 0116 did.
- **Test data.** The protocol tests run against a fake that replays the real captures of MariaDB 12.3.3 under `testdata/mariadb/`
  (as `testdata/systemd/` pins the D-Bus fake) for every exchange the captures hold. The error packets before the greeting and at
  the login, the ping and the authentication switch were not captured (the Product Manager has no capture of them): those replies
  are built from the protocol documentation, marked as such in the test, and their residual risk is recorded in the decision
  record.
- **Logs.** Only the fixed lines of S2 to S9, S12 and E11, with counts, error numbers and byte values as attributes; no line per
  run.

## Acceptance criteria

- [ ] AC1: With a fake MariaDB that accepts a login and answers the ping and the statements, a run writes one `mariadb_status` record
  with `up`, a ping latency, `complete` false, exactly the reported entries of the selection as `status` and `variables` (a status
  value that is not an unsigned 64-bit number and a variable longer than 1,024 bytes left out), no `threads`, and logs nothing
  (S1, E16).
- [ ] AC2: With no socket file, with a stale socket nobody accepts on and with a path that is not a socket, a run writes `down`
  without values, logs `mariadb down` at the first such run only, and logs `mariadb answering again` at the first `up` run after it
  (S2, S5, E1, E2).
- [ ] AC3: With a fake that never sends a greeting, one that leaves the connection pending, and one that stops answering after the
  login (at the ping and at a later statement), a run writes `not_answering` without values by 4 seconds after its start, logs `mariadb
  not answering` once per episode, and the scheduler records no gap for it (S3, E3, E4).
- [ ] AC4: With a read of the collector that blocks until the test releases it, the scheduler abandons the run at 5 seconds with a
  `collector_timeout` gap for collector `mariadb`, does not start the collector again while it is stuck, opens no second connection to
  the fake, and other collectors keep their schedule (E5).
- [ ] AC5: With a fake that sends an error packet in place of the greeting (1040 and 1053) and one that answers the login with an error
  (1698, 1045 and 1226), a run writes `not_answering` with that `error_code` and no values, and records no gap; 1698 and 1045 log
  `mariadb user missing or not allowed` with `error_code` and the `fix` text, the others `mariadb login refused`, once per episode, also
  when the refusal first appears after `up` runs (S4, E6, E7).
- [ ] AC6: The Go model and the C# decoder accept `error_code` of 1 to 65535 with `availability` `not_answering`, refuse it outside
  that range or with `up` or `down`, and still refuse values with `not_answering`; the golden batches hold a `not_answering` record
  with an `error_code` and both contract tests read it (S4).
- [ ] AC7: With `Threads_running` of 11, or of 1 under memory pressure mode, the record holds every thread of the fake's process list
  but the agent's own, in the order of S6 (working threads before sleeping ones, each group by `time_seconds` descending, then
  `id`), with `info` cut to 16 KiB and `truncated` set where cut; with 10 and no pressure mode,
  or `Threads_running` missing, it holds none (S6).
- [ ] AC8: With more than 4,096 threads, or threads whose texts make the record too large for the wire format, the record keeps the
  first threads of the order of S6 within the wire limits as E8 orders the reductions (sleeping threads left out before working
  ones), and passes the wire format's record check; texts with
  control characters and invalid UTF-8 are kept as the wire rules prescribe and appear in no log line (E8, E9).
- [ ] AC9: For a table of variable sets, `mariadb_memory_max_bytes` and `mariadb_memory_max_ram_percent` equal the formula of S7
  (with `log_bin` `ON` and `OFF`, missing optional summands counting 0, values near 2^64 computed without overflow), both are left
  out for a missing or non-numeric required value or a non-numeric summand, only the percentage is left out when `/proc/meminfo`
  fails, and `mariadb memory maximum above RAM` is logged at the first run above RAM and again only after a run below it (S7, E10).
- [ ] AC10: Over a sequence of `up` samples the rate and ratio metrics of S8 have the exact values of their formulas, and are left
  out on the first sample, after a non-`up` sample, after a restart (the server's start time moved forward by more than 5 seconds,
  also across `down` samples between two `up` samples, which logs `mariadb restarted` with `uptime_seconds`; a move of 5 seconds or
  less logs nothing), when a counter fell, when the time between runs is not positive and in the zero and inconsistent cases of E11
  (S8, E11).
- [ ] AC11: For TCP table fixtures with listeners on MariaDB's port at `127.0.0.1`, `127.1.2.3`, `::1`, `::ffff:127.0.0.1`, `0.0.0.0`,
  `::`, `::127.0.0.1`, a public IPv4 and IPv6 address and a Tailscale address, on `extra_port` and on another port,
  `mariadb_exposed_listeners` counts exactly the non-loopback ones on `port` and on a non-zero `extra_port`; it is 0 for loopback only, read from `tcp` alone without a `tcp6` table, left out for
  unreadable tables or an invalid `port`, and the warning and info lines of S9 follow the changes of the count (S9, E12).
- [ ] AC12: With a fake that sends a malformed packet, a packet or value above the agent's bounds, a status name twice or an error
  to a statement after the login, and with a socket the agent may not use, the run fails as a gap with cause `unknown`, its error text
  holds no byte the fake sent (permission denied reads `mariadb: connect: permission denied`), and a test pins that the run's
  allocation stays within the bound the plan fixes for an oversized reply (E14, E15).
- [ ] AC13: The fake records every statement the agent sends: only the fixed read-only statements of the plan, the user name is the
  constant `vandox-agent` (MariaDB's `unix_socket` compares it with the OS user the agent runs as), the authentication data is
  empty, no password or other credential is sent, an authentication switch is answered by closing the connection, and at most one
  connection is open at any time (S1, E5, E6).
- [ ] AC14: The configuration accepts `mariadb.enabled` and `mariadb.socket` with their defaults and the example file sets both; a
  relative, unclean, longer than 107 bytes or control-character path and a non-boolean `enabled` stop the start-up with an error that
  names the key and not the value; with `enabled: false` the collector is not scheduled and never connects (S11, E13).
- [ ] AC15: `deploy/agent/mariadb-user.sql` exists, can be run twice without an error, creates `vandox-agent`@`localhost` identified
  via `unix_socket` with `MAX_USER_CONNECTIONS 2` and grants exactly `PROCESS` on `*.*`; `deploy/agent/README.md` documents it as S10
  says (S10).
- [ ] AC16: With a fake whose account check answers `vandox-agent`@`localhost` with `PROCESS`, nothing is logged; answering an
  anonymous account or without `PROCESS` logs `mariadb user not as installed` with the `fix` text once, again only after a run with an
  `error_code`, and the run's record is written either way; an error answer to the check writes the record, logs nothing and repeats
  the check at the next `up` run (S12, E17).
- [ ] AC17: The fake of the protocol criteria (AC1, AC3, AC5, AC7, AC8, AC12, AC13, AC16) replays the captures under
  `testdata/mariadb/` byte for byte for every exchange they hold — the greeting, the `unix_socket` login without an authentication
  switch, the status, variables, process list and account statements — and builds only what they do not hold: hangs, malformed and
  oversized packets, generated process lists, and, from the protocol documentation and marked as such, the error packets before the
  greeting and at the login, the ping's answer and the authentication switch (S1, S4).

## Out of scope

- Reading or parsing `SHOW ENGINE INNODB STATUS`; the issue names it only as something `PROCESS` allows.
- Detecting privileges beyond `PROCESS` (an over-privileged user is not reported); verifying the user's connection limit.
- RSS and PSS of `mariadbd`: the process collector already records them every minute (every 15 seconds under memory pressure) as the
  program `mariadbd` with `rss_bytes` and `pss_bytes` (Agent area, *Processes*); this collector does not read them again.
- Anything the backend does with the records and metrics: views, rules, alerts and the forensics report.
- Replication state, per-database sizes, the slow query log and the error log (the log import reads the error log).
- The systemd unit file and the install scripts of the agent (they follow with the spool and the sender).
- Telling which program owns a listener on MariaDB's port (that needs `CAP_SYS_PTRACE`; the connection collector records owners).

## Open questions

- Which per-connection buffers make up the theoretical maximum, and does it include global buffers besides the buffer pool?
  Answered: the issue names only the buffer pool and "per-connection buffers". The usual estimate (as MySQLTuner computes it)
  adds the other global buffers that MariaDB allocates up front — on a 2 GB server the default `key_buffer_size` and
  `aria_pagecache_buffer_size` of 128 MiB each are not negligible — so S7 defines both sums explicitly; a stricter or looser formula
  is a later change of the definition, not of the collector.
- How are "service down" and "running but not answering" told apart, and where does a refusal go? Answered: the wire format has
  `down` and `not_answering` (`internal/model/mariadb.go`, Wire format area). Down is "nothing accepts on the socket" (missing file
  or a refused connection), not answering is "accepted but no usable answer in time". An error packet before the greeting or at
  the login is the server answering that it will not serve the agent: recorded as a gap it would lose the "too many connections"
  state that the outage analysis needs, and `up` without values would read as normal. It is `not_answering` with the new optional
  `error_code` (S4): additive under record 0043, so no availability value is added and no versioning rule changes (*Challenge*, M1).
- How does the agent speak the MariaDB protocol, and what pins the fake? Answered: a minimal client of the agent's own (no new
  dependency), for the reasons record 0117 gave for D-Bus; the plan and its decision record weigh the library. The Product Manager
  chose option (a) of the escalation and supplied real captures of the server (`testdata/mariadb/`), which the fake replays (AC17);
  the error cases were not captured and are built from the protocol documentation (*Test data*).
- What is the "event" for a public listener? Answered: the agent has no event record of its own (`kernel_event` holds only
  `oom_kill` and `boot`; `log_line.event` is for log lines). The finding becomes the metric `mariadb_exposed_listeners`, which the
  backend can store and later alert on, plus a warning in the agent's log, which reaches the journal. "Public" is read strictly as
  "not loopback", because the agent cannot know which interface the internet reaches.
- What is the threshold for the snapshot of long-running queries? Answered: the issue sets none. `Threads_running` stays at 1 to 3
  on a quiet server (the agent's own statement counts); more than 10 means statements are piling up. Fixed, not an option, as the
  thresholds of record 0113 are until the parallel run (#45) gives a reason; the snapshot is also taken under memory pressure mode,
  as the process and connection collectors take their full snapshots then, because that is when MariaDB is killed.
- Does the collector need RSS/PSS of `mariadbd` itself? Answered: no, see *Out of scope*; the program entry of the process collector
  carries both.
- What is the start-up check? Answered: the first run after the start that reaches the database: a refused login reports a missing
  user (S4), and a successful one is followed by the account check of S12, because a login alone does not prove that the account is
  `vandox-agent` (an anonymous account can accept it) or that it holds `PROCESS`. Every later refusal is seen at once, not only at
  the next agent start.
- Which socket path? Answered: the server's variable `socket` is `/run/mysqld/mysqld.sock` (capture
  `testdata/mariadb/select-status-variables-processlist.txt`); the option exists for a server that differs.
- Which MariaDB version? Answered: the captures show `12.3.3-MariaDB-ubu2204`, not the 10.6 this spec first assumed; every value of
  the selection is present there, and *Compatibility* keeps 10.6 and later supported by the same rules, uncaptured.

## Area documents

- `docs/areas/agent.md` — a new section *MariaDB*: the collector's schedule, what it reads and writes (S1 to S9), the time budget,
  the availability rules, the formulas, the log lines, failures and gaps, the bounds on what the server sends, and the stated
  residuals (a foreign listener on the port counts; statement text is stored as sent); *Rights*: no new right, the socket must stay
  reachable; *Memory pressure mode*: the collector's pressure interval and thread snapshot; status line and *Implementation*.
- `docs/areas/wire-format.md` — `mariadb_status`: `error_code`, the producer paragraph; *Metric names*: the `mariadb_*` metrics
  with source `mariadb`; *Versioning*: what 1.1 added.
- `docs/areas/configuration-and-secrets.md` — the options `mariadb.enabled` and `mariadb.socket` with their rules.
- `docs/areas/network-and-security-model.md` — *MariaDB access*: the SQL file, the login and the account check at start-up,
  read-only fixed statements, a refusal recorded with its error number.
- Not an area document, but updated with them: `.squad/project.md` — security area 7 (the bounded decoding of the server's replies,
  as area 10 states it for the D-Bus client), area 11 (the MariaDB socket as a further local destination), area 12 (what the
  collector logs), and the *Test doubles* row "MariaDB socket" (the replaying fake and the capture under `testdata/mariadb/`).

## Challenge

- M1 (major, `refused` contradicts record 0043) — accepted. The precedent was wrong: `listener_check` is itself a 1.1 field. Instead
  of amending 0043, S4 now writes the existing `not_answering` with the new optional field `error_code`, which is additive: a 1.0
  decoder ignores it and still reads a true statement. NFR *Compatibility*, AC5 and AC6 changed; no versioning rule changes.
- M2 (major, error packet in place of the greeting) — accepted. S4, E3, E7 and E14 now say that an error packet before the greeting
  (1040, 1053) is `not_answering` with its number, never a gap; AC5 tests 1040 and 1053 before the greeting separately from the login
  errors. The claim about MariaDB's behavior is to be confirmed by the capture (AC17).
- M3 (major, protocol unspecified and no real sample) — accepted. A new open question states that the plan decides library or own
  client with reasons as in record 0117 (leaning: own minimal client), NFR *Test data* and AC17 require a fake that replays a capture
  under `testdata/mariadb/`, with the variants the Devil's Advocate lists. No such capture exists; the source is escalated to the
  Product Manager.
- m1 (snapshot order) — accepted: working threads first, then sleeping ones, each by `time_seconds` descending, then `id` (S6, E8,
  AC7, AC8).
- m2 (restart detection) — accepted: the server's start time (run start minus `Uptime`) moving forward by more than 5 seconds is a
  restart, also across samples that were not `up` (S8, E11, AC10).
- m3 (account check) — accepted: S12, E17 and AC16 add a fixed check of the authenticated account and `PROCESS` at the first `up` run
  after the start or after a refusal, with the warning `mariadb user not as installed`. Over-privilege stays out of scope.
- m4 (`.squad/project.md`) — accepted: listed under *Area documents* (security areas 7, 11, 12 and the *Test doubles* row).
