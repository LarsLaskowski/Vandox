# Plan: Shared data model and versioned wire format

Source: Issue #10
Status: Draft
Tier: security — the ingest wire format is external input named in *Security areas* 10 of
`.squad/project.md` (parsing of external input), and its decoder sets the memory bounds that area 1
(ingest request limits) relies on.

## Problem / root cause

Feature, no bug. Agent (#30–#39), backend ingest (#40, #41) and the log importer (#15–#20) will all
produce the same kinds of records. This change adds the shared, validated record model and the versioned,
compressed batch format the agent sends to the backend, plus the decoder the ingest API will use. Nothing
is wired into `cmd/` yet: the first consumers are #39 (agent sender) and #40 (ingest API).

Claims of the issue, checked against the code (`main` at 23aa2f0):

- "Agent, backend and the log importer produce the same kinds of records": **confirmed as design intent,
  not code** — none of the three exists yet; `internal/` holds only `cli` and `version`. The shared-model
  requirement follows from 0005 and 0014.
- "A single model in `internal/` keeps them compatible": **confirmed absent** — no model package exists, so
  this change creates `internal/model` and `internal/wire` from scratch.
- "The backend rejects unknown major versions": **refuted as stated** — there is no backend ingest code
  yet (#40). This change delivers the rejection in the shared decoder (`wire.NewDecoder` returns
  `wire.ErrUnsupportedVersion`); mapping it to an HTTP response is #40.
- "The backend later adds the receive timestamp": **confirmed** — matches #40; the receive timestamp is
  therefore *not* part of the wire format (0044).
- Dependencies #4 and #5: **confirmed** closed.

Related gap found on the way: accepted record 0018 (and, quoting it, the context of 0028,
`docs/ARCHITECTURE.md` *Offline behavior and backfill* and `.squad/project.md` *Security areas* 1) says
"every batch carries an identity and sequence number" without defining either, while the issue, #39, #40
and #41 put the sequence number on every record and deduplicate by (agent ID, sequence number). This is a
decision, not a wording slip: 0045 (Proposed) supersedes 0018, restates its spool-and-backfill decision
unchanged and defines the batch identity as the header's `agent_id` plus the sequence numbers of the
batch's records; the documents quoting it are updated with it (see *Documentation updates*). 0028 is not
superseded: its decision does not depend on the wording, and its context names 0018 as history. The same
`ARCHITECTURE.md`'s "as of now only the two binaries' `--version` exist" becomes untrue with this change and
is updated too.

## Acceptance criteria

All `FieldError` assertions check `errors.Is(err, model.ErrInvalid)` and the exact `Field` path given here.

**Model (`internal/model`)**

- [ ] AC1 `FieldError`: `Error()` returns `"model: <Field>: <Reason>"` (and `"model: <Reason>"` when
  `Field` is empty); `errors.Is(fe, model.ErrInvalid)` is true; `errors.As` recovers `Field` and `Reason`.
  `QuoteName`: `"mount"` → `"\"mount\""`; `"a\nb"` → `"\"a\\nb\""` (Go literals; the result holds no byte
  below 0x20); a key of `MaxNameBytes` bytes is quoted whole without marker; a key of `MaxNameBytes`+1 and
  of 5000 bytes → `strconv.Quote(key[:MaxNameBytes]) + "..."`; a key whose cut falls inside a multi-byte
  rune is quoted with the partial bytes as `\x..` escapes (deterministic, no panic); `""` → `"\"\""`.
- [ ] AC2 `Meta.Validate`: a valid agent meta passes; `origin` not one of `agent`/`import`/`backend` →
  field `origin`; `source` empty, longer than `MaxNameBytes`, or not matching the name pattern (e.g.
  `"-x"`, `"a b"`, `"a\n"`) → field `source`; zero `captured_at` → field `captured_at`; `captured_at` with
  a non-zero zone offset (e.g. `+02:00`) → field `captured_at`; a zero-offset time in a non-UTC
  `*time.Location` (`time.FixedZone("", 0)`) passes; origin `agent` with `seq` 0 → field `seq`; origin
  `import` or `backend` with `seq` ≠ 0 → field `seq`.
- [ ] AC3 `Record.Validate`: nil `Data` and a typed nil payload (e.g. `(*model.MetricPoint)(nil)`) → field
  `data`; a payload error is prefixed (`data.processes[1].pid`); meta errors come first; a gap with cause
  `sequence_missing` or `no_data` fails with field `data.cause` when the origin is `agent` and passes when
  the origin is `backend`; `Record.Kind()` returns the payload's kind and `""` for nil `Data`.
- [ ] AC4–AC11, one per payload type: a fully populated valid value passes, a minimal valid value passes,
  and each rule in the payload table under *Approach* violated on its own fails with exactly the listed
  field path (table-driven, one case per rule). Every type's `Kind()` returns its constant. AC4
  `MetricPoint`, AC5 `ProcessSnapshot`, AC6 `ConnectionSnapshot`, AC7 `ServiceState`, AC8 `MariaDBStatus`,
  AC9 `KernelEvent`, AC10 `LogLine`, AC11 `Gap`. Map-key paths are rendered with `QuoteName` and checked
  exactly: AC4 a label key `"a\nb"` → `Field == "labels[\"a\\nb\"]"` (Go literal, i.e. the text
  `labels["a\nb"]` with a backslash, no newline byte); a 200-byte label key of `k` → `Field ==
  "labels[" + strconv.Quote(strings.Repeat("k", 128)) + "...]"`; an over-long label value under a valid key
  `mount` → `labels["mount"]`. AC8 the same two key cases for `status[…]` and `variables[…]`. AC6 zone
  cases, each rejected with reason `zone not allowed` and the listed field: `remotes[0].addr` =
  `fe80::1%eth0`, `listeners[0].local` = `[fe80::1%eth0]:80`, `connections[0].local` and
  `connections[0].remote` each with a zone containing `\n`, a 5000-byte zone, and a zone containing `]:`
  (`[fe80::1%x]:]:80`); an IPv4-mapped address with a zone (`::ffff:1.2.3.4%eth0`) is rejected too; the same
  addresses without zone pass.

**Wire (`internal/wire`)**

- [ ] AC12 `Header.Validate`: `NewHeader` with a valid agent ID and boot ID passes and sets
  `FormatMajor == 1`, `FormatMinor == 0`; `FormatMajor` 0, 2 or -1 → `errors.Is(err,
  wire.ErrUnsupportedVersion)`; `FormatMinor` -1 → field `format_minor`; agent ID empty, 65 bytes, or with
  a character outside the pattern → field `agent_id`; boot ID in upper case, without dashes, with braces,
  or of the wrong length → field `boot_id`; mode not `live`/`backfill` (including `"Live"`) → field
  `mode`; `ClockOffset` nil, negative and positive all pass.
- [ ] AC13 `Batch.Validate`: no records → `wire.ErrEmptyBatch`; more than `DefaultLimits().MaxRecords`
  records → `wire.ErrLimitExceeded`; a record with origin `import` → field `records[i].origin`; sequence
  numbers equal or decreasing → `wire.ErrSequence`; an invalid record → field path prefixed
  `records[i].` (e.g. `records[2].data.name`); header errors are prefixed `header.` (e.g.
  `header.agent_id`).
- [ ] AC14 Round trip: a batch with at least one record of every kind (all optional fields set, including
  `ProcessSample.truncated`, `MariaDBThread.truncated` and `MariaDBStatus.complete`; every time built with
  `time.Date(…, time.UTC)`, so it carries no monotonic reading) encoded with `EncodeBatch` and read back
  with `NewDecoder`/`Next` yields a header and records that are `reflect.DeepEqual` to the input, then
  `io.EOF`. A second case encodes records whose `captured_at` and nested times (`Gap.from`/`to`,
  `ProcessSample.started_at`) are in `time.FixedZone("", 0)` and `time.FixedZone("X", 0)`: each decoded
  record is `reflect.DeepEqual` to the input with every time replaced by its `.UTC()`, and every decoded
  time's `Location()` is `time.UTC` (which only holds if the encoder wrote `Z`, since `+00:00` decodes to
  a fixed zone). The encoded bytes start with the gzip magic `1f 8b`; decompressed, line 1 is the header,
  there is exactly one line per record, every line ends with `\n`, and no line contains the keys `origin`
  or `received_at`.
