# Wire format

## Scope

The format of the batches the agent sends to the backend, and the shared record model behind it: header, record kinds and their
fields, limits, versioning, batch validation and the duties of the consumer. The rules hold for both implementations, whatever their
language; names such as `MaxLineBytes` or `ErrLimitExceeded` are the identifiers of the Go agent and the C# decoder for the same
rules (see *Implementation*). A golden batch with one record of every kind, `testdata/wire/all-kinds.jsonl`, and a second one, `testdata/wire/replaced-text.jsonl`, with texts at their limits that held
invalid UTF-8 before encoding, are written by the Go encoder and decoded by the C# contract test, so the two sides cannot drift apart unnoticed; a shared fixture of decoder cases,
`testdata/wire/decoder-cases.json`, is run by both decoders and pins the JSON-level rules below. How the backend stores the records
is in [Storage](storage.md).

## Stream layout

A batch is a gzip stream (RFC 1952) whose content is JSON Lines: UTF-8 text, one JSON object per line, lines
separated by `\n` (a trailing `\r` is tolerated). Line 1 is the **header**, every further line is one
**record**. A batch holds at least one record. The encoder writes a single gzip member; the decoder also
reads concatenated members as one stream. The encoder does not write a receive time or an origin: both are
added by the backend and cannot be supplied by the agent.

## Header

```json
{"format_major":1,"format_minor":1,"agent_id":"web-1","boot_id":"0b6f9b0c-2d1e-4c43-9a4e-7f1b2c3d4e5f","clock_offset_ns":-120000000,"mode":"live"}
```

| Field | Type | Rule |
| ----- | ---- | ---- |
| `format_major` | integer | must be 1; a missing key, `null` or another integer from -2^63 to 2^63 - 1 is rejected as an unsupported version, any other JSON value as malformed |
| `format_minor` | integer | 0 to 2^63 - 1; a newer minor is accepted (see *Versioning*) |
| `agent_id` | string | `^[A-Za-z0-9][A-Za-z0-9._-]*$`, 1 to 64 bytes |
| `boot_id` | string | lower-case UUID, `^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$` |
| `clock_offset_ns` | integer, optional | agent clock minus the reference time of its time synchronization, in nanoseconds; positive means the agent is ahead; omitted means unknown |
| `mode` | string | `live` or `backfill`; a hint only, the backend classifies records itself (0022) |

### Capture context (0046)

`boot_id` is the boot during which every record of the batch was captured, and `clock_offset_ns` the offset
estimate valid for the capture of every record (corrected time = `captured_at - clock_offset_ns`). Both
describe the capture, not the sending. Producer contract (agent spool and sender, #38/#39; the decoder cannot
check it): boot ID and offset estimate are spooled with every record, and a batch holds only records with the
same boot ID and the same estimate (or all without one).

### Batch identity (0045)

