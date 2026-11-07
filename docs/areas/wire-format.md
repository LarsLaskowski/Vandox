# Wire format

## Scope

The format of the batches the agent sends to the backend: the stream layout, the header, the envelope of a record, the rules every
record follows, limits, versioning, batch validation, what a decoder does with unusual input, its error texts and the duties of the
consumer. The rules hold for both implementations, the agent's encoder and decoder and the backend's decoder, whatever their
language; shared fixtures under `testdata/wire/` pin them, so the two sides cannot drift apart unnoticed (*Implementation*). The
payload of each record kind is in [Wire format records](wire-format-records.md); how the backend stores the records is in
[Storage](storage.md).

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
| `mode` | string | `live` or `backfill`; a hint only, logged and never stored or used: the backend classifies records itself (0022, 0130) by lateness and gaps. The agent's sender sets `live` on the stream that sends current records and `backfill` on the throttled stream that sends the spool's older ones ([Agent sending](agent-sending.md)) |

### Capture context (0046)

`boot_id` is the boot during which every record of the batch was captured, and `clock_offset_ns` the offset
estimate valid for the capture of every record (corrected time = `captured_at - clock_offset_ns`). Both
describe the capture, not the sending. Producer contract (agent spool and sender; the decoder cannot
check it): boot ID and offset estimate are spooled with every record, and a batch holds only records with the
same boot ID and the same estimate (or all without one).

### Batch identity (0045)

There is no batch ID. A batch is identified by its `agent_id` and the `seq` values of its records, which come
from one persistent per-agent counter that is never reused. The backend stores each (agent ID, `seq`) once, so
a resent batch is idempotent. The agent's spool issues the numbers ([Agent spool](agent-spool.md)): they
increase strictly and stay below 2^63 - 1. A spool that lost its counter (a reinstall, a wiped spool directory, a meta
file of another agent ID or a damaged one) and a spool after an unclean stop in an earlier boot start at least at the
**clock bound**, the wall clock's microseconds since the Unix epoch plus 1,000,000, so the agent keeps its ID and its
new numbers lie above every number it issued before, unless the clock was set back by more than the time in between
(record 0123, amending 0045). Two consequences for the backend's sequence detection ([Detection, alerts and
reports](detection-and-reports.md), *Live data and backfill*): an agent's first number is large, so numbers below the first number
the backend received from an agent are never reported as missing; and a fresh start or a crash leaves a jump in the numbers, which
is reported as a `sequence_missing` gap like any other gap in them and becomes final after 5 minutes in which no record arrives into it.

## Records

```json
{"kind":"metric","source":"proc.meminfo","seq":41,"captured_at":"2026-03-01T12:00:00Z","data":{"name":"mem_free_bytes","value":1024}}
```

| Field | Rule |
| ----- | ---- |
| `kind` | one of the kinds of [Wire format records](wire-format-records.md), exact lower-case match; an unknown kind rejects the batch |
| `source` | name pattern (collector or parser, e.g. `proc.meminfo`, `journal`) |
| `seq` | integer > 0, strictly increasing within the batch (gaps allowed; the backend detects them, 0130); the backend stores at most 2^63 - 1 ([0063](../decisions/0063-storage-schema-records-table-typed-metric-and-log-tables-json-payloads.md)) |
| `captured_at` | RFC 3339 in UTC, written with `Z`; the time the data describes (a log line: the time stamped in the line, else when it was read; a gap: when it was recorded, which its `to` may follow); the backend stores instants from 1677-09-21 to 2262-04-11 (nanoseconds in a signed 64-bit integer, 0063) |
| `data` | the payload object of the kind; required, not `null` |

Records of origin `import` (log importer) and `backend` exist in the model but never on the wire: a batch with
one is invalid, and the decoder sets the origin of every decoded record to `agent`.

### Common rules

- **Keys**: every key of every object, at any depth and in maps and unknown keys too, is ASCII after unescaping, and no two keys of
  one object are equal ignoring ASCII case; a line that breaks this, or nests objects and arrays deeper than 64 (the line's outermost
  value counts as 1), is malformed. These line rules are checked before anything else is read from the line. A key then matches a
  field when the two are equal ignoring ASCII case. Map keys (metric `labels`, MariaDB `status` and `variables`) that are equal
  ignoring ASCII case are refused by the model's validation, so a producer never writes them. See
  [0090](../decisions/0090-wire-keys-matched-ignoring-ascii-case-duplicates-rejected-header-shape-malformed.md).
