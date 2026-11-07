# Wire format records

## Scope

The payload of every record kind of the [wire format](wire-format.md): the fields of `metric`, `process_snapshot`,
`connection_snapshot`, `service_state`, `mariadb_status`, `kernel_event`, `backup_run`, `log_line` and `gap`, the field errors
their validation reports, the metric names every producer shares, the snapshot trigger and how a producer maps connection
endpoints. The agent's collectors and the log import produce these records; the backend's decoder and storage rely on them. The
common rules for keys, text, names, lengths, times, `null` and field paths that every payload follows are in [Wire
format](wire-format.md), *Common rules*.

## Record kinds

The `kind` of a record line (envelope in [Wire format](wire-format.md), *Records*) is one of the kinds below; `data` holds the
payload. The field errors named here are reported alike by both decoders and by the producer's own record check; each is
a field path and a fixed reason.

### `metric`

`name` (name pattern, required), `value` (finite number), `unit` (optional name pattern), `labels` (optional
map of name-pattern key to short text, at most 32, keys unique ignoring ASCII case). Counters are named with the suffix `_total` (convention,
not validated).

### Metric names

These names, units and labels are used by every producer of these values, the agent's collectors and the importer's
legacy monitoring log parser ([Legacy monitoring log parser](log-parsers-legacy-monitoring.md)), so the same value has the same name
whoever produced it. Byte values are exact integers in a double (up to 2^53 exactly), counters carry the suffix `_total`.

| Name | Unit | Labels | Value |
| ---- | ---- | ------ | ----- |
| `load1`, `load5`, `load15` | — | — | load average over 1, 5 and 15 minutes |
| `cpu_percent` | `percent` | `mode`: `user`, `system`, `nice`, `idle`, `iowait`, `irq`, `softirq`, `steal` | share of CPU time in the mode (`top`: `us sy ni id wa hi si st`) |
| `mem_total_bytes`, `mem_free_bytes`, `mem_used_bytes`, `mem_buff_cache_bytes`, `mem_available_bytes` | `bytes` | — | memory as `top` prints it (`avail Mem` for the available memory) |
| `swap_total_bytes`, `swap_free_bytes`, `swap_used_bytes` | `bytes` | — | swap as `top` prints it |
| `meminfo_bytes` | `bytes` | `field`: the name in `/proc/meminfo` | a value in `kB`, times 1,024 |
| `meminfo` | — | `field` | a value without a unit (for example `HugePages_Total`) |
| `pressure_avg10`, `pressure_avg60`, `pressure_avg300` | `percent` | `resource`: `memory`, `cpu`, `io`; `kind`: `some`, `full` | pressure stall average over 10, 60 and 300 seconds |
| `pressure_stall_microseconds_total` | `microseconds` | `resource`, `kind` | total stall time |
| `vmstat_total` | — | `counter`: `pswpin`, `pswpout`, `pgmajfault`, `oom_kill` | the raw counter of `/proc/vmstat` |
| `vmstat_per_second` | `per_second` | `counter` | (value − previous value) / seconds between two samples; only when the counter did not fall |
| `disk_reads_total`, `disk_writes_total` | — | `device`: the disk's name in `/sys/block` | reads and writes completed |
| `disk_read_bytes_total`, `disk_written_bytes_total` | `bytes` | `device` | sectors read and written, times 512 |
| `disk_io_time_seconds_total` | `seconds` | `device` | time with I/O in flight |
| `disk_reads_per_second`, `disk_writes_per_second` | `per_second` | `device` | rate of the counter between two samples, as for `vmstat_per_second` |
| `disk_read_bytes_per_second`, `disk_written_bytes_per_second` | `bytes_per_second` | `device` | rate of the byte counter between two samples |
| `disk_busy_percent` | `percent` | `device` | increase of the time with I/O in flight as a share of the time between two samples |
| `filesystem_size_bytes`, `filesystem_used_bytes`, `filesystem_free_bytes`, `filesystem_available_bytes` | `bytes` | `mount`: the configured mount point | size, used, free and available to unprivileged users, from `statfs` |
| `filesystem_inodes`, `filesystem_inodes_used`, `filesystem_inodes_free` | — | `mount` | inodes in total, used and free; left out when the file system reports 0 inodes |
| `clock_synchronized` | — | — | 1 when the kernel's maximum clock error is below 16 seconds, else 0 |
| `clock_max_error_seconds`, `clock_estimated_error_seconds` | `seconds` | — | the kernel's maximum and estimated clock error |
| `clock_pll_offset_seconds` | `seconds` | — | the correction the kernel's phase-locked loop still has to apply; only while the loop is on |
| `agent_rss_bytes` | `bytes` | — | resident memory of the agent itself (`VmRSS`, kB times 1,024) |
| `agent_cpu_seconds_total` | `seconds` | — | user plus system CPU time the agent used since it started |
| `agent_goroutines` | — | — | number of goroutines of the agent |
| `agent_spool_bytes` | `bytes` | — | size of the agent's spool files on disk (its data files and its meta file) |
| `agent_spool_age_seconds` | `seconds` | — | the sample's start minus the capture time of the oldest record the spool holds unacknowledged; 0 when it holds none or that time lies after the start |
| `agent_send_lag_seconds` | `seconds` | — | the sample's time minus the capture time of the newest record the sender has had acknowledged since the agent started (before the first acknowledgement: minus the start), never negative; it stays high while the backend does not accept batches |
| `mariadb_memory_max_bytes` | `bytes` | — | MariaDB's theoretical maximum memory: its global buffers plus `max_connections` times its per-connection buffers |
| `mariadb_memory_max_ram_percent` | `percent` | — | that maximum as a share of `MemTotal` |
| `mariadb_questions_per_second` | `per_second` | — | increase of the status value `Questions` / seconds between two samples |
| `mariadb_innodb_buffer_pool_used_percent` | `percent` | — | used pages of the InnoDB buffer pool as a share of its pages |
| `mariadb_innodb_buffer_pool_read_hit_percent` | `percent` | — | share of the buffer pool's read requests between two samples that did not read from disk |
| `mariadb_exposed_listeners` | — | — | listening TCP sockets on MariaDB's ports whose local address is not loopback |