- [ ] AC15 `EncodeBatch`: an invalid batch (invalid header, empty, bad record, wrong origin, sequence
  order) returns the matching error and writes **zero bytes** to `w`; a batch whose record 2 is valid in
  the model but encodes to more than `DefaultLimits().MaxLineBytes` bytes returns a `*wire.RecordSizeError`
  with `Index == 2`, `Size` the encoded length and `Limit == DefaultLimits().MaxLineBytes`, for which
  `errors.Is(err, wire.ErrRecordTooLarge)` and `errors.Is(err, wire.ErrLimitExceeded)` both hold, and
  writes zero bytes; a batch whose lines together exceed `DefaultLimits().MaxBatchBytes` returns
  `wire.ErrLimitExceeded` and writes zero bytes; a failing writer's error is returned wrapped
  (`errors.Is`).
- [ ] AC24 `CheckRecord`: a valid agent record returns `n` equal to the length of its encoded line
  including the `\n` (compared with the corresponding line of `EncodeBatch` output) and a nil error; a
  record that fails `Record.Validate` returns that `*model.FieldError` unprefixed (e.g. `data.name`); origin
  `import` → field `origin`; a `MariaDBStatus` with 500 threads of `MaxTextBytes` `info` (which passes
  `Record.Validate`) → `*wire.RecordSizeError` with `Index == -1`, wrapping `wire.ErrRecordTooLarge`; the
  same status with every `info` cut to 64 bytes and `truncated` set passes. Worst-case records of the kinds
  0044 says always fit pass `CheckRecord`: a `MetricPoint` with `MaxLabels` labels, a `LogLine`, a
  `KernelEvent` (OOM kill) — each with every text at its maximum byte length made of `\x01` (escaped as
  six bytes each) and every name at `MaxNameBytes`. `RecordSizeError.Error()` reads
  `"wire: records[2]: encoded record is <Size> bytes, limit <Limit>"`, without `records[…]: ` when
  `Index` is -1.
- [ ] AC16 Version rejection: a stream whose header has `format_major` 2 (or 0, -1, missing, `null`) is
  rejected by `NewDecoder` with `wire.ErrUnsupportedVersion` and `DecodeError.Line == 1`, **also when the
  rest of that header has an incompatible shape** (e.g. `"agent_id": {"x": 1}`); a header
  `{"format_major":1,"FORMAT_MAJOR":2,…}` is rejected (last key wins, case-insensitive) and
  `{"format_major":2,"Format_Major":1,…}` is accepted as 1.x; `format_minor` 7 with unknown keys in header,
  envelope and payload is accepted and the known fields decode.
- [ ] AC17 Malformed input: every form marked *rejected* in the *Accepted forms* table returns an error of
  the listed class from `NewDecoder` or `Next` (with the listed `Line` where given) and never panics; every
  form marked *accepted* decodes. Unknown `kind` (e.g. `"Metric"`, `"backup_run"`) and missing/empty
  `kind` → `wire.ErrUnknownKind`; missing `data` and `"data": null` → field `data`. The error text for
  an unknown kind is `"wire: line <n>: unknown record kind: " + model.QuoteName(kind)`; a 5000-byte kind
  containing `\n` yields exactly that text (cut to `MaxNameBytes` with `...`, no newline byte). A
  connection snapshot line whose `remotes[0].addr` is `"fe80::1%\n<b>x]:"`, whose
  `listeners[0].local` has a 5000-byte zone, or whose `connections[0].remote` is `"[fe80::1%x]:]:80"` →
  field `data.<list>[0].<addr|local|remote>` (reason `zone not allowed`). Unicode-folded keys: a record line
  with the keys `"Kind"` (KELVIN SIGN) and `"ſeq"` (LONG S) decodes as `kind` and `seq`
  (documented, accepted); a line with `"kind":"gap"` followed by `"Kind":"metric"` decodes as
  `metric` (last key wins across folded spellings). A `status` key `"evil\nkey<script>"` with a string
  value → `ErrMalformed` (the wrapped `encoding/json` text carries the raw key; the test asserts only the
  class, not the text).
- [ ] AC18 Limits, each tested with small explicit limits: a line of exactly `MaxLineBytes` bytes is
  read, one byte more → `wire.ErrLimitExceeded`; decompressed content beyond `MaxBatchBytes` (a small
  gzip of many zero-padded or repeated lines) → `wire.ErrLimitExceeded`; `MaxRecords + 1` records →
  `wire.ErrLimitExceeded`; `DefaultLimits()` returns 1 MiB / 16 MiB / 20 000; a zero or negative field in
  the `Limits` passed to `NewDecoder` means that field's default.
- [ ] AC19 Integrity: a stream with a corrupted gzip CRC, a truncated stream and a stream with trailing
  non-gzip bytes each yield the records before the damage and then an error that wraps
  `wire.ErrMalformed` instead of `io.EOF`; two concatenated gzip members are read as one stream; a stream
  that is not gzip at all (plain JSON Lines, zstd magic `28 b5 2f fd`) → `wire.ErrMalformed` from
  `NewDecoder`; an empty input → `wire.ErrMalformed`; header only → `wire.ErrEmptyBatch` from the first
  `Next`.
- [ ] AC20 Batch rules while streaming: equal or decreasing `seq` → `wire.ErrSequence` with the line of the
  offending record; `seq` 0 → field `seq`; after any error, every further `Next` returns the same error;
  after `io.EOF`, `Next` returns `io.EOF` again.
- [ ] AC21 Origin and receive time cannot be injected: a record line with `"origin":"backend"` and
  `"received_at":"…"` decodes to a record with `Origin == model.OriginAgent` (and the gap rule of AC3
  applies to it); a gap with cause `sequence_missing` in a batch → field `data.cause`.
- [ ] AC22 `Decoder.Header()` returns the validated header (including a nil and a set `ClockOffset`);
  `Close` closes the gzip reader and does not close the underlying reader (an `io.ReadCloser` fake records
  no `Close` call).
- [ ] AC23 Documentation (verified by the Reviewer in step 8, not by a unit test): `docs/WIRE_FORMAT.md`
  describes the stream layout, header (including the capture-context meaning of `boot_id` and
  `clock_offset_ns` and the batching rule of 0046), envelope, every record kind with its fields and rules,
  the limits, the versioning rules of 0043, the batch rules and the producer size contract of 0044, the
  batch identity of 0045, the producer mapping for connection endpoints (see *Approach*), the accepted
  forms and a decompressed example batch; it matches the code. `README.md`, `docs/ARCHITECTURE.md` and `.squad/project.md` are updated as
  listed below.

## Approach

Two packages, both standard library only (0042):

- `internal/model` — the record types, their validation and the shared bounds. No JSON code beyond
  struct tags on the payload types (the tags define the wire shape of `data`). Usable by the importer and
  the backend without the wire package.
- `internal/wire` — header, batch, limits, `EncodeBatch` and the streaming `Decoder`.

### Common rules (model)

- **Name pattern** `^[A-Za-z0-9][A-Za-z0-9._:/@+-]*$`, 1..`MaxNameBytes` (128) bytes (Go RE2; `$` matches
  only at the end of the text, so a trailing `\n` is rejected). Used for `source`, metric names, metric
  units, label keys and `Gap.Collector`.
- **Short text**: at most `MaxShortTextBytes` (1024) bytes; **text**: at most `MaxTextBytes` (16384)
  bytes. Lengths are in bytes. No character restriction (0021; display escaping is area 12, not this
  change).
- **Lists and maps**: at most `MaxItems` (4096) entries each; metric labels at most `MaxLabels` (32).
- **UTC time**: non-zero and zone offset 0 (`_, off := t.Zone(); off == 0`); optional times
  (`omitzero`) are checked only when non-zero.
- **Floats**: finite (`!math.IsNaN && !math.IsInf`); percentages and rates additionally ≥ 0.
- **Unknown vs zero**: an optional measurement that may be unknown is a pointer (`*uint64`, `*int16`,
  `*time.Duration`, `*uint8`) with `omitempty`; nil means "not known", never 0 (spirit of 0028).
