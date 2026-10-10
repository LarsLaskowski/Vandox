# Plan: C# decoder reads a JSON null as an absent key, like the Go decoder

Source: Issue #166
Status: Draft
Tier: security — the change is in the parsing of the ingest wire format (`Vandox.Core.Wire.BatchDecoder`, `PayloadRegistry`), *Security areas* 10 in `.squad/project.md`.

## Problem / root cause

`PayloadRegistry.Options` (`src/Vandox.Core/Model/PayloadRegistry.cs:32-35`) sets only `DefaultIgnoreCondition`. System.Text.Json ignores
nullable annotations by default, so a JSON `null` is stored in a non-nullable `string` property, a `null` list element stays a null
reference, and a `null` for a non-nullable value type (`int`, `uint`, `ulong`, `double`, `bool`, `DateTimeOffset`) throws `JsonException`.
The header (`WireHeader`) and the envelope (`Envelope`) are read with the same options.

The null strings then reach the validation helpers in `src/Vandox.Core/Model/Check.cs`: `Pattern` (line 64, `value.Length`), `OptionalName`
(line 96), `RequiredShort` (line 163) throw `NullReferenceException`; `Length` (line 130, `Encoding.UTF8.GetByteCount(null)`, reached via
`Short`/`Text`) throws `ArgumentNullException`; `WireFormat.IsBootId` (`src/Vandox.Core/Wire/WireFormat.cs:62`, `Regex.IsMatch(null)`) throws
`ArgumentNullException`; `ValidateAgentId` (line 47) throws `NullReferenceException`; `PayloadRegistry.TypeOf(null)` (via `"kind":null`)
throws `ArgumentNullException`. A null list element throws `NullReferenceException` in the list loops (`ProcessSnapshot.Validate`,
`ConnectionSnapshot.ValidateList`, `MariaDbStatus.ValidateEntries`). None of them is caught: `BatchDecoder.DecodeRecord`
(`src/Vandox.Core/Wire/BatchDecoder.cs:241-314`) catches only `JsonException` (lines 251, 267, 293) and calls `record.Validate()` (line 306)
outside any `try`; `ReadHeaderAsync` does the same with `header?.Validate()` (line 225). So the exception leaves `NextAsync` and
`OpenAsync`.

The cases were exercised once against the current code (a probe in a scratch copy, Go and C# side by side): of 62 lines with a `null` in one
place, 34 make an exception other than `WireException` escape from the C# decoder, and 13 more are reported as `Malformed` where Go reports
a field error or accepts.

Claims of the issue, checked:

- *`PayloadRegistry.Options` assigns a JSON `null` to non-nullable string properties* — **confirmed**.
- *validation throws `ArgumentNullException` (`Check.Short`, e.g. `"host":null` in a `log_line`)* — **confirmed**.
- *or `NullReferenceException` (`Check.RequiredShort`, `Check.OptionalName`, e.g. `"log":null`, `"event":null`)* — **confirmed**.
- *`DecodeRecord` catches only `JsonException`, so the exception leaves `NextAsync`, whose contract is `WireException`* — **confirmed**; the
  same happens in `OpenAsync` for the header (`"agent_id":null` → `NullReferenceException`, `"boot_id":null` → `ArgumentNullException`),
  which the issue does not name.
- *the Go decoder ignores such a `null`* — **confirmed** for a key given once: the field keeps its zero value, a pointer, list or map is set to
  nil, and the record's validation decides. With duplicate keys Go keeps the earlier value of a scalar when the later one is `null` (see
  *Out of scope*).
- *a nil slice without `omitempty` encodes as `null`* — true of `encoding/json`, but **no field is affected**: every slice and map in
  `internal/model` is tagged `omitempty`, every pointer `omitempty`, optional times `omitzero`, the header's `ClockOffset` `omitempty`, and
  `EncodeBatch`/`CheckRecord` validate first (`Data` non-nil). The Go encoder never writes `null`; the golden batch
  `testdata/wire/all-kinds.jsonl` contains none.

Further defects found on the way, in the same area:

- A `null` list element in C# would, even without the crash, leave a null reference in a decoded record (`"threads":[null]` is a *valid*
  record in Go: one thread with id 0).
