# Wire format

## Scope

The format of the batches the agent sends to the backend, and the shared record model behind it: header, record kinds and their
fields, limits, versioning, batch validation and the duties of the consumer. The rules hold for both implementations, whatever their
language; names such as `MaxLineBytes` or `ErrLimitExceeded` are the identifiers of the Go agent and the C# decoder for the same
rules (see *Implementation*). A golden batch with one record of every kind, `testdata/wire/all-kinds.jsonl`, is written by the
Go encoder and decoded by the C# contract test, so the two sides cannot drift apart unnoticed. How the backend stores the records
is in [Storage](storage.md).

## Stream layout

A batch is a gzip stream (RFC 1952) whose content is JSON Lines: UTF-8 text, one JSON object per line, lines
separated by `\n` (a trailing `\r` is tolerated). Line 1 is the **header**, every further line is one
**record**. A batch holds at least one record. The encoder writes a single gzip member; the decoder also
reads concatenated members as one stream. The encoder does not write a receive time or an origin: both are
added by the backend and cannot be supplied by the agent.

## Header

```json
{"format_major":1,"format_minor":0,"agent_id":"web-1","boot_id":"0b6f9b0c-2d1e-4c43-9a4e-7f1b2c3d4e5f","clock_offset_ns":-120000000,"mode":"live"}
```

| Field | Type | Rule |
| ----- | ---- | ---- |
| `format_major` | integer | must be 1; anything else (also missing, `null`, 0, negative) is rejected as an unsupported version |
| `format_minor` | integer | >= 0; a newer minor is accepted (see *Versioning*) |
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
| `captured_at` | RFC 3339 in UTC, written with `Z`; the time the data describes (a log line: the time stamped in the line, else when it was read; a gap: when it was recorded); the backend stores instants from 1677-09-21 to 2262-04-11 (nanoseconds in an int64, 0063) |
| `data` | the payload object of the kind; required, not `null` |

Records of origin `import` (log importer) and `backend` exist in the model but never on the wire: a batch with
one is invalid, and the decoder sets the origin of every decoded record to `agent`.

### Common rules

- **Name pattern**: `^[A-Za-z0-9][A-Za-z0-9._:/@+-]*$`, 1 to 128 bytes (`MaxNameBytes`). Used for `source`,
  metric names and units, label keys and `gap.collector`.
- **Short text** at most 1024 bytes (`MaxShortTextBytes`), **text** at most 16384 bytes (`MaxTextBytes`); lengths are in
  bytes, no character restriction. Invalid UTF-8 is replaced by U+FFFD when decoded.
- **Lists and maps** hold at most 4096 entries (`MaxItems`); metric labels at most 32 (`MaxLabels`).
- **Times** are UTC (offset 0) and non-zero; optional times are checked only when present.
- **Floats** are finite; percentages and rates are also >= 0.
- **Unknown versus zero**: an optional measurement that may be unknown is omitted, never 0.
- **Addresses** are numeric and carry no zone (see *Producer mapping for connection endpoints*).
- **Field paths** in errors use the JSON names, list indexes in brackets and map keys in brackets rendered
  with `model.QuoteName` / `FieldError.QuoteName` (cut to 128 bytes, quoted with Go escaping, `...` appended when cut), e.g.
  `data.processes[3].pid`, `data.labels["mount"]`.

### `metric`

`name` (name pattern, required), `value` (finite number), `unit` (optional name pattern), `labels` (optional
map of name-pattern key to short text, at most 32). Counters are named with the suffix `_total` (convention,
not validated).

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

### `service_state`

`unit` (required, `^[A-Za-z0-9:_.@\-]+$`, at most 256 bytes), `load_state` (`loaded not-found bad-setting
error masked stub merged`), `active_state` (`active reloading inactive failed activating deactivating
maintenance refreshing`), `sub_state` (optional, `^[a-z0-9-]+$`, at most 64 bytes), `active_enter_at`
(optional), `restarts`.

### `mariadb_status`

`availability` (`up`, `down`, `not_answering`), `ping_latency_ns` (optional, >= 0), `status` (map of
`^[A-Za-z][A-Za-z0-9_]*$` key, at most 128 bytes, to unsigned integer), `variables` (same keys, short text
values), `threads`, `complete` (true only if `status`, `variables` and `threads` hold every entry the
producer read; false for a configured subset or a reduction for size). When `availability` is not `up`,
`status`, `variables` and `threads` must be empty. A thread: `id`, `user`, `host`, `db`, `command`, `state`
(short text), `time_seconds`, `info` (text), `truncated` (the producer shortened `info`).

### `kernel_event`

`type` is `oom_kill` or `boot`; exactly the matching sub-object is set. `oom_kill`: `victim_pid` (> 0),
`victim_command` (required short text), `anon_rss_bytes` (optional), `oom_score_adj` (optional, -1000 to 1000).
`boot`: `boot_id` (required lower-case UUID), `previous_boot_id` (optional UUID), `booted_at` (optional),
`previous_uptime_ns` (optional, >= 0). `message` is text.

### `log_line`

`log` (required short text: `journal`, or the file path: the absolute path for the agent, the path as the import lists
it for imported records, relative to the import root or archive and with its rotation suffix), `host` (optional short
text: the host that wrote the line, empty when unknown), `program` (short text), `pid` (>= 0), `priority` (optional, 0
to 7), `message` (text, may be empty), `truncated` (the producer cut the message to 16384 bytes). `host` is an additive
optional field, so the wire version stays 1.0 ([0084](../decisions/0084-log-line-record-gets-an-optional-host-field.md)).
The Go encoder omits an empty `host`; the C# decoder reads a line without it as an empty host.

### `gap`