- Field paths: JSON names, list index in brackets, map key in brackets rendered with `QuoteName`:
  `processes[3].pid`, `labels["mount"]`, `status["Uptime"]`. `QuoteName(key)` cuts the key to its first
  `MaxNameBytes` bytes (a byte cut; a split rune becomes `\x..` escapes), quotes it with `strconv.Quote`
  and appends `...` after the closing quote if it was cut. A map key is attacker-controlled and is the only
  value that ever enters a `Field`; quoted and cut it carries no control byte and at most
  `4 × MaxNameBytes + 5` bytes. Payload `Validate` returns paths relative to the payload; `Record.Validate`
  prefixes `data.`.
- **Addresses**: numeric `netip.Addr` / `netip.AddrPort`, valid, and **without zone** (`Zone() == ""`, also
  for IPv4-mapped IPv6). `netip` accepts any zone text of any length (including `\n`, `]:` and 5000
  bytes), and a zone names an interface of the sending host that means nothing to the backend, so the model
  rejects it (reason `zone not allowed`) instead of bounding it; the producer strips the zone (the
  link-local address itself is kept).

### Meta (every record)

| Field | Go | Wire | Rule |
| ----- | -- | ---- | ---- |
| Origin | `Origin` | not on the wire; the decoder sets `agent` | one of `agent`, `import`, `backend` |
| Source | `string` | `source` | name pattern (collector or parser, e.g. `proc.meminfo`, `journal`, `legacy-log`) |
| Seq | `uint64` | `seq` | origin `agent`: > 0; otherwise 0 |
| CapturedAt | `time.Time` | `captured_at` (RFC 3339, written as UTC `Z`) | UTC time; the time the data describes (a log line: the time stamped in the line, else when it was read; a gap: when it was recorded) |