- **Text**: every line is valid UTF-8 (no byte that does not start a well-formed sequence: no stray continuation byte, overlong form,
  UTF-8-encoded surrogate, code point above U+10FFFF or sequence cut short), and every `\u` escape of a surrogate is a high half
  (`\ud800` to `\udbff`) directly followed by an escaped low half (`\udc00` to `\udfff`), in any hex case. A line that breaks this,
  anywhere, also in the value of an unknown key and in a header of any major, is malformed (reason `line not valid UTF-8` or
  `unpaired surrogate escape`). It is a line rule, checked after *Keys* and nesting, so a key that breaks it is reported as `key not
  ASCII`, and only on a line that is one complete JSON value: a line cut short or with content after its value is refused for its
  syntax, whatever bytes it holds. Like the key rules it does not depend on the major (see *Versioning*). Neither decoder replaces
  text. The encoder writes valid UTF-8 only: an invalid byte in a text is replaced by U+FFFD on encode, and no surrogate is ever
  escaped.
- **Name pattern**: `^[A-Za-z0-9][A-Za-z0-9._:/@+-]*$`, 1 to 128 bytes. Used for `source`, metric names and units, label keys,
  `gap.collector` and `log_line.event`.
- **Short text** at most 1024 bytes, **text** at most 16384 bytes; lengths are in UTF-8 bytes of the decoded string, no character
  restriction beyond *Text*. A producer measures a record before encoding the same way: a byte that does not start a valid UTF-8
  sequence counts as the 3 bytes of its U+FFFD (a valid U+FFFD counts its own 3), for names too, so a record the producer's record
  check accepts never exceeds a limit at either decoder. See
  [0114](../decisions/0114-model-text-limits-measured-after-the-encoders-replacement.md).
- **Lists and maps** hold at most 4096 entries; metric labels at most 32.
- **Times** are UTC (offset 0) and non-zero; optional times are checked only when present.
- **Floats** are finite; percentages and rates are also >= 0.
- **Unknown versus zero**: an optional measurement that may be unknown is omitted, never 0.
- **Addresses** are numeric and carry no zone ([Wire format records](wire-format-records.md), *Producer mapping for connection
  endpoints*).
- **Field paths** in errors use the JSON names, list indexes in brackets and map keys in brackets, quoted (cut to 128 bytes, quoted
  with Go string escaping, `...` appended when cut), e.g. `data.processes[3].pid`, `data.labels["mount"]`.
- **Null**: a JSON `null` reads as if the key were absent (a key is never given twice, see *Keys*), in the header, the record line and
  the payload at any depth. A `null` list element reads as an element with every field at its zero value, a `null` map value as the
  zero value of the map's value type (`""` for `labels` and `variables`, `0` for `status`), and a record line that is `null` as an
  object without keys. The record's validation then decides, so a missing required field is a field error, never malformed. The
  encoder never writes `null`. See [0089](../decisions/0089-json-null-reads-as-an-absent-key-in-both-decoders.md).

## Errors

A decoder reports each failure as one of these classes; the ingest endpoint maps them to statuses ([Ingest
endpoint](ingest-endpoint.md)):

| Class | Meaning |
| ----- | ------- |
| malformed | not gzip, not JSON Lines, a broken line rule, a value of the wrong JSON type or out of its field's range, a damaged or trailing stream |
| limit exceeded | a line, the decompressed stream or the record count over its limit; also *record too large* (*Producer size contract*) |
| unsupported version | a `format_major` the decoder does not know |
| unknown kind | a `kind` that is not one of the record kinds |
| sequence | `seq` not strictly increasing |
| empty batch | a header without a record |
| field error | a field that breaks its rule: a field path and a fixed reason |

Malformed or hostile input yields one of these errors, never a crash, an unbounded allocation or a hang. A truncated or corrupt
gzip stream is an error.

## Limits

| Limit | Default | Applies to |
| ----- | ------- | ---------- |
| line limit | 1 MiB | one line before `\n` (the header too) |
| batch limit | 16 MiB | decompressed bytes of the whole stream |
| record limit | 20 000 | records per batch |