The `agent_*` metrics are the agent's self-metrics ([Agent](agent.md), *Self-metrics*) and carry the source `agent`, as do the gaps
the agent's scheduler records. The agent's system metrics (`load*`, `cpu_percent`, `mem_*`, `swap_*`, `meminfo*`, `pressure_*`,
`vmstat_*`, `disk_*`, `filesystem_*`, `clock_*`) carry the source `system` ([Agent system metrics](agent-system-metrics.md)). The
`mariadb_*` metrics carry the source `mariadb`, like the `mariadb_status` record they are written with ([Agent MariaDB
collector](agent-mariadb.md), where their formulas and the cases that leave them out are).

### `process_snapshot`

`complete` (true only if `processes` and `programs` hold every process and program the producer saw; false when
the source shows only part of them or entries were left out for size), `total` (optional, processes on the
system), `processes`, `programs`; at least one process or program. A process: `pid` (> 0, unique in the
snapshot), `ppid` (>= 0), `user` (short text), `command` (required short text), `cmdline` (text), `truncated`
(command or cmdline cut by the source), `state` (empty or one ASCII letter), `started_at` (optional),
`cpu_percent`, `rss_bytes`, `pss_bytes`, `swap_bytes` (optional), `oom_score_adj` (optional, -1000 to 1000).
A program: `program` (required short text, unique), `count` (>= 1), `cpu_percent`, `rss_bytes`, `pss_bytes`
(optional), `rss_overcounted` (the RSS sum counts shared pages once per process). `trigger` (optional, since format 1.1): the
kills the snapshot was taken for (*Snapshot trigger*).

### Snapshot trigger