The receive timestamp is not part of the model or the wire; the backend stores it (#40).

### Payload rules (AC4–AC11)

Required means non-empty / non-zero. Each row is one test case at least.

**`MetricPoint`** (`metric`): `name` required, name pattern → `name`; `value` finite → `value`; `unit`
optional, name pattern → `unit`; `labels` ≤ `MaxLabels` → `labels`; each key name pattern →
`labels[<QuoteName(key)>]`; each value short text → `labels[<QuoteName(key)>]`. Counters are named with suffix `_total`
(documented convention, not validated).

**`ProcessSnapshot`** (`process_snapshot`): fields `complete bool` (true only if `processes` holds every
process and `programs` every program the producer saw; false when the source shows only part of them or
entries were left out for size, 0044), `total *uint32` (processes on the system, if known), `processes []ProcessSample`, `programs []ProgramAggregate`. At least one process or
program → `processes`; `processes` ≤ `MaxItems` → `processes`; `programs` ≤ `MaxItems` → `programs`; PIDs
unique → `processes[i].pid`; program names unique → `programs[i].program`.
`ProcessSample`: `pid` > 0 → `processes[i].pid`; `ppid` ≥ 0 → `.ppid`; `user` short text → `.user`;
`command` required short text → `.command`; `cmdline` text → `.cmdline`; `truncated bool` (command or
cmdline cut by the source, e.g. `sw-engi+` in the legacy log); `state` empty or exactly one ASCII letter →
`.state`; `started_at` optional UTC time → `.started_at`; `cpu_percent` finite ≥ 0 → `.cpu_percent`;
`rss_bytes uint64`; `pss_bytes`, `swap_bytes` `*uint64`; `oom_score_adj` `*int16` in -1000..1000 →
`.oom_score_adj`.
`ProgramAggregate`: `program` required short text → `programs[i].program`; `count` ≥ 1 → `.count`;
`cpu_percent` finite ≥ 0 → `.cpu_percent`; `rss_bytes uint64`; `pss_bytes *uint64`; `rss_overcounted
bool` (RSS sum counts shared pages once per process, #20).

**`ConnectionSnapshot`** (`connection_snapshot`): `complete bool` (true only if no entry the producer saw
was left out of any list — in particular `connections` holds every socket; false after a reduction for
size, 0044);
`states []StateCount`, `processes []ProcessConnections`, `remotes []RemoteCount`, `listeners
[]Listener`, `connections []Connection`, each ≤ `MaxItems` → the list name. An entirely empty snapshot is
valid (a server with no sockets is a fact, not an error).
`proto` everywhere one of `tcp`, `tcp6`, `udp`, `udp6` → `<list>[i].proto`. TCP state set:
`ESTABLISHED SYN_SENT SYN_RECV FIN_WAIT1 FIN_WAIT2 TIME_WAIT CLOSE CLOSE_WAIT LAST_ACK LISTEN CLOSING
NEW_SYN_RECV`; `state` for `tcp`/`tcp6` required and in the set, for `udp`/`udp6` empty or in the set →
`<list>[i].state`. `count` ≥ 1 → `<list>[i].count`.
`StateCount{proto, state, count}`; `ProcessConnections{pid > 0, command required short text, count}`;
`RemoteCount{addr netip.Addr valid, no zone, count}` → `remotes[i].addr`; `Listener{proto, local
netip.AddrPort valid, no zone, pid ≥ 0, command short text}` → `listeners[i].local`; `Connection{proto,
local, remote netip.AddrPort valid, no zone, state, pid ≥ 0, command short text}` → `connections[i].local`
/ `.remote` (invalid and zoned addresses fail on the same path; reasons `invalid address` / `zone not
allowed`). Addresses are numeric without zone; mapping a source's notation is the producer's job and is
stated in `docs/WIRE_FORMAT.md`: a zone (`fe80::1%eth0`) is stripped; a wildcard local address (`*` in `lsof`) becomes `0.0.0.0` for IPv4 and `::` for
IPv6 sockets; a port given as a service name (legacy `lsof -ni` without `-P`, issue #20) is mapped to its
number by the producer from a built-in table, never by a lookup on the host; a socket whose address or port
cannot be mapped is left out with a warning and the snapshot's `complete` set to false; an unconnected
socket without a remote endpoint (listening TCP, unconnected UDP) goes to `listeners`, never to
`connections`.

**`ServiceState`** (`service_state`): `unit` required, `^[A-Za-z0-9:_.@\\-]+$`, ≤ 256 bytes → `unit`;
`load_state` one of `loaded not-found bad-setting error masked stub merged` → `load_state`;
`active_state` one of `active reloading inactive failed activating deactivating maintenance refreshing` →
`active_state`; `sub_state` optional `^[a-z0-9-]+$` ≤ 64 → `sub_state`; `active_enter_at` optional UTC
time → `active_enter_at`; `restarts uint32`.

**`MariaDBStatus`** (`mariadb_status`): `availability` one of `up`, `down`, `not_answering` →
`availability`; `ping_latency` `*time.Duration` (wire `ping_latency_ns`) ≥ 0 → `ping_latency_ns`;
`status map[string]uint64` (numeric global status), `variables map[string]string` (system variables),
`threads []MariaDBThread`, each ≤ `MaxItems` → the name; `complete bool` (true only if `status`,
`variables` and `threads` hold every entry the producer read; false for a configured subset or a reduction
for size, 0044; no rule); keys `^[A-Za-z][A-Za-z0-9_]*$` ≤ `MaxNameBytes` →
`status[<QuoteName(key)>]` / `variables[<QuoteName(key)>]`; variable values short text →
`variables[<QuoteName(key)>]`; when availability is
not `up`, `status`, `variables` and `threads` must be empty → `availability`.
`MariaDBThread`: `id uint64`, `user`, `host`, `db`, `command`, `state` short text → `threads[i].<field>`;
`time_seconds uint64`; `info` text → `threads[i].info`; `truncated bool` (the producer shortened `info`).

**`KernelEvent`** (`kernel_event`): `type` one of `oom_kill`, `boot` → `type`; exactly the matching
sub-struct set: `oom_kill` requires `oom_kill` non-nil and `boot` nil, and vice versa → `oom_kill` /
`boot`; `message` text → `message`.
`OOMKill`: `victim_pid` > 0 → `oom_kill.victim_pid`; `victim_command` required short text →
`oom_kill.victim_command`; `anon_rss_bytes *uint64`; `oom_score_adj *int16` in -1000..1000 →
`oom_kill.oom_score_adj`.
`Boot`: `boot_id` required, lower-case UUID `^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$`
→ `boot.boot_id`; `previous_boot_id` optional, same pattern → `boot.previous_boot_id`; `booted_at` optional
UTC time → `boot.booted_at`; `previous_uptime` `*time.Duration` (wire `previous_uptime_ns`) ≥ 0 →
`boot.previous_uptime_ns`.

**`LogLine`** (`log_line`): `log` required short text (`journal` or the file path) → `log`; `program` short
text → `program`; `pid` ≥ 0 → `pid`; `priority *uint8` ≤ 7 → `priority`; `message` text (may be empty) →
`message`; `truncated bool` (the producer cut the message to `MaxTextBytes`).

**`Gap`** (`gap`): `from` UTC time → `from`; `to` UTC time and after `from` → `to`; `cause` one of
`agent_not_running spool_dropped collector_timeout sequence_missing no_data unknown` → `cause`;
`collector` name pattern if set, required for `collector_timeout` → `collector`; `first_seq`/`last_seq`:
both 0 or both > 0 with `first_seq ≤ last_seq` → `last_seq`, and both required (> 0) for `spool_dropped`
and `sequence_missing` → `first_seq`. Origin rule in `Record.Validate` (AC3): `sequence_missing` and
`no_data` are invalid for origin `agent` → `data.cause`.

### Header and batch (wire)

- Header line: `{"format_major":1,"format_minor":0,"agent_id":"…","boot_id":"…","clock_offset_ns":…,"mode":"live"}`.
  `agent_id` `^[A-Za-z0-9][A-Za-z0-9._-]*$`, 1..64 bytes; `boot_id` lower-case UUID as in `Boot`;
  `clock_offset_ns` optional (agent clock minus the reference time of its time synchronization; positive =
  agent ahead; any int64; omitted = unknown); `mode` `live` or `backfill`, a hint only (0022).
- Capture context (0046): `boot_id` is the boot during which every record of the batch was captured, and
  `clock_offset_ns` the offset estimate valid for the capture of every record (corrected time =
  `captured_at − clock_offset_ns`) — both describe capture, not sending. Producer contract (#38/#39, not
  checkable by the decoder): boot ID and offset estimate are spooled with each record, and a batch holds
  only records with the same boot ID and the same estimate (or all without one). Doc comments on
  `Header.BootID` and `Header.ClockOffset` state this.
- Batch identity (0045): no batch ID; a batch is identified by `agent_id` and the `seq` values of its
  records, taken from one persistent, never reused per-agent counter. The backend deduplicates per record
  by (agent ID, seq) (#40).
- Record line: `{"kind":"…","source":"…","seq":N,"captured_at":"…Z","data":{…}}`.
- Batch rules (0044): ≥ 1 record, ≤ `MaxRecords`, every record origin `agent` with `seq` > 0, `seq`
  strictly increasing (gaps allowed — the backend detects them, #41).
- Per-record size (0044): `CheckRecord(r)` runs `r.Validate()`, requires origin `agent` (field `origin`),
  marshals the envelope exactly as `EncodeBatch` does and returns `len(line)+1` (the bytes the record adds to
  the decompressed batch). If `len(line)` (without the `\n`, as the decoder's line limit counts it) exceeds
  `DefaultLimits().MaxLineBytes`, it returns `*RecordSizeError{Index: -1, Size: len(line), Limit: …}`.
  Producer contract in `docs/WIRE_FORMAT.md`: call `CheckRecord` when a record is created, before it enters
  the spool; on `ErrRecordTooLarge` shorten `cmdline` / `info` with `truncated` set, then leave out entries
  with `complete` false; `metric`, `service_state`, `kernel_event`, `log_line` and `gap` always fit (at
  maximum field sizes with every byte escaped as `\uXXXX`, the largest, a metric with 32 labels, stays near
  200 KiB).
- `NewDecoder`: wraps `r` in `gzip.NewReader` (multistream as by default), counts decompressed bytes
  against `MaxBatchBytes`, reads lines with a `bufio.Scanner` whose maximum token size admits exactly
  `MaxLineBytes` bytes before the `\n` (a trailing `\r` counts towards the line and is then JSON
  whitespace). Line 1: unmarshal into `struct{ FormatMajor *int \`json:"format_major"\` }` first; nil or ≠ 1
  → `ErrUnsupportedVersion`; then unmarshal the full `Header` and `Validate` it.
- `Next`: reads one line, `json.Unmarshal` into an unexported envelope with `Data json.RawMessage`;
  missing/`null` data → `FieldError{Field:"data"}`; looks the kind up in an unexported kind → constructor
  table (unknown → `fmt.Errorf("%w: %s", ErrUnknownKind, model.QuoteName(kind))`, so the kind in the text
  is cut to `MaxNameBytes` and quoted); unmarshals `data` into the payload; sets `Origin = OriginAgent`;
  `Record.Validate`; checks the sequence order and the record count. At end of input it surfaces any
  scanner/gzip error (CRC, truncation, trailing bytes) as `ErrMalformed`, returns `ErrEmptyBatch` if no
  record was read, otherwise `io.EOF`. Errors are wrapped in `*DecodeError{Line, Err}` and sticky.
- `EncodeBatch`: `b.Validate()` first (nothing written on failure); marshals every line in memory with the
  same unexported function `CheckRecord` uses, checks each against `DefaultLimits().MaxLineBytes`
  (`*RecordSizeError` with the record's index) and the running total against `MaxBatchBytes`
  (`ErrLimitExceeded`) before writing anything through `gzip.NewWriter(w)`; writes `captured_at` as
  `.UTC()`; closes the gzip writer (not `w`). On a write error the output is incomplete and must be
  discarded (documented).
- No `context.Context`: like `encoding/json`, the codec does no I/O of its own (0042).

### Accepted forms (guard on the decoder input)

Taken from the real consumers — `compress/gzip`, `bufio.Scanner` (`ScanLines`), `encoding/json` v1,
`time.Time.UnmarshalJSON`, `netip` text unmarshalling — and checked with a scratch program under Go
1.27.0. Each row is at least one AC17 case (classes: M = `ErrMalformed`, V = `ErrUnsupportedVersion`,
K = `ErrUnknownKind`, F = `model.ErrInvalid` with field, L = `ErrLimitExceeded`).

| Input form | Consumer behavior | Guard result |
| ---------- | ----------------- | ------------ |
| Not gzip (plain JSON, zstd, empty input) | `gzip.NewReader` fails | rejected, M, line 1 |
| gzip FNAME/FCOMMENT/FEXTRA header fields | read and ignored (name/comment > 511 bytes → `gzip.ErrHeader`) | accepted / rejected M |
| Concatenated gzip members | read as one stream (multistream default) | accepted |
| Trailing non-gzip bytes, truncated stream, bad CRC/ISIZE | error only at end of stream | rejected M at the end, after earlier records (consumer commits only on `io.EOF`, 0044) |
| Decompressed size > `MaxBatchBytes` (gzip bomb) | counted by the decoder | rejected L |
| Line > `MaxLineBytes` | `bufio.ErrTooLong` | rejected L |
| `\n` line ends, `\r\n` line ends, last line without `\n` | `ScanLines` splits on `\n`, drops one trailing `\r` | accepted |
| Lone `\r` as separator | not a separator → two objects on one line | rejected M |
| Empty or whitespace-only line (also a trailing one) | `json.Unmarshal`: unexpected end of input | rejected M |
| UTF-8 BOM at the start of a line | invalid character `﻿` | rejected M |
| Leading/trailing spaces or tabs around the object | JSON whitespace | accepted |
| Two values on one line, comments, trailing comma, `NaN`/`Infinity`, single quotes | not JSON / trailing data | rejected M |
| Line is `null` | unmarshals to the zero struct | header: rejected V (no `format_major`); record: rejected K (empty kind) |
| Line is an array, string or number | type error | rejected M |
| Keys in other case (`FORMAT_MAJOR`, `Kind`, `DATA`) | matched case-insensitively | accepted as the field |
| Keys with Unicode case-folding equivalents (`"Kind"` KELVIN SIGN for `k`, `"ſeq"` LONG S for `s`) | `encoding/json` folds them like ASCII case (struct field names only, not map keys) | accepted as the field; a folded duplicate takes part in last-key-wins; documented in `docs/WIRE_FORMAT.md` because `jq` shows a different key than the decoder uses |
| Map keys (`labels`, `status`, `variables`) with control characters, `<`, or over `MaxNameBytes` | kept as-is by `encoding/json` | rejected F by the key pattern; the path shows the key through `QuoteName` (quoted, cut); a type error inside such a map is rejected M and the wrapped `encoding/json` text carries the raw key |
| Duplicate keys | last one wins (same parser in the version pre-read and the full read, so both agree) | validated as the last value |
| Unknown keys (any nesting depth ≤ the JSON depth limit 10000) | ignored | accepted (0043); `origin`, `received_at` ignored too (0044) |
| `format_major` missing, `null`, 0, negative, ≠ 1 | pre-read | rejected V, line 1 |
| `format_major` as `1.0`, `1e0`, `"1"` | type error for `int` | rejected M, line 1 |
| `format_major` 1 with an incompatible header shape | full read fails after the version passed | rejected M (a header of major 1 must be well-formed) |
| `format_major` 2 with an incompatible header shape | pre-read only | rejected V |
| `kind` unknown, wrong case (`"Metric"`), empty, missing | exact comparison against the table | rejected K |
| `data` missing or `null` | — | rejected F `data` |
| `data` not an object | type error | rejected M |
| Integers out of range (negative into `uint64`, > 2^31-1 into `int32` PID, ≥ 2^64), fractions or exponents into integer fields | type error | rejected M |
| Float out of `float64` range (`1e400`) | type error | rejected M |
| Time: `Z`, `+00:00`, `-00:00`, fractional seconds | parsed; `+00:00` gives a zero-offset non-UTC location | accepted (offset 0) |
| Time: other offset (`+02:00`) | parsed with that offset | rejected F |
| Time: space instead of `T`, lower-case `t`/`z`, not RFC 3339, `null` | parse error / `null` leaves zero | rejected M / zero → rejected F |
| Time `0001-01-01T00:00:00Z` | zero time | rejected F |
| Strings with invalid UTF-8 bytes | replaced by U+FFFD | accepted (documented, 0042) |
| Strings with escaped control characters (`\u0000`, `\n`) | decoded as such | accepted where the field is text; rejected F where a name pattern applies |
| Address `""`, hostname (`host:80`), missing port | `""` → zero value (then F), others parse error | rejected F / M |
| Address with zone (`fe80::1%eth0`, `[fe80::1%eth0]:80`, `::ffff:1.2.3.4%eth0`), including a zone with `\n`, `]:` or 5000 bytes | parsed, any zone text accepted by `netip` | rejected F `<list>[i].addr` / `.local` / `.remote`, reason `zone not allowed` |
| Empty zone (`fe80::1%`), zone on plain IPv4 (`1.2.3.4%eth0`) | parse error | rejected M |
| IPv4-mapped IPv6 without zone (`::ffff:1.2.3.4`) | parsed | accepted |
| Boot ID / agent ID / names in other forms (upper case, braces, `urn:uuid:`, trailing newline) | exact pattern | rejected F |

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| `internal/model` | `model.go` | new: package doc, bounds, `Kind`, `Origin`, `Meta`, `Record`, `Payload`, `ErrInvalid`, `FieldError`, `QuoteName`, unexported pattern/text/time/address helpers |
| `internal/model` | `metric.go`, `process.go`, `connection.go`, `service.go`, `mariadb.go`, `kernel.go`, `logline.go`, `gap.go` | new: one payload type (plus its element types) per file |
| `internal/wire` | `wire.go` | new: package doc, version constants, `Mode`, `Header`, `NewHeader`, `Batch`, `Limits`, `DefaultLimits`, sentinel errors (including `ErrRecordTooLarge`), `DecodeError`, `RecordSizeError` |
| `internal/wire` | `encode.go` | new: `EncodeBatch`, `CheckRecord`, unexported envelope and line marshalling shared by both |
| `internal/wire` | `decode.go` | new: `Decoder`, `NewDecoder`, `Header`, `Next`, `Close`, kind table |
| docs | `docs/WIRE_FORMAT.md` | new |
| docs | `README.md`, `docs/ARCHITECTURE.md`, `.squad/project.md` | updated (see below) |

No change to `cmd/`, `go.mod` or `go.sum`. No existing file is rewritten by the skeleton.

## Signatures (for the Dev's skeleton)

Every exported identifier gets a doc comment starting with its name. Payload methods use **pointer
receivers**; a nil receiver's `Validate` returns `&FieldError{Reason: "required"}` (field empty, so
`Record.Validate` reports `data`).

```go
// internal/model/model.go
package model

const (
	MaxNameBytes      = 128
	MaxShortTextBytes = 1024
	MaxTextBytes      = 16384
	MaxItems          = 4096
	MaxLabels         = 32
)

type Kind string

const (
	KindMetric             Kind = "metric"
	KindProcessSnapshot    Kind = "process_snapshot"
	KindConnectionSnapshot Kind = "connection_snapshot"
	KindServiceState       Kind = "service_state"
	KindMariaDBStatus      Kind = "mariadb_status"
	KindKernelEvent        Kind = "kernel_event"
	KindLogLine            Kind = "log_line"
	KindGap                Kind = "gap"
)

type Origin string

const (
	OriginAgent   Origin = "agent"
	OriginImport  Origin = "import"
	OriginBackend Origin = "backend"
)

var ErrInvalid = errors.New("invalid record")

type FieldError struct {
	Field  string
	Reason string
}

func (e *FieldError) Error() string
func (e *FieldError) Unwrap() error // returns ErrInvalid

// QuoteName returns s for use in a field path or error message: cut to its first MaxNameBytes bytes,
// quoted with strconv.Quote, followed by "..." when it was cut.
func QuoteName(s string) string

type Payload interface {
	Kind() Kind
	Validate() error
}

type Meta struct {
	Origin     Origin
	Source     string
	Seq        uint64
	CapturedAt time.Time
}

func (m *Meta) Validate() error

type Record struct {
	Meta
	Data Payload
}

func (r *Record) Kind() Kind
func (r *Record) Validate() error
```

```go
// internal/model/metric.go
type MetricPoint struct {
	Name   string            `json:"name"`
	Value  float64           `json:"value"`
	Unit   string            `json:"unit,omitempty"`
	Labels map[string]string `json:"labels,omitempty"`
}

func (p *MetricPoint) Kind() Kind
func (p *MetricPoint) Validate() error
```

```go
// internal/model/process.go
type ProcessSnapshot struct {
	Complete  bool               `json:"complete"`
	Total     *uint32            `json:"total,omitempty"`
	Processes []ProcessSample    `json:"processes,omitempty"`
	Programs  []ProgramAggregate `json:"programs,omitempty"`
}

type ProcessSample struct {
	PID         int32     `json:"pid"`
	PPID        int32     `json:"ppid,omitempty"`
	User        string    `json:"user,omitempty"`
	Command     string    `json:"command"`
	Cmdline     string    `json:"cmdline,omitempty"`
	Truncated   bool      `json:"truncated,omitempty"`
	State       string    `json:"state,omitempty"`
	StartedAt   time.Time `json:"started_at,omitzero"`
	CPUPercent  float64   `json:"cpu_percent"`
	RSSBytes    uint64    `json:"rss_bytes"`
	PSSBytes    *uint64   `json:"pss_bytes,omitempty"`
	SwapBytes   *uint64   `json:"swap_bytes,omitempty"`
	OOMScoreAdj *int16    `json:"oom_score_adj,omitempty"`
}

type ProgramAggregate struct {
	Program        string  `json:"program"`
	Count          uint32  `json:"count"`
	CPUPercent     float64 `json:"cpu_percent"`
	RSSBytes       uint64  `json:"rss_bytes"`
	PSSBytes       *uint64 `json:"pss_bytes,omitempty"`
	RSSOvercounted bool    `json:"rss_overcounted,omitempty"`
}

func (s *ProcessSnapshot) Kind() Kind
func (s *ProcessSnapshot) Validate() error
```

```go
// internal/model/connection.go
type Proto string

const (
	ProtoTCP  Proto = "tcp"
	ProtoTCP6 Proto = "tcp6"
	ProtoUDP  Proto = "udp"
	ProtoUDP6 Proto = "udp6"
)

type ConnectionSnapshot struct {
	Complete    bool                 `json:"complete"`
	States      []StateCount         `json:"states,omitempty"`
	Processes   []ProcessConnections `json:"processes,omitempty"`
	Remotes     []RemoteCount        `json:"remotes,omitempty"`
	Listeners   []Listener           `json:"listeners,omitempty"`
	Connections []Connection         `json:"connections,omitempty"`
}

type StateCount struct {
	Proto Proto  `json:"proto"`
	State string `json:"state,omitempty"`
	Count uint32 `json:"count"`
}

type ProcessConnections struct {
	PID     int32  `json:"pid"`
	Command string `json:"command"`
	Count   uint32 `json:"count"`
}

type RemoteCount struct {
	Addr  netip.Addr `json:"addr"`
	Count uint32     `json:"count"`
}

type Listener struct {
	Proto   Proto          `json:"proto"`
	Local   netip.AddrPort `json:"local"`
	PID     int32          `json:"pid,omitempty"`
	Command string         `json:"command,omitempty"`
}

type Connection struct {
	Proto   Proto          `json:"proto"`
	Local   netip.AddrPort `json:"local"`
	Remote  netip.AddrPort `json:"remote"`
	State   string         `json:"state,omitempty"`
	PID     int32          `json:"pid,omitempty"`
	Command string         `json:"command,omitempty"`
}

func (s *ConnectionSnapshot) Kind() Kind
func (s *ConnectionSnapshot) Validate() error
```

```go
// internal/model/service.go
type ServiceState struct {
	Unit          string    `json:"unit"`
	LoadState     string    `json:"load_state"`
	ActiveState   string    `json:"active_state"`
	SubState      string    `json:"sub_state,omitempty"`
	ActiveEnterAt time.Time `json:"active_enter_at,omitzero"`
	Restarts      uint32    `json:"restarts"`
}

func (s *ServiceState) Kind() Kind
func (s *ServiceState) Validate() error
```

```go
// internal/model/mariadb.go
type MariaDBAvailability string

const (
	MariaDBUp           MariaDBAvailability = "up"
	MariaDBDown         MariaDBAvailability = "down"
	MariaDBNotAnswering MariaDBAvailability = "not_answering"
)

type MariaDBStatus struct {
	Availability MariaDBAvailability `json:"availability"`
	PingLatency  *time.Duration      `json:"ping_latency_ns,omitempty"`
	Status       map[string]uint64   `json:"status,omitempty"`
	Variables    map[string]string   `json:"variables,omitempty"`
	Threads      []MariaDBThread     `json:"threads,omitempty"`
	Complete     bool                `json:"complete"`
}

type MariaDBThread struct {
	ID          uint64 `json:"id"`
	User        string `json:"user,omitempty"`
	Host        string `json:"host,omitempty"`
	DB          string `json:"db,omitempty"`
	Command     string `json:"command,omitempty"`
	TimeSeconds uint64 `json:"time_seconds"`
	State       string `json:"state,omitempty"`
	Info        string `json:"info,omitempty"`
	Truncated   bool   `json:"truncated,omitempty"`
}

func (s *MariaDBStatus) Kind() Kind
func (s *MariaDBStatus) Validate() error
```

```go
// internal/model/kernel.go
type KernelEventType string

const (
	KernelEventOOMKill KernelEventType = "oom_kill"
	KernelEventBoot    KernelEventType = "boot"
)

type KernelEvent struct {
	Type    KernelEventType `json:"type"`
	OOMKill *OOMKill        `json:"oom_kill,omitempty"`
	Boot    *Boot           `json:"boot,omitempty"`
	Message string          `json:"message,omitempty"`
}

type OOMKill struct {
	VictimPID     int32   `json:"victim_pid"`
	VictimCommand string  `json:"victim_command"`
	AnonRSSBytes  *uint64 `json:"anon_rss_bytes,omitempty"`
	OOMScoreAdj   *int16  `json:"oom_score_adj,omitempty"`
}

type Boot struct {
	BootID         string         `json:"boot_id"`
	PreviousBootID string         `json:"previous_boot_id,omitempty"`
	BootedAt       time.Time      `json:"booted_at,omitzero"`
	PreviousUptime *time.Duration `json:"previous_uptime_ns,omitempty"`
}

func (e *KernelEvent) Kind() Kind
func (e *KernelEvent) Validate() error
```

```go
// internal/model/logline.go
type LogLine struct {
	Log       string `json:"log"`
	Program   string `json:"program,omitempty"`
	PID       int32  `json:"pid,omitempty"`
	Priority  *uint8 `json:"priority,omitempty"`
	Message   string `json:"message"`
	Truncated bool   `json:"truncated,omitempty"`
}

func (l *LogLine) Kind() Kind
func (l *LogLine) Validate() error
```

```go
// internal/model/gap.go
type GapCause string

const (
	GapAgentNotRunning  GapCause = "agent_not_running"
	GapSpoolDropped     GapCause = "spool_dropped"
	GapCollectorTimeout GapCause = "collector_timeout"
	GapSequenceMissing  GapCause = "sequence_missing"
	GapNoData           GapCause = "no_data"
	GapUnknown          GapCause = "unknown"
)

type Gap struct {
	From      time.Time `json:"from"`
	To        time.Time `json:"to"`
	Cause     GapCause  `json:"cause"`
	Collector string    `json:"collector,omitempty"`
	FirstSeq  uint64    `json:"first_seq,omitempty"`
	LastSeq   uint64    `json:"last_seq,omitempty"`
}

func (g *Gap) Kind() Kind
func (g *Gap) Validate() error
```

```go
// internal/wire/wire.go
package wire

const (
	MajorVersion = 1
	MinorVersion = 0
)

type Mode string

const (
	ModeLive     Mode = "live"
	ModeBackfill Mode = "backfill"
)

var (
	ErrUnsupportedVersion = errors.New("unsupported format version")
	ErrMalformed          = errors.New("malformed batch")
	ErrUnknownKind        = errors.New("unknown record kind")
	ErrSequence           = errors.New("sequence numbers not strictly increasing")
	ErrEmptyBatch         = errors.New("batch has no records")
	ErrLimitExceeded      = errors.New("batch limit exceeded")
	ErrRecordTooLarge     = fmt.Errorf("%w: record too large", ErrLimitExceeded)
)

type Header struct {
	FormatMajor int            `json:"format_major"`
	FormatMinor int            `json:"format_minor"`
	AgentID     string         `json:"agent_id"`
	BootID      string         `json:"boot_id"`                   // boot in which every record was captured (0046)
	ClockOffset *time.Duration `json:"clock_offset_ns,omitempty"` // valid for every record's capture (0046)
	Mode        Mode           `json:"mode"`
}

// RecordSizeError reports a record whose encoded line exceeds the line limit (0044).
type RecordSizeError struct {
	Index int // position in Batch.Records; -1 when returned by CheckRecord
	Size  int // encoded line length in bytes, without the trailing '\n'
	Limit int // DefaultLimits().MaxLineBytes
}

func (e *RecordSizeError) Error() string // "wire: records[<Index>]: encoded record is <Size> bytes, limit <Limit>"; no "records[…]: " when Index < 0
func (e *RecordSizeError) Unwrap() error // returns ErrRecordTooLarge

func NewHeader(agentID, bootID string, mode Mode) Header
func (h *Header) Validate() error

type Batch struct {
	Header  Header
	Records []model.Record
}

func (b *Batch) Validate() error

type Limits struct {
	MaxLineBytes  int
	MaxBatchBytes int64
	MaxRecords    int
}

func DefaultLimits() Limits

type DecodeError struct {
	Line int
	Err  error
}

func (e *DecodeError) Error() string // "wire: line <Line>: <Err>"
func (e *DecodeError) Unwrap() error
```

```go
// internal/wire/encode.go
func EncodeBatch(w io.Writer, b *Batch) error

// CheckRecord validates one agent record and returns the number of bytes its line adds to the
// decompressed batch (including '\n'); *RecordSizeError if the line exceeds the line limit.
func CheckRecord(r *model.Record) (int, error)
```

```go
// internal/wire/decode.go
type Decoder struct{ /* unexported */ }

func NewDecoder(r io.Reader, lim Limits) (*Decoder, error)
func (d *Decoder) Header() Header
func (d *Decoder) Next() (model.Record, error)
func (d *Decoder) Close() error
```

Error wrapping: wire errors are built with `fmt.Errorf("%w: …", ErrX)` or `fmt.Errorf("%w: %w", ErrMalformed,
jsonErr)` so `errors.Is` and `errors.As` (for `*model.FieldError`, `*DecodeError`) both work. A version
error from `Header.Validate` wraps `ErrUnsupportedVersion` and is not a `FieldError`.

## Test files

Per *Layout* in `.squad/stack.md` (one `_test.go` per source file):

- `internal/model/model_test.go` — AC1, AC2, AC3 (and the shared helpers through them)
- `internal/model/metric_test.go` — AC4
- `internal/model/process_test.go` — AC5
- `internal/model/connection_test.go` — AC6
- `internal/model/service_test.go` — AC7
- `internal/model/mariadb_test.go` — AC8
- `internal/model/kernel_test.go` — AC9
- `internal/model/logline_test.go` — AC10
- `internal/model/gap_test.go` — AC11
- `internal/wire/wire_test.go` — AC12, AC13, `DefaultLimits` and `DecodeError` formatting (part of AC18),
  `RecordSizeError` formatting and unwrapping (part of AC24)
- `internal/wire/encode_test.go` — AC14, AC15, AC24 (`CheckRecord`)
- `internal/wire/decode_test.go` — AC16–AC22

Raw gzip/JSON fixtures are built in the test code (small helpers with `t.Helper()` that gzip a string), not
stored under `testdata/`, so every malformed form is visible next to its case. No real clock or network.

Existing test code that calls a changed signature: none (no existing signature changes).

## Documentation updates

All by the Dev:

- `docs/WIRE_FORMAT.md` (new): purpose and scope; stream layout (gzip, JSON Lines, header line, record
  lines); header fields; envelope fields and the meaning of `captured_at`; every record kind with its fields,
  types, units and rules (the tables under *Approach*), including the meaning of every `complete` and
  `truncated` flag; the producer mapping for connection endpoints (wildcard, service-name ports, unmappable
  sockets, unconnected sockets — named for the legacy `lsof -ni` parser, #20); limits and bounds with their
  defaults; the producer size contract (`CheckRecord` at creation, reduction order, the kinds that always
  fit) (0044); the capture-context meaning of `boot_id` and `clock_offset_ns` and the batching rule (0046);
  the batch identity and deduplication key (0045); versioning rules (0043); batch validity and trust rules
  (0044); accepted forms (the table above, in user terms); a short decompressed example batch with one
  record of each of at least three kinds; links to 0042–0046.
- `README.md`: in *Layout*, name `internal/model` and `internal/wire` and link `docs/WIRE_FORMAT.md`.
- `docs/ARCHITECTURE.md` (inside the project block): replace "as of now only the two binaries' `--version`
  exist" with a statement that the binaries' `--version` and the shared data model and wire format
  (`internal/model`, `internal/wire`, not yet used by the binaries) exist; in *Components*, link
  `docs/WIRE_FORMAT.md` from the `internal/` bullet; in *Offline behavior and backfill*, replace "Every batch
  carries an identity and sequence number, so a resend is idempotent and the backend can detect gaps." with
  "Every agent record carries a sequence number from a persistent per-agent counter, and every batch the
  agent's ID; a batch is identified by the agent ID and the sequence numbers of its records, so a resend is
  idempotent (the backend stores each agent ID and sequence number once) and the backend can detect gaps.";
  add records 0042–0044 to the record list of *Components*; in the record list of *Offline behavior and
  backfill*, replace 0018 by 0045 and add 0044 and 0046.
- `.squad/project.md`: *Security areas* 1, replace "a resent batch (same identity and sequence number) is
  stored once and never overwrites stored data. Records 0018, 0032." with "a resent record (same agent ID
  and sequence number), and so a resent batch, is stored once and never overwrites stored data. Records
  0032, 0044, 0045."; *Security areas* 9, "Record 0018." → "Record 0045."; the guarantee *No data gaps
  unless explicitly recorded*, "records 0018, 0028" → "records 0028, 0045"; *Security areas* 10, name the
  concrete code, "the ingest wire format (`internal/wire`: `NewDecoder`, `Decoder.Next`, `wire.Limits`)".
  No other entry changes; *Test doubles* gets no new row (no new external surface).

## Architecture check

- **No data gaps unless explicitly recorded (0018 → 0045, 0028)**: preserved and enabled — the `gap` kind
  exists from format 1.0 with the causes 0028 names; every agent record carries a sequence number, strictly
  increasing within a batch, so the backend can detect missing ranges (#41). Rejecting a whole invalid
  batch (0044) does not lose data silently: the agent keeps it in its spool (#38/#39), and a spool drop is
  itself a `spool_dropped` gap. A record too large for a line is found by `CheckRecord` before it is
  spooled and reduced visibly (`truncated`, `complete` false), so it can neither block the spool nor be
  dropped silently.
- **0018 superseded by 0045**: the spool-and-backfill decision is carried over unchanged; only the
  undefined "batch identity and sequence number" is made precise (agent ID plus per-record sequence
  numbers). No guarantee in `docs/ARCHITECTURE.md` is weakened, so no Product Manager decision is needed.
- **Backfilled data never raises an alert by itself (0022)**: preserved — `mode` is documented and named as
  a hint only; the receive timestamp is set only by the backend and cannot be supplied by the agent (0044).
- **A hanging collector never blocks the agent (0029)**: not touched (no collector code).
- 0005 (Go, shared `internal/`), 0014 (import uses the same model: origin `import`, no sequence number),
  0021 (no pseudonymization: no text field is altered beyond the U+FFFD replacement stated in 0042),
  0007 (SQLite storage is #14; the model has no storage code) — consistent.
- Integration surface *new module*: exported interface under `internal/` (this change); wiring in `cmd/` —
  none yet, by design (consumers #39/#40); test double — none needed (pure functions over `io.Reader` /
  `io.Writer`); component list in `docs/ARCHITECTURE.md` — already names "data model and versioned wire
  format", link added.

## Security considerations

- Area 10 (parsing): the decoder is the guard; every accepted and rejected form is enumerated above and
  each is a test case (AC17). Hostile input yields an error, never a panic: no `panic` in either package,
  payload `Validate` handles nil receivers, all indexing is bounds-safe by construction (range loops).
- Allocation bounds: decompressed bytes (`MaxBatchBytes`, the gzip-bomb bound, counted before the scanner
  sees the bytes), line length (`MaxLineBytes`, enforced by the scanner before `json.Unmarshal`), record
  count (`MaxRecords`), list/map sizes (`MaxItems`) and string lengths. Accepted residual, stated in 0044:
  `encoding/json` allocates list elements before `Validate` can reject an over-long list, so one hostile
  1 MiB line costs tens of MiB transiently; reachable only with a valid agent token (area 1), one line at a
  time. gzip FEXTRA is bounded by the format to 64 KiB and FNAME/FCOMMENT to 511 bytes by `compress/gzip`.
- No hang: the decoder only reads from the caller's reader; CPU per byte is linear (RE2 regular
  expressions, `encoding/json`, `compress/gzip`). Blocking on a slow reader is bounded by the caller (#40:
  server timeouts, `http.MaxBytesReader` on the compressed body).
- Integrity and atomicity: gzip CRC/ISIZE errors surface only at the end of the stream; `Next` returns
  `io.EOF` only after they passed, and the documented contract (0044) is to commit nothing before `io.EOF`.
- Trust: origin and receive time are not on the wire; backend-only gap causes are rejected in agent
  records; the batch `mode` is a hint; `boot_id` and `clock_offset_ns` are agent-supplied context, no more
  trusted than `captured_at` (0046). Not covered here and left to #40 (stated in `docs/WIRE_FORMAT.md`):
  the header's `agent_id` must be checked against the agent the token belongs to, and deduplication by
  (agent ID, sequence number).
- Secrets: none in the format (the token travels in the HTTP request, #40, 0032). Error text of this
  change's own errors: a `FieldError` names the field path and a fixed reason; the only agent-supplied
  text in a path is a map key, rendered with `QuoteName` (quoted, no control byte, cut to `MaxNameBytes`
  with `...`, at most 517 bytes); the wire package's own messages add the line number, the version (an
  integer) and an unknown kind, likewise through `QuoteName`. They never carry a field's value or a whole
  line. Wrapped parser errors are different and carry raw agent text, bounded only by `MaxLineBytes`:
  `time` and `netip` quote the offending string (`parsing time "…"`, `ParseAddr("…")`), and
  `encoding/json` type errors name the Go struct field path **including raw map keys** (verified: `json:
  cannot unmarshal string into Go struct field M.status.evil\nkey<script> of type uint64`, with a real
  newline byte; a 3000-byte key gives a 3078-byte message). `docs/WIRE_FORMAT.md` therefore states that a
  consumer logs or displays decode errors only through the area-12 sanitization (#40). The decoder does not
  rewrite wrapped errors, so `errors.As` (e.g. `*json.UnmarshalTypeError`) still works.
- Zones: no address carries a zone (rejected in the model), so no unbounded zone string is stored; the
  zone text appears only in a `netip` parse error, covered by the previous point.
- No new dependency (0042); `govulncheck` must stay clean.

## Decision records

- `docs/decisions/0042-wire-format-gzip-json-lines-standard-library.md` (Proposed) — encoding and
  compression choice, standard library only, API without `context.Context`.
- `docs/decisions/0043-wire-format-major-minor-versioning.md` (Proposed) — integer major/minor, version
  pre-read, unknown majors rejected, minor additive, unknown keys ignored, unknown kinds rejected.
- `docs/decisions/0044-batch-validated-as-a-whole-agent-records-only.md` (Proposed) — atomic batch,
  agent-only records without origin/receive time on the wire, format limits, the per-record size check
  with the producer reduction contract, and the accepted allocation residual.
- `docs/decisions/0045-batch-identified-by-agent-id-and-record-sequence-numbers.md` (Proposed, supersedes
  0018) — spool and backfill as in 0018; a batch is identified by the agent ID and its records' sequence
  numbers, no batch ID. At approval the Lead sets 0018's status to `Superseded by 0045` (the only edit
  allowed) and updates the index row.
- `docs/decisions/0046-batch-header-describes-the-capture-context.md` (Proposed) — `boot_id` and
  `clock_offset_ns` describe the capture of every record in the batch; batching rule for the agent.

A search for "identity and sequence number" in `docs/` and `.squad/` after the change finds only 0018,
0028 (context, history) and 0045 (which quotes 0018).

## Out of scope / follow-ups

- Wiring into the binaries: the agent's batching and sending (#39) and the ingest API with authentication,
  size/rate limits, `agent_id`-to-token check, deduplication and the receive timestamp (#40); live/backfill
  classification and backend gap detection (#41); storage (#14).
- Agent/backend version compatibility and upgrade order (#85); 0043 states the interim rule (backend first).
- Record kinds not in the issue (backup runs and listener-change events from #36/#33, self-metrics of the
  spool from #38) and a link from a snapshot to the event that triggered it (#36): added later as new kinds
  or optional fields in a minor version (0043).
- Sequence-number scope after an agent loses its persisted counter (reinstall, wiped spool): a resent
  `seq` would be deduplicated away by #40. #38/#40 must keep the counter persistent or rotate the agent ID
  (0045); an optional header field for a counter epoch can be added in a minor version if needed. Named
  here so #38 and #40 plan for it; no follow-up issue, since both issues already exist.
- Producer obligations stated here and in `docs/WIRE_FORMAT.md`, implemented by their own issues: spooling
  boot ID and offset per record and batching by them (#38, #39, 0046); calling `CheckRecord` at creation
  and reducing oversized records (the collectors #31–#36, the spool #38); endpoint mapping for the legacy
  `lsof -ni` output, i.e. wildcard and service-name ports (#20). No follow-up issue: each issue exists and
  its plan reads `docs/WIRE_FORMAT.md`.

## Challenge

Devil's Advocate, round 1 (3 major, 2 minor). All five accepted; scope kept, tier stays `security`.

1. **major — batch identity contradicts accepted 0018**: accepted. The original plan treated 0018's
   sentence as a wording bug; it is a decision, and it was quoted in 0028's context, `ARCHITECTURE.md` and
   security area 1. Of the two fixes offered, a header batch ID was rejected (recorded in 0045, *Options
   considered*): it deduplicates only if the agent re-cuts the spool into identical batches, which it
   cannot promise across restarts or the live/backfill split, and it cannot locate a gap inside a batch.
   Instead 0045 supersedes 0018, carries its spool-and-backfill decision over unchanged and defines the
   batch identity as `agent_id` plus the records' sequence numbers. `ARCHITECTURE.md`, security area 1
   (and the 0018 references in area 9 and the gap guarantee) are updated together (*Documentation
   updates*); 0018's status is set at approval. 0028 is not superseded: its decision does not depend on
   the sentence.
2. **major — valid records can exceed `MaxLineBytes` and block every batch**: accepted. Confirmed by
   arithmetic: 500 threads × 16 KiB `info` ≈ 8 MiB, and 4096 labels of 1 KiB would make even a metric
   exceed 1 MiB. Fix (0044, options a–d): metric labels bounded to `MaxLabels` (32) in the model; new
   `wire.CheckRecord` that producers call at creation, before spooling, returning the line size or a
   `*RecordSizeError` (`ErrRecordTooLarge`, wrapping `ErrLimitExceeded`); `EncodeBatch` reports the same
   error with the record's index; reduction markers `MariaDBThread.truncated` and `MariaDBStatus.complete`
   added, `complete` on both snapshots redefined to cover reductions; the producer contract documented in
   `docs/WIRE_FORMAT.md`. New AC24 and a revised AC15 cover the identifiable error, including the 500 ×
   16 KiB case, and pin the claim that the other five kinds always fit. Raising `MaxLineBytes` to the
   model's worst case was rejected (> 100 MiB per line on a 2 GB server).
3. **major — `boot_id` / `clock_offset_ns` undefined for backfill**: accepted. 0046 fixes both to the
   capture of every record in the batch (corrected time = `captured_at − clock_offset_ns`, offset relative
   to the agent's time-sync reference, omitted = unknown) with the batching rule for #38/#39; a per-record
   boot ID was rejected as redundant on almost every line and kept open as an additive minor change.
   Documented in `docs/WIRE_FORMAT.md` and on the `Header` fields; not unit-testable in the decoder,
   verified under AC23.
4. **minor — AC14 `DeepEqual` vs zero-offset zones; "ends in Z" proves nothing**: accepted. Confirmed:
   RFC 3339 formatting prints `Z` for every zero offset, so the string check could not fail. AC14 now
   uses `time.UTC` times for the `DeepEqual` round trip and a separate case with
   `time.FixedZone("", 0)` / `("X", 0)` compared against the input normalized with `.UTC()`, asserting
   `Location() == time.UTC` on every decoded time (only true if `Z` was written).
5. **minor — legacy `lsof -ni` emits `*` and service names**: accepted, not relaxed. The fields stay
   numeric `netip.AddrPort`; the producer mapping (wildcard → `0.0.0.0` / `::`, service names via a
   built-in table, unmappable sockets left out with `complete` false, unconnected sockets as listeners) is
   stated under *Approach* and in `docs/WIRE_FORMAT.md`, and named for #20 under *Out of scope*.