The nesting depth of a line is fixed at 64 (*Common rules*) and is not part of the limits. In both decoders the line rules cost time
and memory in proportion to the line's length: the work for one object never depends on the size of another object
([0090](../decisions/0090-wire-keys-matched-ignoring-ascii-case-duplicates-rejected-header-shape-malformed.md)). A decoder keeps the
state of this check from one line to the next and, between lines, holds no key of an earlier line and no key set or list sized for
more than 64 keys; a line that is cut short is malformed and stops the decoder; a decoder instance is used by one caller at a time.
A consumer may lower the limits for its decoder; a limit given as zero or negative means its default. The encoder always uses the
defaults. A line over the limit, a stream that decompresses to more than the limit and one record too many are rejected as limit
exceeded.

### Producer size contract (0044)

The bounds on single fields do not add up to a bounded line (500 MariaDB threads of 16 KiB `info` encode to about 8 MiB). A producer
therefore runs the record check when it creates a record, before it enters the spool. The check validates the record, requires
origin `agent` and returns the bytes the record adds to the decompressed batch, or the error *record too large* (a limit exceeded
error). On that error the producer shortens `cmdline` / `info` and sets `truncated`, then leaves out entries and sets `complete` to
false. `metric`, `service_state`, `kernel_event`, `backup_run`, `log_line` and `gap` always fit: at maximum field sizes with every
byte escaped as `\uXXXX` the largest, a metric with 32 labels, stays near 200 KiB. The batch encoder reports the same error with the
record's index and writes nothing.

A record that the record check refuses with a field error (for example metric labels or MariaDB `status` / `variables` keys that are
equal ignoring ASCII case, see *Common rules*) is not spooled. The collector that produced it records a `gap` instead (cause
`unknown`, `collector` set to its name, `from` / `to` covering the sample), so the loss is recorded, never silent
([0028](../decisions/0028-data-gaps-are-always-recorded.md)).

## Versioning (0043)