- A `null` map value: `"labels":{"a":null}` and `"variables":{"A":null}` throw `ArgumentNullException`; `"status":{"A":null}` is `Malformed`
  in C#, accepted as 0 in Go.
- A record line that is `null` is `Malformed` in C# (`BatchDecoderTests` pins it), `ErrUnknownKind` in Go and in the area document's row
  *Line is `null`*.
- Not null-related, split off (see *Out of scope*): C# matches keys case-sensitively, classifies a header `[1]` or `"format_major":"1"` as
  `UnsupportedVersion` instead of `Malformed`, and replaces repeated keys where Go merges them.

## Acceptance criteria

The rule (record 0089): **a JSON `null` reads as if its key were absent**, in the header, the record line and the payload at any depth; a
`null` list element reads as an element with every field at its zero value (as `{}` would); a `null` map value reads as the zero value of the
map's value type (`""` for `labels` and `variables`, `0` for `status`); a record line that is `null` reads as an object without keys. The
record's validation then decides.

- [ ] AC1 (no escape): for every line of `testdata/wire/all-kinds.jsonl` (header and nine records) and every key at any depth of that line,
  the line with that one key's value replaced by `null` makes `BatchDecoder.OpenAsync` (header) or `NextAsync` (record) either succeed or
  throw `WireException` — never another exception. The same for `"clock_offset_ns":null` in the header (not in the fixture).
- [ ] AC2 (null equals absent): for each line of AC1 whose key is an object member outside a map, the outcome equals the outcome of the same
  line with that key removed: both succeed with an equal result (header fields equal; record `Source`, `Seq`, `CapturedAt`, `Kind` equal
  and `PayloadRegistry.Serialize(Data)` equal), or both throw `WireException` with equal `Kind`, `Line`, `FieldError?.Field` and
  `FieldError?.Reason`.
- [ ] AC3 (list element): for every list in the fixture (`data.processes`, `data.programs`, `data.states`, `data.processes` of
  `connection_snapshot`, `data.remotes`, `data.listeners`, `data.connections`, `data.threads`), the line with element 0 replaced by `null`
  has the same outcome as the line with element 0 replaced by `{}` (comparison as in AC2), and a decoded record never holds a null element:
  `"threads":[null]` on an `up` status decodes to one `MariaDbThread` that is not null, with `Id` 0 and every string empty.
- [ ] AC4 (map value): for every map entry in the fixture (`data.labels`, `data.status`, `data.variables`), the line with that entry's value
  replaced by `null` has the same outcome as with the value replaced by `""` (labels, variables) or `0` (status); in particular
  `"labels":{"device":null,...}` decodes with `Labels["device"] == ""`, and `{"availability":"down","status":{"A":null},"complete":true}` is
  rejected with field `data.availability`, reason `status, variables and threads must be empty unless up`.
- [ ] AC5 (exact outcomes, one `DataRow` each; header `H` = `BatchBuilder.Header` with the one change, record lines with `source` `s`, `seq`
  1, a valid `captured_at`): the results in the table *Expected outcomes* below, compared by `Kind`, `Line` and, for `Invalid`, the exact
  `FieldError.Field` and `FieldError.Reason`; for accepted lines the named property value.
- [ ] AC6 (record line `null`): a record line `null` throws `WireException` with `Kind` `UnknownKind`, `Line` 2 (the existing `DataRow("null",
  WireErrorKind.Malformed)` in `BatchDecoderRefusesBadRecord` changes to `UnknownKind`); a header line `null` stays `UnsupportedVersion`,
  line 1.
- [ ] AC7 (type errors unchanged): a non-null value of the wrong type is still `Malformed`: `"name":5`, `"pid":"1"` in a `log_line`,
  `"processes":{}`, `"processes":[5]`, `"labels":{"a":5}`, `"status":{"A":"x"}`, plus the existing rows (`"seq":-1`, `"format_minor":"x"`,
  `"captured_at":"yesterday"`).
- [ ] AC8 (writing unchanged): `PayloadRegistry.Serialize` writes the same JSON as before for every payload; the golden contract test and the
  existing round-trip tests stay green, and a `NullAsAbsentConverterFactoryTests` case pins the exact JSON of a payload with labels, a list,
  nullable numbers, an address and an empty optional string (e.g. a `MetricPoint` and a `ConnectionSnapshot`, expected text in the test).