`process_snapshot` and `connection_snapshot` carry an optional `trigger` when the producer took the snapshot because of OOM kills:
`{"kills":[{"boot_id":…,"kernel_time_ns":…,"victim_pid":…}],"kills_omitted":n}`. `kills` holds 1 to 64 distinct kills, each by the
identity of its `kernel_event` (*`kernel_event`*): `boot_id` (lower-case UUID), `kernel_time_ns` (>= 0) and `victim_pid` (> 0);
`kills_omitted` (optional, omitted when 0) counts the further kills the snapshot was taken for but does not name. Field errors:
`data.trigger.kills`: `at least one kill required` and `too many entries`; `data.trigger.kills[<i>]`: `duplicate kill`;
`data.trigger.kills[<i>].boot_id`: the UUID rule; `.kernel_time_ns`: `must not be negative`; `.victim_pid`: `must be greater than
0`. A snapshot without `trigger` is a scheduled one. Producer: the agent's `processes` and `connections` collectors, in the extra run
the `kernel` collector requests for live kills ([Agent kernel events and boots](agent-kernel-events.md)); such a snapshot lists every
process, or every connection, and is a full one.

### `connection_snapshot`

`complete` (true only if no entry the producer saw was left out of any list, in particular `connections` holds
every socket; false after a reduction for size) and the lists `states` (`proto`, `state`, `count`),
`processes` (`pid` > 0, `command`, `count`), `remotes` (`addr`, `count`), `listeners` (`proto`, `local`, `pid`,
`command`, `uid`, `user`, `mixed_uids`) and `connections` (`proto`, `local`, `remote`, `state`, `pid`, `command`, `uid`, `user`). An
empty snapshot is valid. `proto` is `tcp`, `tcp6`, `udp` or `udp6`; `state` is required for `tcp`/`tcp6` and optional for
`udp`/`udp6`, from `ESTABLISHED SYN_SENT SYN_RECV FIN_WAIT1 FIN_WAIT2 TIME_WAIT CLOSE CLOSE_WAIT LAST_ACK
LISTEN CLOSING NEW_SYN_RECV`; `count` >= 1; `pid` >= 0. `trigger` (optional, since format 1.1) names the kills a snapshot was taken
for (*Snapshot trigger*).

**Socket UID (since format 1.1, record 0146).** A listener entry (`listeners`, `new_listeners`, `changed_listeners`) and a connection
entry carry the optional `uid` (an integer from 0 to 4294967295: the socket's UID from the kernel's table, not the UID of a process
that holds it) and `user` (short text: the name of that UID, or the UID in decimal when the producer knows no name); a listener entry
also the optional `mixed_uids` (true: the endpoint's sockets have more than one UID, and `uid` is the lowest of them). An **absent
`uid` means unknown**, never root: a socket without a file, an imported legacy snapshot, a 1.0 agent's snapshot or a stored record
from before the field. A `uid` of 0 is written; `user` is left out when empty and `mixed_uids` when false. The rules, checked after
the `pid`/`command` rules of the entry and in this order: a non-empty `user` without `uid` is `data.<list>[<i>].user`: `requires uid`; a
`mixed_uids` true without `uid` is `data.<list>[<i>].mixed_uids`: `requires uid`; then `user` follows the short-text rule (`too long` over
1,024 bytes). `uid` without `user` is accepted (a reader shows the number). A JSON `null` of any of the three reads as absent (0089),
and `"user":""` and `"mixed_uids":false` read as absent too, so they are accepted with or without `uid`. A `uid` that is negative
(also `-0`), a fraction, above 4294967295, a string or `1e400` is malformed, the rule for integers out of range. `mixed_uids` on a
connection entry is an unknown key and ignored. The agent's producer rules (which line gets a UID, the name lookup) are in [Agent
connections](agent-connections.md), *Socket UIDs*.

Since format 1.1 nine optional fields say what kind of snapshot it is, what its producer compared and what its own list holds (all omitted
when false, zero or absent):

- `aggregates_only` (true): a **regular** snapshot of the connection collector: the aggregates (`states`, `processes`, `remotes`,
  `listeners`) without `connections`. Requires `complete` false and no `connections`. Its `remotes` are the top 25 by count over the
  addresses the producer counted (at most 4,096; a heavy address first seen after the cap is missing).