There is no batch ID. A batch is identified by its `agent_id` and the `seq` values of its records, which come
from one persistent per-agent counter that is never reused. The backend stores each (agent ID, `seq`) once, so
a resent batch is idempotent. An agent that loses its counter (reinstall, wiped spool) must keep the counter
persistent or use a new agent ID (#38, #40).

## Records

```json
{"kind":"metric","source":"proc.meminfo","seq":41,"captured_at":"2026-03-01T12:00:00Z","data":{"name":"mem_free_bytes","value":1024}}
```

| Field | Rule |
| ----- | ---- |
| `kind` | one of the kinds below, exact lower-case match; an unknown kind rejects the batch |
| `source` | name pattern (collector or parser, e.g. `proc.meminfo`, `journal`) |
| `seq` | integer > 0, strictly increasing within the batch (gaps allowed; the backend detects them, #41); the backend stores at most 2^63 - 1 ([0063](../decisions/0063-storage-schema-records-table-typed-metric-and-log-tables-json-payloads.md)) |
| `captured_at` | RFC 3339 in UTC, written with `Z`; the time the data describes (a log line: the time stamped in the line, else when it was read; a gap: when it was recorded, which its `to` may follow, see *`gap`*); the backend stores instants from 1677-09-21 to 2262-04-11 (nanoseconds in an int64, 0063) |
| `data` | the payload object of the kind; required, not `null` |

Records of origin `import` (log importer) and `backend` exist in the model but never on the wire: a batch with
one is invalid, and the decoder sets the origin of every decoded record to `agent`.

### Common rules

- **Keys**: every key of every object, at any depth and in maps and unknown keys too, is ASCII after unescaping, and no two keys of one
  object are equal ignoring ASCII case; a line that breaks this, or nests objects and arrays deeper than 64 (`MaxDepth`; the line's
  outermost value counts as 1), is `ErrMalformed`. These line rules are checked before anything else is read from the line. A key then
  matches a field when the two are equal ignoring ASCII case. Map keys (metric `labels`, MariaDB `status` and `variables`) that are equal
  ignoring ASCII case are refused by the model's validation, so a producer never writes them. See
  [0090](../decisions/0090-wire-keys-matched-ignoring-ascii-case-duplicates-rejected-header-shape-malformed.md).
- **Text**: every line is valid UTF-8 (no byte that does not start a well-formed sequence: no stray continuation byte, overlong form,
  UTF-8-encoded surrogate, code point above U+10FFFF or sequence cut short), and every `\u` escape of a surrogate is a high half
  (`\ud800` to `\udbff`) directly followed by an escaped low half (`\udc00` to `\udfff`), in any hex case. A line that breaks this,
  anywhere, also in the value of an unknown key and in a header of any major, is `ErrMalformed` (reason `line not valid UTF-8` or
  `unpaired surrogate escape`). It is a line rule, checked after *Keys* and nesting, so a key that breaks it is reported as `key not
  ASCII`, and only on a line that is one complete JSON value: a line cut short or with content after its value is refused for its
  syntax, whatever bytes it holds. Like the key rules it does not depend on the major (see *Versioning*). Neither decoder replaces text. The encoder writes valid UTF-8 only: an invalid byte in a Go string is replaced by U+FFFD on
  encode, and no surrogate is ever escaped. See [0090](../decisions/0090-wire-keys-matched-ignoring-ascii-case-duplicates-rejected-header-shape-malformed.md).
- **Name pattern**: `^[A-Za-z0-9][A-Za-z0-9._:/@+-]*$`, 1 to 128 bytes (`MaxNameBytes`). Used for `source`,
  metric names and units, label keys, `gap.collector` and `log_line.event`.
- **Short text** at most 1024 bytes (`MaxShortTextBytes`), **text** at most 16384 bytes (`MaxTextBytes`); lengths are in
  UTF-8 bytes of the decoded string, no character restriction beyond *Text*. The Go model measures a record before encoding the same
  way: a byte that does not start a valid UTF-8 sequence counts as the 3 bytes of its U+FFFD (a valid U+FFFD counts its own 3), for
  names too, so a record `wire.CheckRecord` accepts never exceeds a limit at either decoder. See
  [0114](../decisions/0114-model-text-limits-measured-after-the-encoders-replacement.md).
- **Lists and maps** hold at most 4096 entries (`MaxItems`); metric labels at most 32 (`MaxLabels`).
- **Times** are UTC (offset 0) and non-zero; optional times are checked only when present.
- **Floats** are finite; percentages and rates are also >= 0.
- **Unknown versus zero**: an optional measurement that may be unknown is omitted, never 0.
- **Addresses** are numeric and carry no zone (see *Producer mapping for connection endpoints*).
- **Field paths** in errors use the JSON names, list indexes in brackets and map keys in brackets rendered
  with `model.QuoteName` / `FieldError.QuoteName` (cut to 128 bytes, quoted with Go escaping, `...` appended when cut), e.g.
  `data.processes[3].pid`, `data.labels["mount"]`.
- **Null**: a JSON `null` reads as if the key were absent (a key is never given twice, see *Keys*), in the header, the record line and the payload at any
  depth. A `null` list element reads as an element with every field at its zero value, a `null` map value as the zero value of the map's
  value type (`""` for `labels` and `variables`, `0` for `status`), and a record line that is `null` as an object without keys. The record's
  validation then decides, so a missing required field is a field error, never `ErrMalformed`. The Go encoder never writes `null`.
  See [0089](../decisions/0089-json-null-reads-as-an-absent-key-in-both-decoders.md).

### `metric`

`name` (name pattern, required), `value` (finite number), `unit` (optional name pattern), `labels` (optional
map of name-pattern key to short text, at most 32, keys unique ignoring ASCII case). Counters are named with the suffix `_total` (convention,
not validated).

#### Metric names

These names, units and labels are used by every producer of these values, the agent's collectors (#31, #32, #33) and the importer's
legacy monitoring log parser (see [log import](log-import.md), *Legacy monitoring log*), so the same value has the same name whoever
produced it. Byte values are exact integers in a double (up to 2^53 exactly), counters carry the suffix `_total`.

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
| `agent_spool_bytes` | `bytes` | — | size of the agent's spool |
| `agent_send_lag_seconds` | `seconds` | — | how far the agent's sending lags behind capture |
| `mariadb_memory_max_bytes` | `bytes` | — | MariaDB's theoretical maximum memory: its global buffers plus `max_connections` times its per-connection buffers |
| `mariadb_memory_max_ram_percent` | `percent` | — | that maximum as a share of `MemTotal` |
| `mariadb_questions_per_second` | `per_second` | — | increase of the status value `Questions` / seconds between two samples |
| `mariadb_innodb_buffer_pool_used_percent` | `percent` | — | used pages of the InnoDB buffer pool as a share of its pages |
| `mariadb_innodb_buffer_pool_read_hit_percent` | `percent` | — | share of the buffer pool's read requests between two samples that did not read from disk |
| `mariadb_exposed_listeners` | — | — | listening TCP sockets on MariaDB's ports whose local address is not loopback |

The `agent_*` metrics are the agent's self-metrics ([Agent](agent.md), *Self-metrics*) and carry the source `agent`, as do the gaps
the agent's scheduler records. The agent's system metrics (`load*`, `cpu_percent`, `mem_*`, `swap_*`, `meminfo*`, `pressure_*`,
`vmstat_*`, `disk_*`, `filesystem_*`, `clock_*`) carry the source `system` ([Agent](agent.md), *System metrics*). The `mariadb_*` metrics
carry the source `mariadb`, like the `mariadb_status` record they are written with ([Agent](agent.md), *MariaDB*, where their formulas
and the cases that leave them out are).

### `process_snapshot`

`complete` (true only if `processes` and `programs` hold every process and program the producer saw; false when
the source shows only part of them or entries were left out for size), `total` (optional, processes on the
system), `processes`, `programs`; at least one process or program. A process: `pid` (> 0, unique in the
snapshot), `ppid` (>= 0), `user` (short text), `command` (required short text), `cmdline` (text), `truncated`
(command or cmdline cut by the source), `state` (empty or one ASCII letter), `started_at` (optional),
`cpu_percent`, `rss_bytes`, `pss_bytes`, `swap_bytes` (optional), `oom_score_adj` (optional, -1000 to 1000).
A program: `program` (required short text, unique), `count` (>= 1), `cpu_percent`, `rss_bytes`, `pss_bytes`
(optional), `rss_overcounted` (the RSS sum counts shared pages once per process).

### `connection_snapshot`

`complete` (true only if no entry the producer saw was left out of any list, in particular `connections` holds
every socket; false after a reduction for size) and the lists `states` (`proto`, `state`, `count`),
`processes` (`pid` > 0, `command`, `count`), `remotes` (`addr`, `count`), `listeners` (`proto`, `local`, `pid`,
`command`) and `connections` (`proto`, `local`, `remote`, `state`, `pid`, `command`). An empty snapshot is
valid. `proto` is `tcp`, `tcp6`, `udp` or `udp6`; `state` is required for `tcp`/`tcp6` and optional for
`udp`/`udp6`, from `ESTABLISHED SYN_SENT SYN_RECV FIN_WAIT1 FIN_WAIT2 TIME_WAIT CLOSE CLOSE_WAIT LAST_ACK
LISTEN CLOSING NEW_SYN_RECV`; `count` >= 1; `pid` >= 0.

Since format 1.1 seven optional fields say what kind of snapshot it is, what its producer compared and what its own list holds (all omitted
when false, zero or absent):

- `aggregates_only` (true): a **regular** snapshot of the connection collector: the aggregates (`states`, `processes`, `remotes`,
  `listeners`) without `connections`. Requires `complete` false and no `connections`. Its `remotes` are the top 25 by count over the
  addresses the producer counted (at most 4,096; a heavy address first seen after the cap is missing).
- `aggregates_incomplete` (true): `states`, `listeners` or `processes` may not cover every socket the producer saw (a list cut, a table
  line skipped, sockets without an owner beyond a cap, owners unknown). Requires `complete` false.
- `listener_check` (`all`, `privileged`, `none`, `start`): the comparison of the listener inventory with the last successful run's that the producer
  made for this snapshot: the whole inventory; only the listeners on ports 1 to 1023, because an inventory was cut at 4,096; none; or `start`,
  the first run after the producer started, which has no earlier inventory (nothing is compared, like `none`; record 0116).
- `new_listeners` (listener entries, at most 4,096): the listeners the producer found new in the run, with the owner's `pid` and `command`
  when known. Requires `listener_check` `all` or `privileged`.
- `new_listeners_omitted` (>= 1): how many more new listeners were found than `new_listeners` holds. Requires `listener_check` `all` or
  `privileged`.
- `inventory` (`all`, `privileged`, `cut`, `skipped`): how much of the run's listener inventory this snapshot's `listeners` list holds: all of
  it; every listener on ports 1 to 1023 but not necessarily the rest; fewer than the ports 1 to 1023 hold (a cut inside the privileged part); or
  nothing reliable because a table line was skipped. A reader that compares two snapshots needs it, because `aggregates_incomplete` has other
  causes too.
- `tables` (protocols, distinct, at most four): the tables the run read, in table order. Two snapshots read from different tables are not
  comparable.

The absence of all seven reads as a **full** snapshot with complete aggregates and no listener check, which is what an imported legacy snapshot
and a 1.0 agent's snapshot are. The rules are checked in Go and C#; their field errors are `data.complete`: `must be false for an
aggregates-only snapshot` and `must be false when aggregates are incomplete`; `data.connections`: `must be empty in an aggregates-only
snapshot`; `data.listener_check`: one of `all`, `privileged`, `none`, `start`, and `must be all or privileged when new listeners are reported`;
`data.inventory`: one of `all`, `privileged`, `cut`, `skipped`; `data.tables[<i>]`: one of `tcp`, `tcp6`, `udp`, `udp6`, and `duplicate protocol`.
A JSON `null` of `listener_check` or `inventory` reads as absent (0089), and a `null` element of `tables` reads as an empty protocol that fails
the one-of rule; the Go encoder never writes an empty `listener_check`, `inventory` or `tables`.

### `service_state`

`unit` (required, `^[A-Za-z0-9:_.@\-]+$`, at most 256 bytes), `load_state` (`loaded not-found bad-setting
error masked stub merged`), `active_state` (`active reloading inactive failed activating deactivating
maintenance refreshing`), `sub_state` (optional, `^[a-z0-9-]+$`, at most 64 bytes), `active_enter_at`
(optional), `restarts`.

Producer: the agent's `systemd` collector ([Agent](agent.md), *Services*) writes it with source `systemd` and the capture time of the
run's start, one record per tracked unit with `unit` the unit's primary name (aliases resolved). A record is written for the first run
after an agent start, for the first run after a lost sample, when `load_state`, `active_state`, `sub_state`, `active_enter_at` or
`restarts` differs from the unit's last record, and again once the last record is an hour old. `active_enter_at` is left out when
systemd has none (the unit was never active) or the time cannot be stored; `restarts` is systemd's `NRestarts`, 0 for a unit that is
not found or masked. The collector checks each record against these rules itself and leaves out a unit whose state they refuse.

### `mariadb_status`

`availability` (`up`, `down`, `not_answering`), `ping_latency_ns` (optional, >= 0), `error_code` (optional, 1 to 65535, only with
`not_answering`), `status` (map of
`^[A-Za-z][A-Za-z0-9_]*$` key, at most 128 bytes, unique ignoring ASCII case, to unsigned integer), `variables` (same keys, short text
values), `threads`, `complete` (true only if `status`, `variables` and `threads` hold every entry the
producer read; false for a configured subset or a reduction for size). When `availability` is not `up`,
`status`, `variables` and `threads` must be empty. A thread: `id`, `user`, `host`, `db`, `command`, `state`
(short text), `time_seconds`, `info` (text), `truncated` (the producer shortened `info`).

`error_code` is MariaDB's error number when the server refused the producer with an error packet in place of its greeting or as the
answer to the login (for example 1040 too many connections, 1698 or 1045 access denied): the server answered, but would not serve. It
was added in format 1.1 (record 0118); a decoder that does not know it ignores it and reads a true `not_answering`. Both decoders refuse
it with the field errors `data.error_code`: `must be 1 to 65535` (the value 0) and `only with not_answering`; a value outside 0 to 65535
is `ErrMalformed` like every integer out of its field's range (*Accepted forms*), and `null` reads as absent (0089).

Producer: the agent's `mariadb` collector ([Agent](agent.md), *MariaDB*) writes one record per run with source `mariadb` and the capture
time of the run's start: `down` when nothing accepts on the socket, `not_answering` (with `error_code` for a refusal) when the server
accepted but gave no usable answer in time, and `up` with `ping_latency_ns`, the selected `status` values and `variables`, and `threads`
only when its thread snapshot is due. `complete` is always false, because the record holds a fixed selection; `info` is cut to 16,384
bytes and kept for at most 64 of at most 4,096 threads, and the producer shortens the record by an upper-bound estimate before it is
encoded (*Producer size contract*).

### `kernel_event`

`type` is `oom_kill` or `boot`; exactly the matching sub-object is set. `oom_kill`: `victim_pid` (> 0),
`victim_command` (required short text), `anon_rss_bytes` (optional), `oom_score_adj` (optional, -1000 to 1000).
`boot`: `boot_id` (required lower-case UUID), `previous_boot_id` (optional UUID), `booted_at` (optional),
`previous_uptime_ns` (optional, >= 0). `message` is text.

### `log_line`

`log` (required short text: `journal`, or the file path: the absolute path for the agent, the path as the import lists
it for imported records, relative to the import root or archive and with its rotation suffix), `host` (optional short
text: the host that wrote the line, empty when unknown), `program` (short text), `pid` (>= 0), `priority` (optional, 0
to 7), `message` (text, may be empty), `truncated` (the producer cut the message to 16384 bytes), `event` (optional name,
see *Name pattern*: the event a producer recognized in the line, such as `mariadb.start`; empty for none). `host` and `event` are additive optional fields, so the wire version stayed 1.0
([0084](../decisions/0084-log-line-record-gets-an-optional-host-field.md),
[0088](../decisions/0088-mariadb-error-log-entries-by-content-and-lifecycle-events-in-log-line.md)). The Go encoder omits an
empty `host` and an empty `event`; the C# decoder reads a line without them as empty.

### `gap`

`from`, `to` (UTC, `to` after `from`), `cause` (`agent_not_running spool_dropped collector_timeout
sequence_missing no_data unknown`), `collector` (name pattern, required for `collector_timeout`), `first_seq`
and `last_seq` (both omitted or both > 0 with `first_seq <= last_seq`; both required for `spool_dropped` and
`sequence_missing`). The causes `sequence_missing` and `no_data` are set by the backend and are rejected in
agent records. `from` and `to` are not bound to the record's `captured_at`: a gap the agent records for a run it abandoned
or skipped covers the sample's slot up to one interval after the due time, so its `to` lies after its
`captured_at` ([Agent](agent.md), *Scheduling*).

## Producer mapping for connection endpoints

Endpoints are numeric `netip` addresses and ports. Mapping a source's notation is the producer's job (the
connection collector #33 and the legacy `lsof -ni` parser #20):

- a zone (`fe80::1%eth0`) is stripped, the link-local address itself is kept; a zone means nothing to the
  backend and is rejected with reason `zone not allowed`;
- a wildcard local address (`*` in `lsof`) becomes `0.0.0.0` for IPv4 and `::` for IPv6 sockets;
- a port given as a service name (legacy `lsof -ni` without `-P`) is mapped to its number from a built-in
  table, never by a lookup on the host;
- a socket whose address or port cannot be mapped is left out and reported (the importer counts it as a skipped line) and `complete` is set to false;
- an unconnected socket without a remote endpoint (listening TCP, unconnected UDP) goes to `listeners`, never
  to `connections`;
- the connection collector reads the kernel's tables `/proc/net/{tcp,tcp6,udp,udp6}`: each address word is a 32-bit number in the host's byte
  order (one word for IPv4, four for IPv6), no zone is written, and an IPv4-mapped address in `tcp6`/`udp6` (`::ffff:a.b.c.d`) is kept as it
  is, not unmapped, so `remotes` can hold one peer twice.

## Limits

| Limit | Default | Applies to |
| ----- | ------- | ---------- |
| `MaxLineBytes` | 1 MiB | one line before `\n` (the header too) |
| `MaxBatchBytes` | 16 MiB | decompressed bytes of the whole stream |
| `MaxRecords` | 20 000 | records per batch |

The nesting depth of a line is fixed at 64 (`MaxDepth`, see *Common rules*) and is not part of the limits. In both decoders the line
rules cost time and memory in proportion to the line's length: the work for one object never depends on the size of another object
([0090](../decisions/0090-wire-keys-matched-ignoring-ascii-case-duplicates-rejected-header-shape-malformed.md)). A decoder keeps the
state of this check from one line to the next and, between lines, holds no key of an earlier line and no key set or list sized for more
than 64 keys; a line that is cut short is `wire.ErrMalformed` and stops the decoder; a decoder (Go `Decoder`,
C# `BatchDecoder`) is used by one caller at a time.
`wire.DefaultLimits()` returns them; `NewDecoder` takes a `Limits` where a zero or negative field means that
field's default. The encoder always uses the defaults. A line over the limit, a stream that decompresses to
more than the limit and one record too many are rejected with `wire.ErrLimitExceeded`.

### Producer size contract (0044)

The bounds on single fields do not add up to a bounded line (500 MariaDB threads of 16 KiB `info` encode to
about 8 MiB). A producer therefore calls `wire.CheckRecord` when it creates a record, before it enters the
spool. It validates the record, requires origin `agent` and returns the bytes the record adds to the
decompressed batch, or a `*wire.RecordSizeError` (`wire.ErrRecordTooLarge`, wrapping `ErrLimitExceeded`). On
that error the producer shortens `cmdline` / `info` and sets `truncated`, then leaves out entries and sets
`complete` to false. `metric`, `service_state`, `kernel_event`, `log_line` and `gap` always fit: at maximum
field sizes with every byte escaped as `\uXXXX` the largest, a metric with 32 labels, stays near 200 KiB.
`EncodeBatch` reports the same error with the record's index and writes nothing.

A record that `wire.CheckRecord` refuses with a validation error (a `*model.FieldError`, for example metric
labels or MariaDB `status` / `variables` keys that are equal ignoring ASCII case, see *Common rules*) is not
spooled. The collector that produced it records a `gap` instead (cause `unknown`, `collector` set to its name,
`from` / `to` covering the sample), so the loss is recorded, never silent
([0028](../decisions/0028-data-gaps-are-always-recorded.md)).

## Versioning (0043)

The header carries an integer major and minor. A decoder checks line 1 against the line rules (*Common rules*, *Keys* and *Text*),
so a header of any major that breaks one is `wire.ErrMalformed`, not `wire.ErrUnsupportedVersion`; a later major can relax a line rule
only through a record that supersedes 0090. It then reads the major before anything else and rejects any major it does not know with `wire.ErrUnsupportedVersion`, even
if the rest of that header has an incompatible shape. A header that is not a JSON object or `null`, or whose major is not
a JSON integer, is `wire.ErrMalformed`: the major is an integer in every version (0043). A minor change is additive: new optional fields and new kinds; a decoder ignores unknown
keys at any depth and so accepts a newer minor of its major. The current version is **1.1**: 1.1 added the optional fields of
`connection_snapshot` (`aggregates_only`, `aggregates_incomplete`, `listener_check` with the value `start`, `new_listeners`,
`new_listeners_omitted`, `inventory`, `tables`; records 0115 and 0116) and the optional field `error_code` of `mariadb_status` (record
0118); the format is unreleased, so the fields extend 1.1 in place;
1.0 was released with v0.1.0. An unknown record kind is rejected. Until the
upgrade rules are settled (#85), upgrade the backend before the agents.

## Batch rules and trust (0044)

A batch is valid only as a whole: a header that validates, 1 to 20 000 records, every record of origin `agent`
with `seq` > 0, `seq` strictly increasing, every record valid. `EncodeBatch` writes nothing for an invalid
batch. The decoder streams, so records are returned before the gzip checksum at the end has been checked: a
corrupted checksum, a truncated stream or trailing non-gzip bytes surface as `wire.ErrMalformed` instead of
`io.EOF`, after the records before the damage. **A consumer commits nothing until `Next` has returned
`io.EOF`.** After any error `Next` returns the same error, after `io.EOF` it returns `io.EOF`. A header with
no record yields `wire.ErrEmptyBatch`. Fields `origin` and `received_at` on a record line are ignored.

## Accepted forms

What the decoder does with unusual input; each row is a test case.

| Input | Result |
| ----- | ------ |
| Not gzip (plain JSON Lines, zstd, empty input) | rejected, `ErrMalformed`, line 1 |
| gzip name/comment/extra fields | accepted; name or comment over 511 bytes rejected, `ErrMalformed` |
| gzip concatenated members | accepted |
| Truncated stream, bad CRC or size, trailing non-gzip bytes | rejected at the end of the stream, `ErrMalformed` |
| More decompressed bytes than `MaxBatchBytes` | rejected, `ErrLimitExceeded` |
| Line longer than `MaxLineBytes`, also an unterminated last line | rejected, `ErrLimitExceeded` |
| `\n` or `\r\n` line ends; last line without `\n` | accepted |
| Lone `\r` separator; empty or whitespace-only line; UTF-8 BOM | rejected, `ErrMalformed` |
| Spaces or tabs around the object | accepted |
| Two values on a line, comments, trailing comma, `NaN`, single quotes | rejected, `ErrMalformed` |
| Line is `null` | header: `ErrUnsupportedVersion`; record: `ErrUnknownKind` |
| Line is an array, string, number, `true` or `false` (header or record) | rejected, `ErrMalformed` |
| Keys in other ASCII case (`FORMAT_MAJOR`, `Kind`, `DATA`, `PID`), also escaped (`"\u004bind"`) | accepted as the field |
| A key with a character outside ASCII after unescaping, at any depth, also a map key or an unknown key (`"Kind"` with U+212A KELVIN SIGN, `"ſeq"` with U+017F, `"agent_ıd"` with U+0131, `"\u212aind"`, a surrogate pair, an unpaired surrogate escape such as `"\ud800"`, invalid UTF-8) | rejected, `ErrMalformed` |
| Two keys in one object equal ignoring ASCII case after unescaping, at any depth, also in a map or an unknown key (`seq` twice, `kind` and `Kind`, `kind` and `"\u006bind"`, `format_major` then `null`, labels `Mount` and `mount`) | rejected, `ErrMalformed`; in the header before the major is read |
| The same key in sibling or nested objects (`[{"a":1},{"a":2}]`, `{"a":{"a":1}}`) | accepted |
| Nesting of objects and arrays deeper than 64, the outermost value counting as 1, also inside an unknown key | rejected, `ErrMalformed`; 64 accepted |
| Unknown keys at any depth | ignored |
| `null` as the value of a field (header, record or payload) | read as absent: accepted where the field is optional (`"host":null`, `"format_minor":null`; `"value":null` reads 0), rejected with the field error of the missing field where it is required (`"log":null` gives `data.log: required`, `"seq":null` gives `seq: must be greater than 0 for origin agent`, `"addr":null` gives `invalid address`) |
| `null` as a list element | an element with every field at its zero value: `"threads":[null]` accepted, `"processes":[null]` gives `data.processes[0].pid: must be greater than 0` |
| `null` as a map value | the zero value of the value type: `"labels":{"a":null}` accepted with an empty value, `"status":{"Uptime":null}` reads 0 |
| `format_major` missing, `null`, or an integer other than 1 (0, `-0`, negative, 2, 3000000000, -2^63, 2^63 - 1) | rejected, `ErrUnsupportedVersion`, line 1 |
| `format_major` as `1.0`, `1e0`, `2.5`, `"1"`, `true`, an object or array, an integer outside -2^63 to 2^63 - 1, `1e400` | rejected, `ErrMalformed`, line 1 |
| `format_minor` 3000000000 or 2^63 - 1 | accepted |
| `format_minor` 2^63 or more | rejected, `ErrMalformed` |
| `kind` unknown, wrong case, empty or missing | rejected, `ErrUnknownKind` |
| `data` missing or `null` | rejected, field `data` |
| `data` not an object; integers out of range; fractions into integer fields; `1e400` | rejected, `ErrMalformed` |
| Time with `Z`, `+00:00`, `-00:00`, fractions | accepted |
| Time with another offset (`+02:00`); the zero time | rejected, field error |
| Time with a space for `T`, lower-case `t`/`z`, not RFC 3339 | rejected, `ErrMalformed` |
| Invalid UTF-8 anywhere in a line (a byte `FF` or `F5`, a stray continuation byte `80`, an overlong form `C0 AF` or `E0 80 80`, an encoded surrogate `ED A0 80`, `F4 90 80 80` above U+10FFFF, a sequence cut short such as `E2 82` before the closing quote), also in `kind`, `agent_id`, a label value, the value of an unknown key or a header of another major | rejected, `ErrMalformed`, reason `line not valid UTF-8` (a line cut short or with content after its value: the syntax error) |
| An unpaired surrogate escape in a string (`\ud800`, `\udc00`, `\ud800\u0041`, `\ud800x`, `\udc00\ud800`, `\ud800\ud800\udc00`, `\uD800`), also in `kind`, `unit`, `agent_id` or the value of an unknown key | rejected, `ErrMalformed`, reason `unpaired surrogate escape` |
| An escaped surrogate pair in any hex case (`\ud83d\ude00`, `\uD83D\uDE00`), escaped `\ufffd` or `\uffff`, an escaped backslash before `ud800` (`\\ud800`), raw multi-byte UTF-8 | accepted |
| Escaped control characters in a string | accepted in text fields, rejected where a name pattern applies |
| Address with a zone (`fe80::1%eth0`, `[fe80::1%eth0]:80`, `::ffff:1.2.3.4%eth0`), also with `\n`, `]:` or 5000 bytes in the zone | rejected, field error, reason `zone not allowed` |
| Empty address, hostname, missing port, empty zone (`fe80::1%`), zone on IPv4 | rejected (field error or `ErrMalformed`) |
| IPv4-mapped IPv6 without zone (`::ffff:1.2.3.4`) | accepted |
| IDs and names in other forms (upper-case UUID, braces, `urn:uuid:`, trailing newline) | rejected, field error |

Because keys are matched ignoring ASCII case, `zcat batch | jq .kind` misses a key spelled `"Kind"`, which the decoder
reads as `kind`; a line never holds both spellings, since they would be duplicates.

## Error text and consumer duties

Errors of this package carry no field value: a `*model.FieldError` names the field path and a fixed reason,
and the only agent-supplied text in a path is a map key, quoted and cut by `model.QuoteName`. An unknown kind
appears the same way. **Wrapped parser errors are different**: the `time`, `netip` and `encoding/json` texts
(including the raw map keys in a type error) can carry agent text of up to `MaxLineBytes`. A consumer logs or
displays decode errors only through the display sanitization of the project's area 12.

Duties of the ingest API (#40, planned), which this package cannot take over. They are written for the Go decoder; the
.NET ingest of the backend has the same duties with its own reader, timeouts and memory peak, which #40 has to work out:

- wrap the request body in `http.MaxBytesReader` on the **compressed** bytes before `NewDecoder` (the decoder
  bounds only the decompressed bytes);
- set the server's read, read-header, write and idle timeouts, so a slow reader cannot block;
- check the header's `agent_id` against the agent the token belongs to;
- deduplicate by (agent ID, `seq`);
- commit only after `Next` returned `io.EOF`;
- run one concurrent decoder per agent and cap the total, sized from the per-decoder peak and the backend
  container memory: a hostile line at the default 1 MiB `MaxLineBytes` can make one decoder allocate about
  270 MiB with a heap peak of about 160-170 MB (roughly 160 x `MaxLineBytes`), from a body of about 1 KB
  compressed, so `http.MaxBytesReader` does not bound it;
- map `ErrUnsupportedVersion`, `ErrLimitExceeded` and the other errors to HTTP responses.

## Example

Decompressed content of a batch of three records (the real stream is gzip-compressed):

```
{"format_major":1,"format_minor":1,"agent_id":"web-1","boot_id":"0b6f9b0c-2d1e-4c43-9a4e-7f1b2c3d4e5f","mode":"live"}
{"kind":"metric","source":"proc.meminfo","seq":41,"captured_at":"2026-03-01T12:00:00Z","data":{"name":"mem_free_bytes","value":1048576,"unit":"bytes"}}
{"kind":"service_state","source":"systemd","seq":42,"captured_at":"2026-03-01T12:00:00Z","data":{"unit":"mariadb.service","load_state":"loaded","active_state":"active","sub_state":"running","restarts":0}}
{"kind":"gap","source":"agent","seq":43,"captured_at":"2026-03-01T12:00:05Z","data":{"from":"2026-03-01T11:58:00Z","to":"2026-03-01T12:00:00Z","cause":"collector_timeout","collector":"mariadb"}}
```

## Related decisions

- [0042](../decisions/0042-wire-format-gzip-json-lines-standard-library.md) — why gzip-compressed JSON Lines on the standard library.
- [0043](../decisions/0043-wire-format-major-minor-versioning.md) — why integer major and minor with the major checked first.
- [0044](../decisions/0044-batch-validated-as-a-whole-agent-records-only.md) — why a batch is valid only as a whole, carries only agent records and is bounded.
- [0045](../decisions/0045-batch-identified-by-agent-id-and-record-sequence-numbers.md) — why per-record sequence numbers and a spool of at least 7 days.
- [0046](../decisions/0046-batch-header-describes-the-capture-context.md) — why the header describes the capture, not the sending.
- [0088](../decisions/0088-mariadb-error-log-entries-by-content-and-lifecycle-events-in-log-line.md) — why `log_line` gets an optional `event`.
- [0075](../decisions/0075-wire-contract-pinned-by-golden-fixtures.md) — why golden fixtures pin the contract between the two languages.
- [0076](../decisions/0076-strict-gzip-validation-in-the-backend.md) — why the backend decodes gzip strictly.
- [0089](../decisions/0089-json-null-reads-as-an-absent-key-in-both-decoders.md) — why a JSON `null` reads as an absent key in both decoders.
- [0090](../decisions/0090-wire-keys-matched-ignoring-ascii-case-duplicates-rejected-header-shape-malformed.md) — why both decoders check
  keys and nesting before binding, match keys ignoring ASCII case and report a misshapen header as malformed.
- [0115](../decisions/0115-connection-collector-proc-net-tables-owners-from-fd-regular-and-full-snapshots-bounded-listener-check.md) — why a
  regular and a full snapshot are one record kind with additive fields, and format 1.1.
- [0116](../decisions/0116-listeners-across-agent-starts-compared-when-stored-and-shown-in-the-forensics-report.md) — why `listener_check` gains
  `start` and a snapshot says what its listener list holds (`inventory`, `tables`).
- [0114](../decisions/0114-model-text-limits-measured-after-the-encoders-replacement.md) — why the Go model measures limits as the encoder writes them, invalid UTF-8 counted as its replacement.
- [0118](../decisions/0118-mariadb-collector-hand-written-client-captured-statements-refusals-as-not-answering-with-error-code.md) — why a
  refused login is `not_answering` with the optional `error_code` instead of a gap or a new availability.

## Not here

- How records are stored, deduplicated and searched: [Storage](storage.md).
- How the agent spools, batches and sends: the agent area.
- The ingest endpoint and its responses: the [Backend host](backend-host.md) area (the ingest API is planned, #40).

## Implementation

Agent (Go): `internal/model` (record types and validation) and `internal/wire` (header, encoder, streaming decoder with the line check
in `decode.go`; golden test `internal/wire/golden_test.go`, `VANDOX_UPDATE_GOLDEN=1` rewrites the fixtures `all-kinds.jsonl` and `replaced-text.jsonl`; the shared decoder cases run in
`internal/wire/decode_test.go`). Backend (C#): `Vandox.Core.Model` and `Vandox.Core.Wire` (`BatchDecoder`, `WireLimits`, `WireKeyCheck` and
`WireTextCheck` for the line check; `NullAsAbsentConverterFactory` with its two converters is the C# side of the null rule), `Vandox.Core.IO` (`StrictGzip`),
contract test `WireContractTests` (both golden batches and shared decoder cases).