- [ ] AC9 (converter units): `NullAsAbsentConverterFactory.CanConvert` is true for `string`, `bool`, `byte`, `short`, `int`, `uint`, `long`,
  `ulong`, `double`, `DateTimeOffset` and `List<T>` of a class with a public parameterless constructor (e.g. `List<ProcessSample>`), and
  false for `int?`, `DateTimeOffset?`, `List<string>`, `Dictionary<string, string>`, `JsonElement`, `IPAddress`, `OomKill`. A
  `NullAsAbsentConverter<T>` returns its absent value for a `null` token and the inner converter's value otherwise, and reads and writes a
  dictionary key through the inner converter. A `NullElementListConverter<T>` reads `[null, {...}]` as two non-null elements and throws
  `JsonException` for a token that is not an array.
- [ ] AC10 (Go parity, pinning): `internal/wire/decode_test.go` gets the AC2/AC3/AC4 equivalences over the same fixture lines and the rows
  of the table below, with the same field paths and reasons. Go production code does not change, so these tests pass on the current code;
  they pin the rule for the Go side (AC1-AC9 are the failing C# tests of step 5).

### Expected outcomes (AC5)

Verified once with the Go decoder; the C# prototype in a scratch copy gave the same result for every row.

| Line | Result |
| ---- | ------ |
| header `"agent_id":null` | `Invalid`, line 1, `agent_id`: `must be 1 to 64 characters of [A-Za-z0-9._-], starting with a letter or digit` |
| header `"boot_id":null` | `Invalid`, line 1, `boot_id`: `must be a lower-case UUID` |
| header `"mode":null` | `Invalid`, line 1, `mode`: `must be live or backfill` |
| header `"format_minor":null` | accepted, `Header.FormatMinor` 0 |
| header `"clock_offset_ns":null` | accepted, `Header.ClockOffsetNs` null |
| `"kind":null` | `UnknownKind`, line 2 |
| `"source":null` | `Invalid`, `source`: `required` |
| `"seq":null` | `Invalid`, `seq`: `must be greater than 0 for origin agent` |
| `"captured_at":null` | `Invalid`, `captured_at`: `required` |
| metric `"name":null` | `Invalid`, `data.name`: `required` |
| metric `"value":null` | accepted, `Value` 0 |
| metric `"unit":null` | accepted, `Unit` empty |
| log_line `"log":null` | `Invalid`, `data.log`: `required` |
| log_line `"host":null`, `"program":null`, `"event":null`, `"message":null` | accepted, the property empty |
| log_line `"pid":null`, `"truncated":null`, `"priority":null` | accepted, 0 / false / null |
| process_snapshot `"processes":[null]` | `Invalid`, `data.processes[0].pid`: `must be greater than 0` |
| process_snapshot `"programs":[null]` | `Invalid`, `data.programs[0].program`: `required` |
| process `"command":null` | `Invalid`, `data.processes[0].command`: `required` |
| process `"user":null`, `"cmdline":null`, `"state":null`, `"started_at":null` | accepted |
| connection_snapshot `"states":[{"proto":null,"count":1}]` | `Invalid`, `data.states[0].proto`: `unknown value` |
| connection_snapshot `"states":[{"proto":"udp","state":null,"count":1}]` | accepted |
| connection_snapshot `"processes":[{"pid":1,"command":null,"count":1}]` | `Invalid`, `data.processes[0].command`: `required` |
| connection_snapshot `"remotes":[{"addr":null,"count":1}]` | `Invalid`, `data.remotes[0].addr`: `invalid address` |
| connection_snapshot `"listeners":[{"proto":"tcp","local":"1.2.3.4:1","command":null}]` | accepted |
| service_state `"unit":null` | `Invalid`, `data.unit`: `required` |
| service_state `"sub_state":null`, `"active_enter_at":null`, `"restarts":null` | accepted |
| mariadb_status `"threads":[null]` (up) | accepted, one thread, `Id` 0 |
| mariadb_status `"variables":{"A":null}` (up) | accepted, `Variables["A"]` empty |
| mariadb_status `"status":{"A":null}` (up) | accepted, `Status["A"]` 0 |
| kernel_event `"oom_kill":{"victim_pid":1,"victim_command":null}` | `Invalid`, `data.oom_kill.victim_command`: `required` |
| kernel_event `"boot":{"boot_id":null}` | `Invalid`, `data.boot.boot_id`: `required` |
| kernel_event `"message":null` (valid oom_kill) | accepted |
| gap `"from":null` | `Invalid`, `data.from`: `required` |
| gap `"collector":null` (cause `unknown`) | accepted |

## Approach

C#: register one converter factory in `PayloadRegistry.Options.Converters` that maps a `null` token to the absent value and leaves every
other token to the built-in converter:

- `NullAsAbsentConverter<T>` for `string` (absent: `string.Empty`) and the non-nullable value types the model uses (`bool`, `byte`, `short`,
  `int`, `uint`, `long`, `ulong`, `double`, `DateTimeOffset`; absent: `default`). `HandleNull` is true; a non-null token goes to the inner
  converter, obtained as `(JsonConverter<T>)JsonSerializerOptions.Default.GetConverter(typeof(T))` (supported API; not
  `JsonMetadataServices`, which is reserved for the source generator). `Write`, `ReadAsPropertyName` and `WriteAsPropertyName` delegate to
  the inner converter, so dictionary keys and the written JSON are unchanged. Nullable value types need nothing: System.Text.Json's nullable
  wrapper returns `null` for a `null` token before it calls the converter of `T`.
- `NullElementListConverter<TItem>` for `List<TItem>` with `TItem : class, new()`: reads the array element by element, a `null` element as
  `new TItem()`, every other element with `JsonSerializer.Deserialize<TItem>(ref reader, options)`; a token that is not `StartArray` throws
  `JsonException`. It writes the elements with `JsonSerializer.Serialize`. A `null` list itself stays `null` (`HandleNull` false), as in Go.
- Not claimed, and already right: reference-type sub-objects (`OomKill?`, `Boot?`) and dictionaries stay `null`; `IPAddress?` and
  `IPEndPoint?` (attribute converters) stay `null` and fail validation with `invalid address`; `JsonElement?` (`Envelope.Data`) stays
  handled by the decoder's `data` check.

`BatchDecoder.DecodeRecord`: a root of kind `Null` is decoded as an empty `Envelope` (kind `""` → `UnknownKind`), arrays, strings and numbers
stay `Malformed` (`record is not a JSON object`). The `record is empty` branch becomes unreachable and is removed. No catch-all is added: the
converters make every property tolerate `null`, and AC1 checks that over every key of every kind.

The approach was prototyped in a scratch copy: with it, every line of a second probe run through both decoders (85 lines: a `null` in each
field position of every kind, map keys, type errors, quoted `"null"`, `NULL`, `null` inside unknown keys, duplicate keys) gives the same
result as the Go decoder except the six duplicate-key lines (*Out of scope*), and the whole C# test suite stays green except the `DataRow("null", Malformed)` that AC6 changes.

Go: no production change; the rule is pinned by tests (AC10).

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| `src/Vandox.Core` | `Model/NullAsAbsentConverterFactory.cs` | new |
| `src/Vandox.Core` | `Model/NullAsAbsentConverter.cs` | new |
| `src/Vandox.Core` | `Model/NullElementListConverter.cs` | new |
| `src/Vandox.Core` | `Model/PayloadRegistry.cs` | `Options` adds the factory to `Converters` (step 6, not in the skeleton) |
| `src/Vandox.Core` | `Wire/BatchDecoder.cs` | `DecodeRecord`: `null` root as empty envelope, `record is empty` branch removed (step 6) |
| `tests/Vandox.Core.Tests` | `BatchDecoderTests.cs`, three new test files | see *Test files* |
| Go `internal/wire` | `decode_test.go` | pinning tests (AC10) |
| docs | `docs/areas/wire-format.md` | see *Areas* |

## Signatures (for the Dev's skeleton)

All `internal`, namespace `Vandox.Core.Model`, one type per file, `#region` blocks and XML documentation as usual. The skeleton adds only
the three types; registering the factory in `PayloadRegistry.Options` and the `BatchDecoder` change are step 6 (a registered skeleton factory
would throw on every deserialization and break the whole suite, including tests that must stay green).

```csharp
// src/Vandox.Core/Model/NullAsAbsentConverterFactory.cs
internal sealed class NullAsAbsentConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert);
    public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options);
}

// src/Vandox.Core/Model/NullAsAbsentConverter.cs
internal sealed class NullAsAbsentConverter<T> : JsonConverter<T>
{
    internal NullAsAbsentConverter(JsonConverter<T> inner, T absent);
    public override bool HandleNull { get; }
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options);
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options);
    public override T ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options);
    public override void WriteAsPropertyName(Utf8JsonWriter writer, T value, JsonSerializerOptions options);
}

// src/Vandox.Core/Model/NullElementListConverter.cs
internal sealed class NullElementListConverter<TItem> : JsonConverter<List<TItem>>
    where TItem : class, new()
{
    public override List<TItem>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options);
    public override void Write(Utf8JsonWriter writer, List<TItem> value, JsonSerializerOptions options);
}
```

`PayloadRegistry.Options` and `BatchDecoder` keep their signatures. Existing files the skeleton rewrites: none.

## Test files

- `tests/Vandox.Core.Tests/BatchDecoderTests.cs` (extend): AC1-AC7 — the fixture-driven equivalences (read `testdata/wire/all-kinds.jsonl`
  through `RepositoryFiles.Path`, edit lines with `System.Text.Json.Nodes`, compress with `BatchBuilder.Gzip`), the `DataRow`s of the table,
  the changed `null` row.
- `tests/Vandox.Core.Tests/NullAsAbsentConverterFactoryTests.cs` (new): AC8, AC9 (`CanConvert`, `CreateConverter`).
- `tests/Vandox.Core.Tests/NullAsAbsentConverterTests.cs` (new): AC9 (`null` token, non-null token, property names, `Write`).
- `tests/Vandox.Core.Tests/NullElementListConverterTests.cs` (new): AC9 (`null` element, mixed list, not an array, `Write`).
- `internal/wire/decode_test.go` (extend): AC10.

Existing test code that calls a changed signature: none. One existing assertion changes because the behavior changes on purpose: the
`DataRow("null", WireErrorKind.Malformed)` of `BatchDecoderRefusesBadRecord` becomes `UnknownKind` — the Tester, in step 5.

## Areas

`docs/areas/wire-format.md` (owner: **Dev**, step 6):

- *Common rules*: a new bullet **Null** with the rule of the acceptance criteria (absent at any depth; list element = all fields zero;
  map value = zero value of the value type, `""` for `labels` and `variables`, `0` for `status`; a record line `null` = an object without
  keys; validation decides, so a missing required field is a field error, never `ErrMalformed`; the Go encoder never writes `null`), linking
  0089 as `../decisions/0089-json-null-reads-as-an-absent-key-in-both-decoders.md`.
- *Accepted forms*, three new rows after *Unknown keys at any depth*:
  - `null` as the value of a field (header, record or payload) — read as absent: accepted where the field is optional (`"host":null`,
    `"format_minor":null`; `"value":null` reads 0), rejected with the field error of the missing field where it is required (`"log":null` →
    `data.log: required`, `"seq":null` → `seq: must be greater than 0 for origin agent`, `"addr":null` → `invalid address`);
  - `null` as a list element — an element with every field at its zero value: `"threads":[null]` accepted, `"processes":[null]` →
    `data.processes[0].pid: must be greater than 0`;
  - `null` as a map value — the zero value of the value type: `"labels":{"a":null}` accepted with an empty value, `"status":{"Uptime":null}`
    reads 0.
  The existing rows *Line is `null`*, *`format_major` missing, `null`, ...* and *`data` missing or `null`* stay as they are (now true for the
  C# decoder as well).
- *Implementation*: name `NullAsAbsentConverterFactory` (with its two converters) in `Vandox.Core.Model` as the C# side of the null rule.
- *Related decisions*: add 0089.

## Documentation updates

- `docs/areas/wire-format.md` — as above, owner Dev.
- `docs/decisions/0089-json-null-reads-as-an-absent-key-in-both-decoders.md` and its index row — written by the Lead in this step (`Proposed`).
- `README.md`, `docs/ARCHITECTURE.md`, `.squad/project.md`: none (no option, no guarantee, flow or security-area entry changes; area 10
  already names `BatchDecoder`).

## Architecture check

No guarantee in `docs/ARCHITECTURE.md` or `.squad/project.md` is touched. The change strengthens *Security areas* 10 (hostile input yields an
error or a record, never a crash) and record 0075 (the C# decoder keeps the Go decoder's rules). Accepting a `null` gives a producer nothing
that omitting the key does not; batch validation as a whole (0044), versioning (0043: unknown keys, also with `null`, stay ignored) and the
golden contract (0075; the fixture has no `null`) are unchanged.

## Security considerations

- [x] Limits: no new limit. `MaxLineBytes` still applies to the raw decompressed line before JSON parsing. A `null` list element allocates one
  empty element; `null,` is 5 bytes where `{},` is 3, so a line of `null` elements allocates fewer objects than a line of empty objects already
  does, and the per-decoder peak in the area document stays valid. `MaxItems` is still checked by the validation after reading, as today.
- [x] Exceptions reaching a user-visible message: after the change only `WireException` leaves `OpenAsync`/`NextAsync` for a `null`; its
  message is fixed text, e.g. `wire: line 2: model: data.log: required` or `wire: line 2: unknown record kind ""`. No runtime exception text
  (`NullReferenceException`, `ArgumentNullException`) reaches a caller any more; `JsonException` texts stay replaced by the decoder's fixed
  reasons as today.
- [x] Guard against bypasses — the forms the parser accepts for "no value", enumerated from System.Text.Json and checked against the rule:
  the literal `null` (only lower case is valid JSON; `Null`, `NULL`, `nil`, `undefined` are invalid JSON → `Malformed`, as today and in Go);
  `null` as an object member at any depth (header, envelope, payload, sub-objects, list elements' members) → absent; `null` as an array
  element → empty element (a list of objects) — the model has no list of scalars; `null` as a dictionary value → zero value; `null` as the
  whole line → header `UnsupportedVersion`, record `UnknownKind`; `null` inside unknown keys → ignored, as today; a key that is
  `null` is not JSON. A quoted `"null"` is a string, not a null: it stays a string (`"kind":"null"` → `UnknownKind`, `"addr":"null"` →
  `Malformed`), as in Go. Escaped forms (`null`) exist only inside strings. AC1 covers every key of every kind; AC3 every list; AC4
  every map.

## Decision records

- `docs/decisions/0089-json-null-reads-as-an-absent-key-in-both-decoders.md` (Proposed) — why `null` reads as absent rather than malformed,
  and why the C# side is a converter, not a catch-all.

## Out of scope / follow-ups

Duplicate keys combined with `null` (Go keeps the earlier scalar, C# takes the later `null`) are not covered by the rule; they belong to the
follow-up below. Proposed follow-up issue for the orchestrator to open:

- **Title:** `[Wire] C# decoder diverges from the Go decoder on key case, duplicate keys and the header's shape`
- **Body:** Found while planning #166 (both decoders exercised with the same lines). (1) The C# decoder matches keys case-sensitively
  (`PayloadRegistry.Options` has no case-insensitive matching): `{"Kind":"metric",...}` is `UnknownKind`, a header with `FORMAT_MAJOR` is
  `UnsupportedVersion`, while Go and the *Accepted forms* rows *Keys in other case* and *Unicode case-fold equivalents* accept them as the
  field. (2) A header `[1]`, `{"format_major":"1"}`, `1.0` or `1e0` is `UnsupportedVersion` in C# (`BatchDecoder.ReadMajor`), `ErrMalformed`
  in Go and in the area document; `BatchDecoderTests.BatchDecoderRefusesBadHeader` pins the C# result. (3) Duplicate keys: Go merges a
  repeated object or map (`"labels":{"a":"1"},"labels":{"b":"2"}` gives both labels), decodes a repeated list into the elements of the first
  (fields of the first occurrence survive), and keeps the earlier value of a scalar when the later occurrence is `null`; C# replaces; the
  area document says *the last one wins*. Decide one rule per point (for duplicates, rejecting them is an option), align both decoders, the
  area document and both test suites. Must be settled before the ingest API (#40) calls `BatchDecoder`.