`from`, `to` (UTC, `to` after `from`), `cause` (`agent_not_running spool_dropped collector_timeout
sequence_missing no_data unknown`), `collector` (name pattern, required for `collector_timeout`), `first_seq`
and `last_seq` (both omitted or both > 0 with `first_seq <= last_seq`; both required for `spool_dropped` and
`sequence_missing`). The causes `sequence_missing` and `no_data` are set by the backend and are rejected in
agent records.

## Producer mapping for connection endpoints

Endpoints are numeric `netip` addresses and ports. Mapping a source's notation is the producer's job (the
connection collector #33 and the legacy `lsof -ni` parser #20):

- a zone (`fe80::1%eth0`) is stripped, the link-local address itself is kept; a zone means nothing to the
  backend and is rejected with reason `zone not allowed`;
- a wildcard local address (`*` in `lsof`) becomes `0.0.0.0` for IPv4 and `::` for IPv6 sockets;
- a port given as a service name (legacy `lsof -ni` without `-P`) is mapped to its number from a built-in
  table, never by a lookup on the host;
- a socket whose address or port cannot be mapped is left out with a warning and `complete` is set to false;
- an unconnected socket without a remote endpoint (listening TCP, unconnected UDP) goes to `listeners`, never
  to `connections`.

## Limits

| Limit | Default | Applies to |
| ----- | ------- | ---------- |
| `MaxLineBytes` | 1 MiB | one line before `\n` (the header too) |
| `MaxBatchBytes` | 16 MiB | decompressed bytes of the whole stream |
| `MaxRecords` | 20 000 | records per batch |

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

## Versioning (0043)

The header carries an integer major and minor. A decoder reads the major from line 1 before anything else and
rejects any major it does not know with `wire.ErrUnsupportedVersion`, even if the rest of that header has an
incompatible shape. A minor change is additive: new optional fields and new kinds; a decoder ignores unknown
keys at any depth and so accepts a newer minor of its major. An unknown record kind is rejected. Until the
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
| Line is an array, string or number | rejected, `ErrMalformed` |
| Keys in other case (`FORMAT_MAJOR`, `Kind`) | accepted as the field |
| Keys that are Unicode case-fold equivalents (`"Kind"` with U+212A KELVIN SIGN, `"ſeq"` with U+017F) | accepted as the field; the last of several spellings wins |
| Duplicate keys | the last one wins |
| Unknown keys at any depth | ignored |
| `format_major` missing, `null`, 0, negative, not 1 | rejected, `ErrUnsupportedVersion`, line 1 |
| `format_major` as `1.0`, `1e0`, `"1"` | rejected, `ErrMalformed` |
| `kind` unknown, wrong case, empty or missing | rejected, `ErrUnknownKind` |
| `data` missing or `null` | rejected, field `data` |
| `data` not an object; integers out of range; fractions into integer fields; `1e400` | rejected, `ErrMalformed` |
| Time with `Z`, `+00:00`, `-00:00`, fractions | accepted |
| Time with another offset (`+02:00`); the zero time | rejected, field error |
| Time with a space for `T`, lower-case `t`/`z`, not RFC 3339 | rejected, `ErrMalformed` |
| Strings with invalid UTF-8 | accepted, bytes replaced by U+FFFD |
| Escaped control characters in a string | accepted in text fields, rejected where a name pattern applies |
| Address with a zone (`fe80::1%eth0`, `[fe80::1%eth0]:80`, `::ffff:1.2.3.4%eth0`), also with `\n`, `]:` or 5000 bytes in the zone | rejected, field error, reason `zone not allowed` |
| Empty address, hostname, missing port, empty zone (`fe80::1%`), zone on IPv4 | rejected (field error or `ErrMalformed`) |
| IPv4-mapped IPv6 without zone (`::ffff:1.2.3.4`) | accepted |
| IDs and names in other forms (upper-case UUID, braces, `urn:uuid:`, trailing newline) | rejected, field error |

Because keys are matched with Unicode case folding, `zcat batch | jq` can show a key that the decoder reads
under another name (`"Kind"` counts as `kind`).

## Error text and consumer duties

Errors of this package carry no field value: a `*model.FieldError` names the field path and a fixed reason,
and the only agent-supplied text in a path is a map key, quoted and cut by `model.QuoteName`. An unknown kind
appears the same way. **Wrapped parser errors are different**: the `time`, `netip` and `encoding/json` texts
(including the raw map keys in a type error) can carry agent text of up to `MaxLineBytes`. A consumer logs or
displays decode errors only through the display sanitization of the project's area 12.

Duties of the ingest API (#40), which this package cannot take over:

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
{"format_major":1,"format_minor":0,"agent_id":"web-1","boot_id":"0b6f9b0c-2d1e-4c43-9a4e-7f1b2c3d4e5f","mode":"live"}
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
- [0075](../decisions/0075-wire-contract-pinned-by-golden-fixtures.md) — why golden fixtures pin the contract between the two languages.
- [0076](../decisions/0076-strict-gzip-validation-in-the-backend.md) — why the backend decodes gzip strictly.

## Not here

- How records are stored, deduplicated and searched: [Storage](storage.md).
- How the agent spools, batches and sends: the agent area.
- The ingest endpoint and its responses: the ingest and backend host area.

## Implementation

Agent (Go): `internal/model` (record types and validation) and `internal/wire` (header, encoder, streaming decoder; golden test
`internal/wire/golden_test.go`, `VANDOX_UPDATE_GOLDEN=1` rewrites the fixture). Backend (C#): `Vandox.Core.Model` and `Vandox.Core.Wire`
(`BatchDecoder`, `WireLimits`), `Vandox.Core.IO` (`StrictGzip`), contract test `WireContractTests`.