The header carries an integer major and minor. A decoder checks line 1 against the line rules (*Common rules*, *Keys* and *Text*), so
a header of any major that breaks one is malformed, not an unsupported version; a later major can relax a line rule only through a
record that supersedes 0090. It then reads the major before anything else and rejects any major it does not know as an unsupported
version, even if the rest of that header has an incompatible shape. A header that is not a JSON object or `null`, or whose major is
not a JSON integer, is malformed: the major is an integer in every version (0043). A minor change is additive: new optional fields
and new kinds; a decoder ignores unknown keys at any depth and so accepts a newer minor of its major. The current version is
**1.1**: 1.1 added the optional fields of `connection_snapshot` (`aggregates_only`, `aggregates_incomplete`, `listener_check` with the
value `start`, `new_listeners`, `new_listeners_omitted`, `changed_listeners`, `changed_listeners_omitted`, `inventory`, `tables`;
records 0115 and 0116; and `uid`, `user`, `mixed_uids` of its listener entries and `uid`, `user` of its connection entries, record
0115), the optional field `error_code` of `mariadb_status` (record 0118), the optional fields `boot_id` and `kernel_time_ns` of
`kernel_event`'s `oom_kill`, the optional `trigger` of `process_snapshot` and `connection_snapshot`, and the kind `backup_run` (record
0120); the format is unreleased, so the fields and the kind extend 1.1 in place, and both decoders accept the new kind in the same
change (a decoder without it would refuse the batch); 1.0 was released with v0.1.0. An unknown record kind is rejected. Until the
upgrade rules are settled (#85), upgrade the backend before the agents.

## Batch rules and trust (0044)

A batch is valid only as a whole: a header that validates, 1 to 20 000 records, every record of origin `agent` with `seq` > 0, `seq`
strictly increasing, every record valid. The encoder writes nothing for an invalid batch. The decoder streams, so records are
returned before the gzip checksum at the end has been checked: a corrupted checksum, a truncated stream or trailing non-gzip bytes
surface as malformed instead of the end of the stream, after the records before the damage. **A consumer commits nothing until the
decoder has reported the end of the stream.** After any error the decoder reports the same error on every further read, after the
end it reports the end again. A header with no record is an empty batch. Fields `origin` and `received_at` on a record line are
ignored.

## Accepted forms

What the decoder does with unusual input; each row is a test case.

| Input | Result |
| ----- | ------ |
| Not gzip (plain JSON Lines, zstd, empty input) | rejected, malformed, line 1 |
| gzip name/comment/extra fields | accepted; name or comment over 511 bytes rejected, malformed (shared cases `G7`-`G11`) |
| gzip header checksum (`FHCRC`) | accepted when it equals the low 16 bits of the header's CRC-32, otherwise rejected, malformed (`G12`, `G13`) |
| gzip concatenated members | accepted (`G5`) |
| Truncated stream, bad CRC or size | rejected at the end of the stream, malformed |
| Any byte after the last complete member: garbage, one zero byte, zero padding, the start of a second header (`1f 8b` alone), also after two members | rejected at the end of the stream, malformed, after the records before it (`G1`-`G4`, `G6`) |
| More decompressed bytes than the batch limit | rejected, limit exceeded |
| Line longer than the line limit, also an unterminated last line | rejected, limit exceeded |
| `\n` or `\r\n` line ends; last line without `\n` | accepted |
| Lone `\r` separator; empty or whitespace-only line; UTF-8 BOM | rejected, malformed |
| Spaces or tabs around the object | accepted |
| Two values on a line, comments, trailing comma, `NaN`, single quotes | rejected, malformed |
| Line is `null` | header: unsupported version; record: unknown kind |
| Line is an array, string, number, `true` or `false` (header or record) | rejected, malformed |
| Keys in other ASCII case (`FORMAT_MAJOR`, `Kind`, `DATA`, `PID`), also escaped (`"\u004bind"`) | accepted as the field |
| A key with a character outside ASCII after unescaping, at any depth, also a map key or an unknown key (`"Kind"` with U+212A KELVIN SIGN, `"ſeq"` with U+017F, `"agent_ıd"` with U+0131, `"\u212aind"`, a surrogate pair, an unpaired surrogate escape such as `"\ud800"`, invalid UTF-8) | rejected, malformed |
| Two keys in one object equal ignoring ASCII case after unescaping, at any depth, also in a map or an unknown key (`seq` twice, `kind` and `Kind`, `kind` and `"\u006bind"`, `format_major` then `null`, labels `Mount` and `mount`) | rejected, malformed; in the header before the major is read |
| The same key in sibling or nested objects (`[{"a":1},{"a":2}]`, `{"a":{"a":1}}`) | accepted |
| Nesting of objects and arrays deeper than 64, the outermost value counting as 1, also inside an unknown key | rejected, malformed; 64 accepted |
| Unknown keys at any depth | ignored |
| `null` as the value of a field (header, record or payload) | read as absent: accepted where the field is optional (`"host":null`, `"format_minor":null`; `"value":null` reads 0), rejected with the field error of the missing field where it is required (`"log":null` gives `data.log: required`, `"seq":null` gives `seq: must be greater than 0 for origin agent`, `"addr":null` gives `invalid address`) |
| `null` as a list element | an element with every field at its zero value: `"threads":[null]` accepted, `"processes":[null]` gives `data.processes[0].pid: must be greater than 0` |
| `null` as a map value | the zero value of the value type: `"labels":{"a":null}` accepted with an empty value, `"status":{"Uptime":null}` reads 0 |
| `format_major` missing, `null`, or an integer other than 1 (0, `-0`, negative, 2, 3000000000, -2^63, 2^63 - 1) | rejected, unsupported version, line 1 |
| `format_major` as `1.0`, `1e0`, `2.5`, `"1"`, `true`, an object or array, an integer outside -2^63 to 2^63 - 1, `1e400` | rejected, malformed, line 1 |
| `format_minor` 3000000000 or 2^63 - 1 | accepted |
| `format_minor` 2^63 or more | rejected, malformed |
| `kind` unknown, wrong case, empty or missing | rejected, unknown kind |
| `data` missing or `null` | rejected, field `data` |
| `data` not an object; integers out of range; fractions into integer fields; `1e400` | rejected, malformed |
| Time with `Z`, `+00:00`, `-00:00`, fractions | accepted |
| Time with another offset (`+02:00`); the zero time | rejected, field error |
| Time with a space for `T`, lower-case `t`/`z`, not RFC 3339 | rejected, malformed |
| Invalid UTF-8 anywhere in a line (a byte `FF` or `F5`, a stray continuation byte `80`, an overlong form `C0 AF` or `E0 80 80`, an encoded surrogate `ED A0 80`, `F4 90 80 80` above U+10FFFF, a sequence cut short such as `E2 82` before the closing quote), also in `kind`, `agent_id`, a label value, the value of an unknown key or a header of another major | rejected, malformed, reason `line not valid UTF-8` (a line cut short or with content after its value: the syntax error) |
| An unpaired surrogate escape in a string (`\ud800`, `\udc00`, `\ud800\u0041`, `\ud800x`, `\udc00\ud800`, `\ud800\ud800\udc00`, `\uD800`), also in `kind`, `unit`, `agent_id` or the value of an unknown key | rejected, malformed, reason `unpaired surrogate escape` |
| An escaped surrogate pair in any hex case (`\ud83d\ude00`, `\uD83D\uDE00`), escaped `\ufffd` or `\uffff`, an escaped backslash before `ud800` (`\\ud800`), raw multi-byte UTF-8 | accepted |
| Escaped control characters in a string | accepted in text fields, rejected where a name pattern applies |
| Address with a zone (`fe80::1%eth0`, `[fe80::1%eth0]:80`, `::ffff:1.2.3.4%eth0`), also with `\n`, `]:` or 5000 bytes in the zone | rejected, field error, reason `zone not allowed` |
| Empty address, hostname, missing port, empty zone (`fe80::1%`), zone on IPv4 | rejected (field error or malformed) |
| IPv4-mapped IPv6 without zone (`::ffff:1.2.3.4`) | accepted |
| IDs and names in other forms (upper-case UUID, braces, `urn:uuid:`, trailing newline) | rejected, field error |

Because keys are matched ignoring ASCII case, `zcat batch | jq .kind` misses a key spelled `"Kind"`, which the decoder reads as
`kind`; a line never holds both spellings, since they would be duplicates.

## Error text and consumer duties

A decode error carries no field value: a field error names the field path and a fixed reason, and the only agent-supplied text in a
path is a map key, quoted and cut as in *Field paths*. An unknown kind appears the same way. **Wrapped parser errors are different**:
the errors of the time, address and JSON parsers (including the raw map keys in a type error) can carry agent text of up to the line
limit. A consumer therefore never logs or displays a decode error's text: it logs a fixed reason, the line number and the field path
with every quoted map key reduced to `[]`, as the ingest endpoint does.

Duties of a consumer that this format cannot take over; the backend's ingest endpoint meets each of them ([Ingest
endpoint](ingest-endpoint.md)):