- `aggregates_incomplete` (true): `states`, `listeners` or `processes` may not cover every socket the producer saw (a list cut, a table
  line skipped, sockets without an owner beyond a cap, owners unknown). Requires `complete` false.
- `listener_check` (`all`, `privileged`, `none`, `start`): the comparison of the listener inventory with the last successful run's that
  the producer made for this snapshot: the whole inventory; only the listeners on ports 1 to 1023, because an inventory was cut at
  4,096; none; or `start`, the first run after the producer started, which has no earlier inventory (nothing is compared, like `none`;
  record 0148).
- `new_listeners` (listener entries, at most 4,096): the listeners the producer found new in the run, with the owner's `pid` and `command`
  when known and the socket UID fields as the endpoint's `listeners` entry has them. Requires `listener_check` `all` or `privileged`.
- `new_listeners_omitted` (>= 1): how many more new listeners were found than `new_listeners` holds. Requires `listener_check` `all` or
  `privileged`.
- `changed_listeners` (changed-listener entries, at most 4,096): the known listener endpoints whose socket UID changed since the
  producer's previous run (record 0147, issue #305). An entry is a listener entry (`proto`, `local`, `pid`, `command`, `uid`, `user`,
  `mixed_uids`: the endpoint as it is now) plus `previous_uid` (0 to 4294967295, required) and `previous_mixed_uids` (true when the
  endpoint's sockets had several UIDs before; left out when false). Its `uid` is required too. Requires `listener_check` `all` or
  `privileged`. The list counts toward the item limit as `new_listeners` does.
- `changed_listeners_omitted` (>= 1): how many more changed listeners were found than `changed_listeners` holds. Requires
  `listener_check` `all` or `privileged`.
- `inventory` (`all`, `privileged`, `cut`, `skipped`): how much of the run's listener inventory this snapshot's `listeners` list holds:
  all of it; every listener on ports 1 to 1023 but not necessarily the rest; fewer than the ports 1 to 1023 hold (a cut inside the
  privileged part); or nothing reliable because a table line was skipped. A reader that compares two snapshots needs it, because
  `aggregates_incomplete` has other causes too.
- `tables` (protocols, distinct, at most four): the tables the run read, in table order. Two snapshots read from different tables are
  not comparable.

The absence of all nine reads as a **full** snapshot with complete aggregates and no listener check, which is what an imported legacy
snapshot and a 1.0 agent's snapshot are. Field errors: `data.complete`: `must be false for an aggregates-only snapshot` and `must be
false when aggregates are incomplete`; `data.connections`: `must be empty in an aggregates-only snapshot`; `data.listener_check`: one of
`all`, `privileged`, `none`, `start`, `must be all or privileged when new listeners are reported` and (checked after it) `must be all
or privileged when changed listeners are reported`; `data.changed_listeners[<i>].uid` and `data.changed_listeners[<i>].previous_uid`:
`required` (a `previous_mixed_uids` without a previous UID is refused the same way), after the listener entry rules of the entry;
`data.inventory`: one of `all`, `privileged`, `cut`, `skipped`; `data.tables[<i>]`: one of `tcp`, `tcp6`, `udp`, `udp6`, and
`duplicate protocol`. A `new_listeners_omitted` or `changed_listeners_omitted` of 0 or `null` reads as absent and a negative one is
malformed. A JSON `null` of `listener_check` or `inventory` reads as absent (0089), and a `null` element of `tables` reads as an empty
protocol that fails the one-of rule; the encoder never writes an empty `listener_check`, `inventory` or `tables`.

### `service_state`

`unit` (required, `^[A-Za-z0-9:_.@\-]+$`, at most 256 bytes), `load_state` (`loaded not-found bad-setting
error masked stub merged`), `active_state` (`active reloading inactive failed activating deactivating
maintenance refreshing`), `sub_state` (optional, `^[a-z0-9-]+$`, at most 64 bytes), `active_enter_at`
(optional), `restarts`.

Producer: the agent's `systemd` collector ([Agent services and backup runs](agent-services.md)) writes it with source `systemd` and
the capture time of the run's start, one record per tracked unit with `unit` the unit's primary name (aliases resolved). A record is
written for the first run after an agent start, for the first run after a lost sample, when `load_state`, `active_state`,
`sub_state`, `active_enter_at` or `restarts` differs from the unit's last record, and again once the last record is an hour old.
`active_enter_at` is left out when systemd has none (the unit was never active) or the time cannot be stored; `restarts` is systemd's
`NRestarts`, 0 for a unit that is not found or masked. The collector checks each record against these rules itself and leaves out a
unit whose state they refuse.

### `mariadb_status`

`availability` (`up`, `down`, `not_answering`), `ping_latency_ns` (optional, >= 0), `error_code` (optional, 1 to 65535, only with
`not_answering`), `status` (map of `^[A-Za-z][A-Za-z0-9_]*$` key, at most 128 bytes, unique ignoring ASCII case, to unsigned
integer), `variables` (same keys, short text values), `threads`, `complete` (true only if `status`, `variables` and `threads` hold
every entry the producer read; false for a configured subset or a reduction for size). When `availability` is not `up`, `status`,
`variables` and `threads` must be empty. A thread: `id`, `user`, `host`, `db`, `command`, `state` (short text), `time_seconds`,
`info` (text), `truncated` (the producer shortened `info`).

`error_code` is MariaDB's error number when the server refused the producer with an error packet in place of its greeting or as the
answer to the login (for example 1040 too many connections, 1698 or 1045 access denied): the server answered, but would not serve. It
was added in format 1.1 (record 0151); a decoder that does not know it ignores it and reads a true `not_answering`. Field errors:
`data.error_code`: `must be 1 to 65535` (the value 0) and `only with not_answering`; a value outside 0 to 65535 is malformed like every
integer out of its field's range ([Wire format](wire-format.md), *Accepted forms*), and `null` reads as absent (0089).

Producer: the agent's `mariadb` collector ([Agent MariaDB collector](agent-mariadb.md)) writes one record per run with source
`mariadb` and the capture time of the run's start: `down` when nothing accepts on the socket, `not_answering` when the server's listen
queue is full or it gives no usable answer in time, and with `error_code` when it refuses the agent with an error packet, and `up`
with `ping_latency_ns`, the selected `status` values and `variables`, and `threads` only when its thread snapshot is due. `complete`
is always false, because the record holds a fixed selection; `info` is cut to 16,384 bytes and kept for at most 64 of at most 4,096
threads, and the producer shortens the record by an upper-bound estimate before it is encoded ([Wire format](wire-format.md),
*Producer size contract*).

### `kernel_event`

`type` is `oom_kill` or `boot`; exactly the matching sub-object is set. `oom_kill`: `victim_pid` (> 0), `victim_command` (required
short text), `anon_rss_bytes` (optional), `oom_score_adj` (optional, -1000 to 1000), and since format 1.1 `boot_id` (optional
lower-case UUID, field `data.oom_kill.boot_id`) and `kernel_time_ns` (optional, >= 0, field `data.oom_kill.kernel_time_ns`: `must not
be negative`). `boot`: `boot_id` (required lower-case UUID), `previous_boot_id` (optional UUID), `booted_at` (optional),
`previous_uptime_ns` (optional, >= 0). `message` is text.

Producer: the agent's `kernel` collector ([Agent kernel events and boots](agent-kernel-events.md)) writes it with source `kernel`. An
`oom_kill` carries the boot ID of the kill's journal entry and the kernel's clock reading of the kill in nanoseconds; with
`victim_pid` they are the kill's identity, the same in every reading of the kill and different for any two kills, and a `trigger`
names a kill by them (*Snapshot trigger*). Its `captured_at` is the moment the kernel wrote the line, `message` the kill line. A
`boot` is captured at `booted_at` (at the agent's run start when the boot time is unknown).

### `backup_run`

Since format 1.1. The start or the end of a backup run: `phase` (`start` or `end`), `boot_id` (required lower-case UUID, the boot the
run started in) and `started_at` (required, the start of the run; it pairs an end with its start). A `start` also has `pid` (> 0) and
`command` (required short text) of the earliest matching process and `processes` (>= 1), the number of matching processes; a producer
sets `captured_at` to `started_at`. An `end` also has `last_seen_at` (required, not before `started_at`), `gone_by` (required, not
before `last_seen_at`; the real end lies between the two) and `max_processes` (>= 1); a producer sets `captured_at` to `last_seen_at`.
A field of the other phase is refused. Field errors: `data.phase`: `unknown value`; `data.last_seen_at`, `data.gone_by`,
`data.max_processes`: `not allowed for phase start`; `data.pid`, `data.command`, `data.processes`: `not allowed for phase end`;
`data.last_seen_at`: `must not be before started_at`; `data.gone_by`: `must not be before last_seen_at`; `data.pid`: `must be greater
than 0`; `data.processes`, `data.max_processes`: `must be at least 1`; and the rules for UUIDs, required texts and times.

Producer: the agent's `backups` collector ([Agent services and backup runs](agent-services.md)), source `backups`. Storage keeps it
as a JSON payload like the other kinds besides metrics and log lines ([Storage](storage.md)).

### `log_line`

`log` (required short text: `journal`, or the file path: the absolute path for the agent, the path as the import lists it for
imported records, relative to the import root or archive and with its rotation suffix), `host` (optional short text: the host that
wrote the line, empty when unknown), `program` (short text), `pid` (>= 0), `priority` (optional, 0 to 7), `message` (text, may be
empty), `truncated` (the producer cut the message to 16384 bytes), `event` (optional name, see [Wire format](wire-format.md), *Name
pattern*: the event a producer recognized in the line, such as `mariadb.start`; empty for none). `host` and `event` are additive
optional fields, so the wire version stayed 1.0 ([0084](../decisions/0084-log-line-record-gets-an-optional-host-field.md),
[0088](../decisions/0088-mariadb-error-log-entries-by-content-and-lifecycle-events-in-log-line.md)). The encoder omits an empty `host`
and an empty `event`; a decoder reads a line without them as empty.

Producers: the import ([Log import](log-import.md)) and the agent's `logs` collector ([Agent log shipping](agent-log-shipping.md)). The
agent's records have source `journal` with `log` `journal`, or source `syslog`, `plesk-panel`, `nginx-error`, `sw-cp-server-error` or
`apache-error` with `log` the configured absolute path, and the `host`, `program`, `pid`, `priority` and `message` the import's
journal, syslog and error log mappings give for the same line (an error log line has no `host`, `program` or `pid`); like the import
it sets `truncated` also when it cut `host` or `program` to 1,024 bytes. Its `event` and priority are set by the stages of the
agent's classification ([Agent log shipping](agent-log-shipping.md), *Classification*) as the import sets them: the mail events of
Postfix and Dovecot lines and the MariaDB events, and a MariaDB entry or a kernel report is sent as one record; the error log sources
have no `event`. A journal line and the same line of an rsyslog file are two records, as in the import.

### `gap`

`from`, `to` (UTC, `to` after `from`), `cause` (`agent_not_running spool_dropped collector_timeout sequence_missing no_data
unknown`), `collector` (name pattern, required for `collector_timeout`), `first_seq` and `last_seq` (both omitted or both > 0 with
`first_seq <= last_seq`; both required for `spool_dropped` and `sequence_missing`). The causes `sequence_missing` and `no_data` are set
by the backend and are rejected in agent records. `from` and `to` are not bound to the record's `captured_at`: a gap the agent records
for a run it abandoned or skipped covers the sample's slot up to one interval after the due time, so its `to` lies after its
`captured_at` ([Agent](agent.md), *Scheduling*).

## Producer mapping for connection endpoints

Endpoints are numeric IP addresses and ports. Mapping a source's notation is the producer's job (the connection collector and the
legacy `lsof -ni` parser):

- a zone (`fe80::1%eth0`) is stripped, the link-local address itself is kept; a zone means nothing to the backend and is rejected
  with reason `zone not allowed`;
- a wildcard local address (`*` in `lsof`) becomes `0.0.0.0` for IPv4 and `::` for IPv6 sockets;
- a port given as a service name (legacy `lsof -ni` without `-P`) is mapped to its number from a built-in table, never by a lookup on
  the host;
- a socket whose address or port cannot be mapped is left out and reported (the importer counts it as a skipped line) and `complete`
  is set to false;
- an unconnected socket without a remote endpoint (listening TCP, unconnected UDP) goes to `listeners`, never to `connections`;
- the connection collector reads the kernel's tables `/proc/net/{tcp,tcp6,udp,udp6}`: each address word is a 32-bit number in the
  host's byte order (one word for IPv4, four for IPv6), no zone is written, and an IPv4-mapped address in `tcp6`/`udp6`
  (`::ffff:a.b.c.d`) is kept as it is, not unmapped, so `remotes` can hold one peer twice.

## Related decisions

- [0063](../decisions/0063-storage-schema-records-table-typed-metric-and-log-tables-json-payloads.md) — why metrics and log lines get
  typed tables and the other kinds JSON payloads.
- [0084](../decisions/0084-log-line-record-gets-an-optional-host-field.md) — why `log_line` gets an optional `host`.
- [0088](../decisions/0088-mariadb-error-log-entries-by-content-and-lifecycle-events-in-log-line.md) — why `log_line` gets an optional `event`.
- [0089](../decisions/0089-json-null-reads-as-an-absent-key-in-both-decoders.md) — why a JSON `null` reads as an absent key, also in payloads.
- [0115](../decisions/0115-connection-collector-proc-net-tables-regular-and-full-snapshots-in-one-record-kind.md) — why a regular and a full
  snapshot are one record kind with additive fields, and format 1.1.
- [0146](../decisions/0146-socket-uid-from-the-tables-uid-column-lowest-uid-and-mixed-uids-per-endpoint.md) — why entries carry the socket's
  `uid` beside a `user` name, absent meaning unknown, and an endpoint's lowest UID with `mixed_uids`.
- [0147](../decisions/0147-a-known-endpoint-whose-socket-uid-changes-is-reported-as-a-changed-listener.md) — why changed listeners are
  reported when a known endpoint's socket UID changes.
- [0148](../decisions/0148-agent-marks-its-first-snapshot-after-a-start-and-what-its-listener-list-holds.md) — why `listener_check` gains
  `start` and a snapshot says what its listener list holds (`inventory`, `tables`).
- [0151](../decisions/0151-a-refused-mariadb-login-is-recorded-as-not-answering-with-its-error-code.md) — why a refused login is
  `not_answering` with the optional `error_code` instead of a gap or a new availability.
- [0120](../decisions/0120-oom-kill-triggers-extra-process-and-connection-snapshots.md) — why a kill carries its own identity and a snapshot
  names the kills it was taken for.
- [0158](../decisions/0158-backup-runs-as-a-record-kind-of-their-own-recognized-by-process-rules.md) — why a backup run is a kind of its
  own.

## Not here

- The stream, the header, the envelope of a record, the common rules, limits, versioning, batch validation and the accepted forms:
  [Wire format](wire-format.md).
- How the agent's collectors decide what to record: the agent areas linked at each kind.
- How records are stored: [Storage](storage.md), [Storage writing](storage-writing.md).

## Implementation

Agent (Go): the record types and their validation in `internal/model` (for example `validateInventory`; a socket UID is `UID *uint32`).
Backend (C#): `Vandox.Core.Model` (for example `ConnectionSnapshot.ValidateInventory`; a socket UID is `uint?`). Both sides are pinned by
the fixtures listed in [Wire format](wire-format.md), *Implementation*.