- **Bound the compressed bytes before decoding**: the decoder bounds only the decompressed bytes.
- **Bound the time** of waiting, reading and decoding one request, so a slow sender cannot hold the consumer's resources.
- **Check the header's `agent_id`** against the agent the credential belongs to, before any record is read.
- **Deduplicate** by (agent ID, `seq`) ([Storage writing](storage-writing.md)).
- **Detect gaps and classify** each record itself, ignoring the header's `mode` and `clock_offset_ns`: the backend's own gap causes
  are `sequence_missing` and `no_data`, and a `gap` record that names numbers below its own makes them final ([Storage
  writing](storage-writing.md), *Agent timeline*).
- **Commit only after the end** of the stream, and nothing when any read fails.
- **Lower the limits and cap the concurrent decoders** from the measured peak memory.
- **Map the error classes** to answers without passing an error's text on.

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
- [0123](../decisions/0123-agent-spool-segment-files-clock-bound-sequence-numbers-range-acknowledgements.md) — why a fresh spool takes
  its sequence numbers from the clock bound instead of a new agent ID (amends 0045).
- [0075](../decisions/0075-wire-contract-pinned-by-golden-fixtures.md) — why golden fixtures pin the contract between the two languages.
- [0076](../decisions/0076-strict-gzip-validation-in-the-backend.md) — why the backend decodes gzip strictly.
- [0128](../decisions/0128-csharp-decoder-reads-the-gzip-framing-itself-to-match-the-go-decoder.md) — why the C# decoder reads the gzip
  framing itself, so trailing bytes and the header rules match the Go decoder (amends 0076).
- [0089](../decisions/0089-json-null-reads-as-an-absent-key-in-both-decoders.md) — why a JSON `null` reads as an absent key in both decoders.
- [0090](../decisions/0090-wire-keys-matched-ignoring-ascii-case-duplicates-rejected-header-shape-malformed.md) — why both decoders check
  keys and nesting before binding, match keys ignoring ASCII case and report a misshapen header as malformed.
- [0114](../decisions/0114-model-text-limits-measured-after-the-encoders-replacement.md) — why the Go model measures limits as the encoder writes them, invalid UTF-8 counted as its replacement.
- [0115](../decisions/0115-connection-collector-proc-net-tables-owners-from-fd-regular-and-full-snapshots-bounded-listener-check.md),
  [0116](../decisions/0116-listeners-across-agent-starts-compared-when-stored-and-shown-in-the-forensics-report.md),
  [0118](../decisions/0118-mariadb-collector-hand-written-client-captured-statements-refusals-as-not-answering-with-error-code.md) and
  [0120](../decisions/0120-oom-kill-events-triggered-snapshots-boots-from-saved-state-and-backup-runs.md) — the additions that make
  format 1.1 (details in [Wire format records](wire-format-records.md)).

## Not here

- The payload of every record kind, the metric names and the producer mapping of connection endpoints: [Wire format
  records](wire-format-records.md).
- How records are stored, deduplicated and searched: [Storage](storage.md), [Storage writing](storage-writing.md).
- How the agent spools, batches and sends: [Agent spool](agent-spool.md), [Agent sending](agent-sending.md).
- The ingest endpoint, its statuses, limits, log lines and the measured memory of one request: [Ingest endpoint](ingest-endpoint.md).

## Implementation

Agent (Go): `internal/model` (record types and validation; field paths quoted by `model.QuoteName`, field errors `*model.FieldError`)
and `internal/wire` (header, encoder `EncodeBatch`, record check `wire.CheckRecord` with `*wire.RecordSizeError`, streaming decoder
`NewDecoder` / `Decoder.Next` with the line check in `decode.go`, limits `wire.Limits` and `wire.DefaultLimits()` with the fields
`MaxLineBytes`, `MaxBatchBytes`, `MaxRecords`; the fixed bounds `MaxDepth`, `MaxNameBytes`, `MaxShortTextBytes`, `MaxTextBytes`,
`MaxItems`, `MaxLabels`; the error classes `ErrMalformed`, `ErrLimitExceeded` with `ErrRecordTooLarge`, `ErrUnsupportedVersion`,
`ErrUnknownKind`, `ErrSequence`, `ErrEmptyBatch`; the end of the stream is `io.EOF`). Backend (C#): `Vandox.Core.Model` (field errors
`FieldError`, `FieldError.QuoteName`) and `Vandox.Core.Wire` (`BatchDecoder` with `NextAsync` and `OpenAsync`, `WireLimits`,
`WireErrorKind` with the same classes, `WireKeyCheck` and `WireTextCheck` for the line check; `NullAsAbsentConverterFactory` with its
two converters is the C# side of the null rule), `GzipMemberReader` with `GzipMemberInput` and `Crc32` for the gzip framing (record
0128: a raw `DeflateStream` per member fed one byte per read, so the reader sees where a member ends), `Vandox.Core.IO` (`StrictGzip`,
still required by `BatchDecoder.OpenAsync` and used by the log import's own `GZipStream`).

Fixtures (record 0075): the golden batch `testdata/wire/all-kinds.jsonl` (one record of every kind) and `testdata/wire/replaced-text.jsonl`
(texts at their limits that held invalid UTF-8 before encoding) are written by the Go encoder and decoded by the C# contract test
`WireContractTests`; the shared decoder cases `testdata/wire/decoder-cases.json` are run by both decoders (`internal/wire/decode_test.go`
and `WireContractTests`) and pin the JSON-level rules and the gzip framing (cases `G1`-`G13` build their gzip stream from the case's
`gzip` field); three gzip samples of the golden batch, two written by Go's `compress/gzip` (`all-kinds.jsonl.gz`,
`all-kinds-two-members.jsonl.gz`) and one by `gzip -9 -c` with a file name (`all-kinds-gzip1.jsonl.gz`), are decoded by the C#
contract test to the golden records with every input byte consumed. `internal/wire/golden_test.go` writes or checks the fixtures
`all-kinds.jsonl`, `replaced-text.jsonl` and the two Go gzip samples under `VANDOX_UPDATE_GOLDEN=1`.
