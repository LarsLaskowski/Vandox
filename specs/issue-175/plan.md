# Plan: Align both wire decoders on key case, duplicate keys, nesting depth and the header's shape

Source: Issue #175
Status: Draft
Tier: security — the change rewrites how both wire decoders parse external input (`.squad/project.md`, *Security areas* 10:
`Vandox.Core.Wire.BatchDecoder`, `internal/wire` `NewDecoder` / `Decoder.Next`).

## Problem / root cause

Both decoders leave JSON-level details to their standard library, and the libraries disagree:

- Go binds every line with `encoding/json` (`internal/wire/decode.go:134` and `:144` header, `:214` envelope, `:225` payload), which
  matches keys with Unicode case folding and handles repeated keys by replacing scalars, merging objects and maps, and decoding a repeated
  list into the elements of the first occurrence; it accepts nesting up to 10,000 levels.
- C# binds with System.Text.Json through `PayloadRegistry.Options` (`src/Vandox.Core/Model/PayloadRegistry.cs:32`), which matches keys
  exactly and replaces repeated keys; `BatchDecoder.ReadMajor` (`src/Vandox.Core/Wire/BatchDecoder.cs:159`) looks `format_major` up exactly
  and returns "missing" for any header that is not an object and `int.MinValue` for any number that is not a 32-bit integer, so every
  misshapen header becomes `UnsupportedVersion` (`:207`); `JsonDocument.Parse` (`:198`, `:247`) limits nesting to 64 by default;
  `WireHeader.FormatMinor` (`src/Vandox.Core/Wire/WireHeader.cs:24`) is a 32-bit `int`.

Verified by running the same lines through both decoders in a scratch copy (a Go test and a C# console program over one case file; 75
cases):

| Claim of the issue | Result |
| ------------------ | ------ |
| 1. C# matches keys case-sensitively: `{"Kind":...}` is `UnknownKind`, a header with `FORMAT_MAJOR` is `UnsupportedVersion`; Go and the area rows accept them | **Confirmed** (also `{"KIND",...,"DATA":{"NAME"}}`, nested `"PID"`, `Kind` with U+212A, `ſeq`; a header `{"format_major":2,"Format_Major":1}` is accepted by Go and `UnsupportedVersion` in C#) |
| 2. A header `[1]`, `{"format_major":"1"}`, `1.0` or `1e0` is `UnsupportedVersion` in C# (`ReadMajor`) but `Malformed` in Go and the area document; `BatchDecoderRefusesBadHeader` pins the C# result | **Confirmed** (`BatchDecoderTests.cs:86-87`). Same for a header `5`, `"x"`, `[]`, and for a major `true`, `{}`, `[1]`, `2.5`, `"2"`, `1e400`, `100000000000000000000` |
| 3. Go merges a repeated object or map, decodes a repeated list into the elements of the first occurrence, keeps the earlier scalar when the later one is `null`; C# replaces; the area doc says "the last one wins" | **Confirmed** (`oom_kill` twice: Go merges, C# field error; `labels` twice: Go keeps both maps' keys; `processes` twice: a `user` of the first element survives in Go; `"name":"cpu","name":null`: Go keeps `cpu`, C# `data.name: required`). Refinement: a later `null` keeps only a scalar in Go; it clears a list, map or object (pointer) |

Related defects found on the way (all confirmed in the scratch run):

- **Nesting depth:** C# rejects a line nested deeper than 64 (`Malformed`), Go accepts up to 10,000 — included here (AC9).
- **`format_minor` range:** `3000000000` is accepted by Go and `Malformed` in C# (`int`) — included here (AC10).
- **Invalid UTF-8 and unpaired surrogate escapes in string values:** C# reports `Malformed` (`"message":"a\xffb"`, `"unit":"\ud800"`,
  `"kind":"\ud800"`, `"agent_id":"\ud800"`), Go replaces them with U+FFFD, and record 0042 plus the area row "Strings with invalid UTF-8 ...
  replaced by U+FFFD" say replace. Not included: it touches 0042 and the storage rule of 0063; follow-up issue F1, an explicit blocker of
  the ingest API #40 (the area document says so under *Duties of the ingest API* and marks the C# behavior in *Common rules* and
  *Accepted forms*, so it is no longer false for C#). Keys are covered here, because such a key is not ASCII.
- **`Kind` in stored payloads:** `[JsonIgnore]` on `IPayload.Kind` is not inherited by the implementing properties, so
  `PayloadRegistry.Serialize` writes `"Kind":"<kind>"` into every JSON payload the store writes (`BatchWriter.cs:103`; pinned by
  `BatchDecoderTests.cs:680-681`). Not part of the wire; follow-up issue F2.

## Decisions (one rule per point, record 0090)

1. **Key case:** every key is ASCII after unescaping, and a key matches a field when equal ignoring ASCII case — in both decoders. C# turns
   on case-insensitive binding; a non-ASCII key is `ErrMalformed`, which removes the folds where the libraries differ (Go folds U+212A and
   U+017F, System.Text.Json's case-insensitive mode folds neither). Exact matching was rejected: Go's `encoding/json` cannot do it,
   `encoding/json/v2` is behind `GOEXPERIMENT=jsonv2` in Go 1.27 (verified: `//go:build goexperiment.jsonv2` in the toolchain), a JSON
   dependency contradicts 0042, and a schema-free rule cannot tell a misspelled field from a map key such as `Uptime`.
2. **Duplicate keys:** two keys of one object equal ignoring ASCII case, at any depth, in maps and unknown keys too, make the line
   `ErrMalformed` in both decoders, also when one of them is `null`.
3. **Header shape:** `ErrMalformed` when the header is not a JSON object or `null`, or `format_major` is present, not `null` and not a JSON
   integer from -2^63 to 2^63 - 1; `ErrUnsupportedVersion` when it is missing, `null` or another integer (Go's behavior, the area document,
   0043). `format_minor` is an integer from 0 to 2^63 - 1 in both.
4. **Nesting depth:** at most 64 in both (the outermost value counts as 1), else `ErrMalformed`.

The line rules (1, 2, 4) run on every line, the header included, before anything is bound — so before the major is read.

None of this is a product decision: the only producer, the Go encoder, writes ASCII lower-case keys once each and nests at most five
levels (checked against `testdata/wire/all-kinds.jsonl`), no guarantee of `docs/ARCHITECTURE.md` changes, and the wire stays 1.0.

## Acceptance criteria

The shared fixture (AC1) is the equivalence proof; per-language unit tests cover what the fixture cannot express (raw invalid bytes, error
texts, allocation, model rules).

- [ ] AC1 **Shared decoder cases.** A new fixture `testdata/wire/decoder-cases.json` (format below) holds at least every case listed under
  *Fixture cases*. Go `TestDecoder_SharedCases` (`internal/wire/decode_test.go`) and C# `WireContractTests.DecodeSharedCaseGivesExpectedResult`
  (one data row per case) gzip each case, decode it to the end and pass every case. Both fail on a case whose `want.error` is not one of the
  seven names, on duplicate case names, and when the line under test of K4, K5, N4 or D9 holds no backslash after the fixture is read
  (the escaped spelling was lost; see *Fixture spellings*).
- [ ] AC2 **Keys in other ASCII case are the field** in the header, the record line and the payload at any depth, also escaped
  (fixture spellings `\\u006bind` and `\\u004BIND`), in both decoders (fixture cases K1-K6).
- [ ] AC3 **A non-ASCII key is `ErrMalformed`** at its line, whatever the object (header, record, payload, list element, map, unknown key):
  raw U+212A, U+017F, U+0131, U+00E9, and the escapes (fixture spellings) `\\u212aind`, the surrogate pair
  `\\ud83d\\ude00`, the unpaired surrogates `\\ud800` and `a\\udc00`, a high surrogate followed by a non-surrogate `\\ud800\\u0041`
  (fixture cases N1-N11). In addition, per language (raw bytes cannot be written into the JSON fixture): a key with the raw byte `0xFF`, and
  one with an overlong encoding (`0xC0 0xAF`), in a record and in the header, is `ErrMalformed` — Go in `decode_test.go`, C# in
  `BatchDecoderTests.cs`.
- [ ] AC4 **Duplicate keys are `ErrMalformed`** at their line: exact repeat of a scalar, an object, a map, a list; a scalar repeated with
  `null`; keys that differ only in ASCII case; a key and its escaped spelling; a repeated map key; map keys `Mount`/`mount`; a repeated
  unknown key; a repeat inside a nested unknown value; in the header (`format_major` twice, with `null`, `format_major`/`Format_Major`)
  (fixture cases D1-D17). The same key in sibling objects or at another depth is accepted (D18, D19).
- [ ] AC5 **Header shape.** Fixture cases H1-H31: a header `[1]`, `[]`, `5`, `"x"`, `true` is `Malformed`; `null` and `{}` are
  `UnsupportedVersion`; `format_major` `"1"`, `"2"`, `1.0`, `1e0`, `1E0`, `10e-1`, `2.5`, `true`, `{}`, `[1]`, `9223372036854775808`,
  `-9223372036854775809`, `1e400` is `Malformed`; `null`, missing, `0`, `-0`, `-1`, `2`, `3000000000`, `9223372036854775807`,
  `-9223372036854775808` is `UnsupportedVersion`; a major 2 with an incompatible rest is `UnsupportedVersion`; a major 1 with
  `"agent_id":{"x":1}` is `Malformed`; all at line 1.
- [ ] AC6 **Line rules come before the major:** a header with major 2 and a duplicate key, a non-ASCII key, or nesting 65 is `Malformed`
  (fixture cases O1-O3).
- [ ] AC7 **Order inside a batch:** a batch whose second record has a duplicate key returns the first record, then `Malformed` at line 3
  (fixture case O4); the error is sticky (existing sticky-error tests stay green).
- [ ] AC8 **No exception other than `WireException`** leaves `BatchDecoder.OpenAsync` / `NextAsync` for any fixture case or AC3 line; in
  particular the `InvalidOperationException` System.Text.Json throws when a key with an unpaired surrogate or invalid UTF-8 is read is mapped
  to `Malformed` (C#, `BatchDecoderTests.cs`; the fixture runner asserts `WireException`).
- [ ] AC9 **Nesting:** a record line and a header nested exactly 64 deep are accepted, 65 deep `Malformed`, also when the depth sits in an
  unknown key (fixture cases X1-X4). Go `wire.MaxDepth` and C# `WireFormat.MaxDepth` are 64 (a test asserts the constant in each language).
- [ ] AC10 **`format_minor`:** `3000000000` and `9223372036854775807` are accepted and read back exactly (Go `Header().FormatMinor`, an
  `int64`; C# `Header.FormatMinor`, a `long`); `9223372036854775808` and `"0"` are `Malformed`; `-1` is `Invalid`, field `format_minor`,
  reason `must not be negative` (fixture cases M1-M5). These cases and the major cases H27-H29 hold on every Go target, not only where
  `int` has 64 bits: because CI runs only `amd64`, the Tester also runs `GOARCH=386 go test ./internal/wire/ -count=1` (no `-race`; it
  runs natively on this x86-64 host, checked) in its coverage step and reports that it passes.
- [ ] AC11 **Error texts carry no key.** Exact texts: Go `wire: line <n>: malformed batch: duplicate key`,
  `wire: line <n>: malformed batch: key not ASCII`, `wire: line <n>: malformed batch: nesting deeper than 64`; C#
  `WireException.Reason` `duplicate key`, `key not ASCII`, `header is not a JSON object`, `format_major is not an integer` (a line nested
  deeper than 64 keeps the existing reasons `header is not valid JSON` / `record is not valid JSON`). A test per language uses a sentinel
  key (`SENTINELKEY` twice, and `SENTINELKÉY`) and compares the exact text.
- [ ] AC12 **The Go producer never writes what the decoders reject.** `MetricPoint.Validate` refuses labels, and `MariaDBStatus.Validate`
  refuses `status` and `variables` keys, that are equal ignoring ASCII case: `*model.FieldError`, field `labels["mount"]` for
  `{"Mount":"a","mount":"b"}` (the later key in byte order is named), `status["Uptime"]` for `{"UPTIME":1,"Uptime":2}`,
  `variables["max_connections"]` for `{"MAX_CONNECTIONS":"1","max_connections":"2"}`, reason `duplicate key ignoring case`; keys that
  differ in more than case stay valid (`internal/model/metric_test.go`, `internal/model/mariadb_test.go`). `wire.CheckRecord` returns that
  error and `wire.EncodeBatch` returns it and writes nothing (`internal/wire/encode_test.go`). The golden batch is unchanged and still
  decodes in both languages. A refused record must not vanish silently: the area document (*Producer size contract*) makes the collector
  record a `gap` (cause `unknown`, `collector` set) for it. No collector exists yet, so this change has no code for it; the rule is checked
  by the Reviewer in the area document and becomes a test in each collector's own issue.
- [ ] AC13 **C# binding ignores ASCII case everywhere it binds wire data** (`PayloadRegistry.Options`), and a reflection test in
  `BatchDecoderTests.cs` asserts for `WireHeader`, `Envelope` and every payload type with the types reachable from it: each public
  settable property has a `[JsonPropertyName]` (or `[JsonIgnore]`), and no two JSON names of one type are equal ignoring ASCII case.
- [ ] AC14 **Memory, C#.** For a batch whose record line is about 1 MiB of one of four shapes in an unknown key — a list of `{"a":0}`, one
  object of distinct short keys, a list of `[]`, a list of `0` — `OpenAsync` plus `NextAsync` allocate at most 20 times the line's length
  (`GC.GetTotalAllocatedBytes(true)`, in Debug and Release; measured today: 6.5 to 11.9 times; a prototype of the check added at most 4
  times). Also a line of 1 MiB of distinct keys with a duplicate at the end is `Malformed`. In `BatchDecoderTests.cs`.
- [ ] AC15 **Memory, Go.** For the same four shapes, `NewDecoder` plus `Next` allocate at most 64 times the line's length
  (`runtime.MemStats.TotalAlloc`; measured today 2 times, a `Decoder.Token` prototype of the check added at most 37 times). In
  `decode_test.go`.
- [ ] AC16 **Existing rows move to the new rule** (see *Test files*); every other existing test in both languages stays green unchanged.

### Fixture format

`testdata/wire/decoder-cases.json`, written by the Tester:

```json
{
  "cases": [
    {
      "name": "K2 record keys in other case",
      "lines": ["<header line>", "<record line>"],
      "want": { "records": [ { "kind": "metric", "seq": 4 } ] }
    },
    {
      "name": "D1 seq twice",
      "lines": ["<header line>", "<record line>"],
      "want": { "error": "Malformed", "line": 2 }
    }
  ]
}
```

- `lines`: JSON strings; each is encoded as UTF-8, the lines are joined with `\n`, a final `\n` is added, and the result is compressed as one
  gzip member. A JSON escape in the fixture string is resolved by the fixture reader, so a line that must contain a JSON escape writes it
  with a doubled backslash: `\\u212a` in the fixture is a single backslash followed by `u212a` in the batch line. A raw non-ASCII
  character of a line is written into the fixture as the raw UTF-8 character. An unpaired surrogate is only ever written doubled
  (`\\ud800`): with a single backslash the fixture readers would replace it (Go) or throw (C#) before the decoder sees the line. The exact
  fixture spelling of every case with an escaped key is given under *Fixture spellings*; the Tester copies it verbatim. The Write and
  Edit tools turn a single-backslash u-escape in the text they are given into the character, while doubled backslashes survive; check
  the written bytes, for example with `grep -c 'u006b' testdata/wire/decoder-cases.json`.
- `want.error`: one of `UnsupportedVersion`, `Malformed`, `UnknownKind`, `Sequence`, `EmptyBatch`, `LimitExceeded`, `Invalid` (the names of
  `WireErrorKind`; Go maps them to `wire.ErrUnsupportedVersion`, `wire.ErrMalformed`, `wire.ErrUnknownKind`, `wire.ErrSequence`,
  `wire.ErrEmptyBatch`, `wire.ErrLimitExceeded`, `model.ErrInvalid`), `want.line` the line of the error (`DecodeError.Line` /
  `WireException.Line`), optional `want.field` and `want.reason` for `Invalid`.
- `want.records`: the records returned before the end or before the error, each with `kind` and `seq`; optional `want.format_minor` (the
  header's minor, as a JSON integer up to 2^63 - 1).

### Fixture cases

`H` is the header `{"format_major":1,"format_minor":0,"agent_id":"agent-1","boot_id":"0123abcd-0123-0123-0123-0123456789ab","mode":"live"}`,
`T` its tail after the version keys, `M(n)` the record `{"kind":"metric","source":"host","seq":n,"captured_at":"2026-10-01T10:00:00Z","data":{"name":"cpu","value":1}}`.
"accepted" means `records` as given; every other line of a case is `H` or `M(1)` unless stated. Line numbers: header 1, first record 2.

| Case | Line under test | Want |
| ---- | --------------- | ---- |
| K1 | header with every key upper or mixed case (`FORMAT_MAJOR`, `Format_Minor`, `AGENT_ID`, `Boot_ID`, `MODE`) | accepted, metric 1 |
| K2 | record `{"KIND":"metric","Source":"host","SEQ":4,"Captured_At":...,"DATA":{"NAME":"cpu","Value":1}}` | accepted, metric 4 |
| K3 | `process_snapshot` with `"Processes":[{"PID":7,"Command":"x"}]` | accepted, process_snapshot 1 |
| K4 | record key `kind` with its `k` escaped, fixture spelling `\\u006bind` | accepted, metric 1 |
| K5 | record key `KIND` with its `K` escaped in upper-case hex, fixture spelling `\\u004BIND` | accepted, metric 1 |
| K6 | metric payload with an extra key `"kind":"gap"` | accepted, metric 1 |
| N1 | record key `Kind` with raw U+212A | Malformed, 2 |
| N2 | record key `ſeq` (raw U+017F) | Malformed, 2 |
| N3 | header key `agent_ıd` (raw U+0131) | Malformed, 1 |
| N4 | record key U+212A KELVIN SIGN followed by `ind`, the sign escaped, fixture spelling `\\u212aind` | Malformed, 2 |
| N5 | unknown payload key `"é":1` | Malformed, 2 |
| N6 | label key that is the raw U+212A KELVIN SIGN alone (`"labels":{"K":"x"}`) | Malformed, 2 |
| N7 | unknown envelope key, an unpaired high surrogate escape, fixture spelling `\\ud800` | Malformed, 2 |
| N8 | label key, `a` and an unpaired low surrogate escape, fixture spelling `a\\udc00` | Malformed, 2 |
| N9 | unknown envelope key, a valid escaped surrogate pair (U+1F600), fixture spelling `\\ud83d\\ude00` | Malformed, 2 |
| N10 | header unknown key, an unpaired high surrogate escape, fixture spelling `\\ud800` | Malformed, 1 |
| N11 | unknown payload key, a high surrogate escape followed by a non-surrogate escape, fixture spelling `\\ud800\\u0041` | Malformed, 2 |
| D1 | `"seq":9,"seq":3` | Malformed, 2 |
| D2 | `"data"` twice | Malformed, 2 |
| D3 | `kernel_event` with `"oom_kill"` twice | Malformed, 2 |
| D4 | metric with `"labels"` twice | Malformed, 2 |
| D5 | `process_snapshot` with `"processes"` twice | Malformed, 2 |
| D6 | `"name":"cpu","name":null` | Malformed, 2 |
| D7 | `"kind":"metric","kind":null` | Malformed, 2 |
| D8 | `"kind":"gap","Kind":"metric"` | Malformed, 2 |
| D9 | `"kind":"gap"`, then the key `kind` with its `k` escaped, fixture spelling `\"kind\":\"gap\",\"\\u006bind\":\"metric\"` | Malformed, 2 |
| D10 | `"labels":{"a":"1","a":"2"}` | Malformed, 2 |
| D11 | `"labels":{"Mount":"a","mount":"b"}` | Malformed, 2 |
| D12 | `mariadb_status` with `"status":{"Uptime":1,"Uptime":2}` | Malformed, 2 |
| D13 | unknown payload key `"x"` twice | Malformed, 2 |
| D14 | unknown key `"x":[{"a":1,"A":2}]` | Malformed, 2 |
| D15 | header `"format_major":1,"format_major":null` | Malformed, 1 |
| D16 | header `"format_major":1,"format_major":2` | Malformed, 1 |
| D17 | header `"format_major":2,"Format_Major":1` | Malformed, 1 |
| D18 | unknown key `"x":[{"a":1},{"a":2}]` | accepted, metric 1 |
| D19 | unknown key `"a":{"a":{"A":1}}` | accepted, metric 1 |
| H1-H5 | header `[1]`, `[]`, `5`, `"x"`, `true` | Malformed, 1 |
| H6-H7 | header `null`, `{}` | UnsupportedVersion, 1 |
| H8-H20 | `format_major` `"1"`, `"2"`, `1.0`, `1e0`, `1E0`, `10e-1`, `2.5`, `true`, `{}`, `[1]`, `9223372036854775808`, `-9223372036854775809`, `1e400`, each followed by `T` | Malformed, 1 |
| H21-H29 | `format_major` `null`, missing, `0`, `-0`, `-1`, `2`, `3000000000`, `9223372036854775807`, `-9223372036854775808`, each with `T` | UnsupportedVersion, 1 |
| H30 | `{"format_major":2,"agent_id":{"x":1},"boot_id":[1],"mode":5}` | UnsupportedVersion, 1 |
| H31 | `{"format_major":1,"format_minor":0,"agent_id":{"x":1},"boot_id":"...","mode":"live"}` | Malformed, 1 |
| O1 | header `{"format_major":2,"x":1,"x":2}` | Malformed, 1 |
| O2 | header `{"format_major":2,"é":1}` | Malformed, 1 |
| O3 | header `{"format_major":2,"x":<64 nested arrays>}` (depth 65) | Malformed, 1 |
| O4 | `H`, `M(1)`, `M(2)` with `"seq":2,"seq":2` | records metric 1, then Malformed, 3 |
| X1 | record with unknown envelope key `"x":` 63 nested arrays (depth 64) | accepted, metric 1 |
| X2 | same with 64 nested arrays (depth 65) | Malformed, 2 |
| X3 | header with `"x":` 63 nested arrays (depth 64) | accepted, metric 1 |
| X4 | header with 64 nested arrays (depth 65) | Malformed, 1 |
| M1 | `format_minor` `3000000000` | accepted, metric 1, `format_minor` 3000000000 |
| M2 | `format_minor` `9223372036854775807` | accepted, metric 1, `format_minor` 9223372036854775807 |
| M3 | `format_minor` `9223372036854775808` | Malformed, 1 |
| M4 | `format_minor` `"0"` | Malformed, 1 |
| M5 | `format_minor` `-1` | Invalid, 1, field `format_minor`, reason `must not be negative` |
| U1 | record `"kind":"Metric"` | UnknownKind, 2 |
| U2 | record line `null` | UnknownKind, 2 |

Notation: a code span holding a backslash in this table is the fixture spelling (backslash doubled, see *Fixture format*); every other
code span shows the line as JSON text. Cases N1-N3, N5, N6 and O2 hold the raw UTF-8 character named in the row (U+212A is the bytes
`E2 84 AA`, U+017F `C5 BF`, U+0131 `C4 B1`, U+00E9 `C3 A9`).

### Fixture spellings

The line under test of each case with an escaped key, exactly as the JSON string in `decoder-cases.json` (`H` and `M(1)` as above):

```text
K4   "{\"\\u006bind\":\"metric\",\"source\":\"host\",\"seq\":1,\"captured_at\":\"2026-10-01T10:00:00Z\",\"data\":{\"name\":\"cpu\",\"value\":1}}"
K5   "{\"\\u004BIND\":\"metric\",\"source\":\"host\",\"seq\":1,\"captured_at\":\"2026-10-01T10:00:00Z\",\"data\":{\"name\":\"cpu\",\"value\":1}}"
N4   "{\"\\u212aind\":\"metric\",\"source\":\"host\",\"seq\":1,\"captured_at\":\"2026-10-01T10:00:00Z\",\"data\":{\"name\":\"cpu\",\"value\":1}}"
N7   "{\"kind\":\"metric\",\"source\":\"host\",\"seq\":1,\"captured_at\":\"2026-10-01T10:00:00Z\",\"\\ud800\":1,\"data\":{\"name\":\"cpu\",\"value\":1}}"
N8   "{\"kind\":\"metric\",\"source\":\"host\",\"seq\":1,\"captured_at\":\"2026-10-01T10:00:00Z\",\"data\":{\"name\":\"cpu\",\"value\":1,\"labels\":{\"a\\udc00\":\"x\"}}}"
N9   "{\"kind\":\"metric\",\"source\":\"host\",\"seq\":1,\"captured_at\":\"2026-10-01T10:00:00Z\",\"\\ud83d\\ude00\":1,\"data\":{\"name\":\"cpu\",\"value\":1}}"
N10  "{\"format_major\":1,\"format_minor\":0,\"agent_id\":\"agent-1\",\"boot_id\":\"0123abcd-0123-0123-0123-0123456789ab\",\"mode\":\"live\",\"\\ud800\":1}"
N11  "{\"kind\":\"metric\",\"source\":\"host\",\"seq\":1,\"captured_at\":\"2026-10-01T10:00:00Z\",\"data\":{\"name\":\"cpu\",\"value\":1,\"\\ud800\\u0041\":1}}"
D9   "{\"kind\":\"gap\",\"\\u006bind\":\"metric\",\"source\":\"host\",\"seq\":1,\"captured_at\":\"2026-10-01T10:00:00Z\",\"data\":{\"name\":\"cpu\",\"value\":1}}"
```

After the fixture reader resolves these strings, each line holds the single-backslash escape (six characters, or twelve for a pair) at
the key's place, which the decoders under test then unescape. K4 and D9 would also pass with the escape resolved too early (an ordinary key, an exact
duplicate), so both runners assert that the line under test of K4, K5, N4 and D9 contains a backslash after the fixture is read (AC1).

## Approach

**Go (`internal/wire/decode.go`).** A new `checkLine(line []byte) error` runs first in `readHeader` (before the version struct is
unmarshalled) and in `decodeRecord` (before the envelope). Suggested shape, standard library only (0042): walk the line with
`json.NewDecoder(bytes.NewReader(line))`, `UseNumber()` and `Token()`, keeping a stack of frames (object or array; for an object, whether a
key is expected and the set of keys seen, lower-cased in ASCII, allocated on the first key and reusable). A `json.Delim` `{` or `[` that makes
the stack deeper than `MaxDepth` fails; a key with any byte >= 0x80 fails — `Token` has already unescaped it and turned invalid UTF-8 and
unpaired surrogates into U+FFFD, so every non-ASCII form is caught by this one test; a key whose ASCII lower case is already in the frame's
set fails. The walk stops after the first complete value; a syntax error from `Token` is returned wrapped as `ErrMalformed` (the later
`json.Unmarshal` would report the same class). The functions stay under gocognit 15 (split the frame handling). `MaxDepth` is a new
exported constant in `wire.go`. The header-shape behavior of Go is already the target on a 64-bit `int`; to make it hold on every
`GOARCH`, `readHeader` reads the major into a `*int64` (today `*int`, `decode.go:132`) and `Header.FormatMinor` becomes `int64` (today
`int`, `wire.go:43`). Otherwise a 32-bit build (CI and release build only `amd64`, `.github/workflows/ci.yml:213`, `release.yml:106`, so no
test would notice) would report `3000000000` or 2^63 - 1 as `ErrMalformed` instead of `ErrUnsupportedVersion` (major) or accepted
(minor). `Header.FormatMajor` stays `int`: it is bound only after the major was read as exactly 1, and the encoder sets it from
`MajorVersion` — the same split as C# (`FormatMajor` `int`, `FormatMinor` `long`).

**Go (`internal/model`).** A generic helper `checkFoldUnique` in `model.go`, called from `MetricPoint.Validate` (labels) and
`MariaDBStatus.Validate` (status, variables) after the existing key checks, walks the sorted keys and fails on the first key whose ASCII lower
case was seen before.

**C# (`src/Vandox.Core/Wire`).**
- `BatchDecoder` parses both header and record lines with `new JsonDocumentOptions { MaxDepth = WireFormat.MaxDepth }`, then calls
  `WireKeyCheck.FindViolation(document.RootElement)`; a non-null result throws `Fail(WireErrorKind.Malformed, <result>, null, line)`.
- `WireKeyCheck.FindViolation` walks objects and arrays (recursion is bounded by `MaxDepth`, which the parse enforced; an explicit stack is
  fine too). For each property it reads `JsonProperty.Name` inside a `try` that catches only `InvalidOperationException` (thrown for an
  unpaired surrogate escape or invalid UTF-8 in a key) and returns `key not ASCII`; a name that fails `System.Text.Ascii.IsValid` returns
  `key not ASCII`; then it adds the name to a per-object `HashSet<string>(StringComparer.OrdinalIgnoreCase)` (exact ASCII case folding, since
  the name is ASCII by then) and returns `duplicate key` when the add fails. Sets may be pooled per call to stay inside AC14.
- `ReadMajor` is rewritten: root `null` → missing (`UnsupportedVersion`, `format_major missing`); root not an object → `Malformed`,
  `header is not a JSON object`; the property whose name equals `format_major` ignoring ASCII case (at most one, after the check) missing or
  `null` → `UnsupportedVersion`; a `Number` for which `TryGetInt64` succeeds → that value; any other value → `Malformed`,
  `format_major is not an integer`; a value other than `WireFormat.MajorVersion` → `UnsupportedVersion` as today. Its private signature is
  the Dev's choice (for example returning `long?` and throwing through `Fail`).
- `WireHeader.FormatMinor` becomes `long`; `WireFormat.MaxDepth` is a new constant 64.

**C# (`src/Vandox.Core/Model/PayloadRegistry.cs`).** `Options` gets `PropertyNameCaseInsensitive = true`. In a scratch copy this alone
broke none of the 1,383 `Vandox.Core.Tests` and 74 `Vandox.Storage.Tests` tests; the storage read path (`RecordQueries.ReadJson`) reads the
store's own canonical rows, which do not change meaning.

### Forms the parsers accept for a key, and the guard's behavior

| Form of a key in the line | Go (`Token`, then byte test) | C# (`JsonProperty.Name`, then `Ascii.IsValid`) | Rule |
| ------------------------- | ---------------------------- | ---------------------------------------------- | ---- |
| raw ASCII, any case | ASCII | ASCII | allowed; matched and compared ignoring ASCII case |
| short escapes `\" \\ \/ \b \f \n \r \t`, `\u0000`-`\u007f` (hex in either case) | unescaped to ASCII | unescaped to ASCII | allowed; compared after unescaping (`kind`, `KIND` and the escaped spellings of fixture cases K4, K5 and D9 are one key) |
| raw multi-byte UTF-8 (U+0080 and up, U+212A, U+017F, U+0131) | non-ASCII | non-ASCII | `Malformed` |
| `\u0080` and up, surrogate pair escape | non-ASCII | non-ASCII | `Malformed` |
| unpaired surrogate escape (`\ud800`, `\udc00`, high followed by non-low) | U+FFFD | `InvalidOperationException` | `Malformed` |
| invalid UTF-8 bytes, overlong forms (`C0 AF`), encoded surrogates (`ED A0 80`) | U+FFFD | `InvalidOperationException` | `Malformed` |
| unquoted, single-quoted, trailing comma | syntax error | `JsonException` | `Malformed` (as today) |

Nesting: objects and arrays both count, scalars do not, the outermost value is 1; C#'s parser limit was measured at that boundary
(64 accepted, 65 rejected, header and record), and fixture cases X1-X4 and O3 pin the Go count to it.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Go `internal/wire` | `wire.go` | new `const MaxDepth = 64`; `Header.FormatMinor` becomes `int64` |
| Go `internal/wire` | `decode.go` | new `checkLine`; called first in `readHeader` and `decodeRecord`; `readHeader` reads the major as `*int64` |
| Go `internal/model` | `model.go`, `metric.go`, `mariadb.go` | new `checkFoldUnique`; called for labels, status, variables |
| `Vandox.Core` | `Wire/WireKeyCheck.cs` (new) | the C# line check |
| `Vandox.Core` | `Wire/BatchDecoder.cs` | parse options with `MaxDepth`, call `WireKeyCheck`, rewritten `ReadMajor` |
| `Vandox.Core` | `Wire/WireFormat.cs` | new `MaxDepth` constant |
| `Vandox.Core` | `Wire/WireHeader.cs` | `FormatMinor` becomes `long` |
| `Vandox.Core` | `Model/PayloadRegistry.cs` | `PropertyNameCaseInsensitive = true` |
| shared | `testdata/wire/decoder-cases.json` (new, Tester) | the shared decoder cases |

## Signatures (for the Dev's skeleton)

Go:

```go
// internal/wire/wire.go
// MaxDepth is the deepest nesting of objects and arrays a line may have; the line's outermost value counts as 1.
const MaxDepth = 64

// internal/wire/wire.go, field of Header (was int; FormatMajor stays int)
FormatMinor int64 `json:"format_minor"`

// internal/wire/decode.go
// checkLine checks the JSON-level rules of one line before it is bound: no key outside ASCII after unescaping,
// no two keys of one object equal ignoring ASCII case, no nesting deeper than MaxDepth. It returns nil or an
// error wrapping ErrMalformed whose text names the broken rule but never the key.
func checkLine(line []byte) error

// internal/model/model.go
// checkFoldUnique requires the keys of m to differ from each other ignoring ASCII case; the error names the
// later of two such keys in byte order, as field[<quoted key>].
func checkFoldUnique[V any](field string, m map[string]V) error
```

C#:

```csharp
// src/Vandox.Core/Wire/WireFormat.cs
/// <summary>
/// The deepest nesting of objects and arrays a line may have; the line's outermost value counts as 1.
/// </summary>
public const int MaxDepth = 64;

// src/Vandox.Core/Wire/WireHeader.cs (was int)
/// <summary>
/// Gets or sets the minor version of the format.
/// </summary>
[JsonPropertyName("format_minor")]
public long FormatMinor { get; set; }

// src/Vandox.Core/Wire/WireKeyCheck.cs (new file)
/// <summary>
/// Checks the keys of a parsed wire line before it is bound.
/// </summary>
internal static class WireKeyCheck
{
    /// <summary>
    /// Finds the first key, at any depth, that is not ASCII after unescaping or that repeats another key of its object ignoring ASCII case.
    /// </summary>
    /// <param name="root">The parsed line, nested at most <see cref="WireFormat.MaxDepth"/> deep</param>
    /// <returns><c>null</c> when every key is fine, otherwise the reason: <c>key not ASCII</c> or <c>duplicate key</c></returns>
    internal static string? FindViolation(JsonElement root);
}
```

`BatchDecoder.ReadMajor` (private) changes its behavior and may change its signature (Dev's choice). `PayloadRegistry.Options` keeps its
signature. Files the skeleton rewrites: none beyond the files above; `WireKeyCheck.cs` is new (skeleton pragma as *Skeleton* in
`.squad/stack.md`).

## Test files

- Go: `internal/wire/decode_test.go` (shared cases runner `TestDecoder_SharedCases`, raw-byte keys, error texts, `MaxDepth`, allocation),
  `internal/wire/encode_test.go` (`CheckRecord`, `EncodeBatch`), `internal/model/metric_test.go`, `internal/model/mariadb_test.go`.
- C#: `tests/Vandox.Core.Tests/WireKeyCheckTests.cs` (new, for `WireKeyCheck`), `tests/Vandox.Core.Tests/BatchDecoderTests.cs` (raw-byte keys,
  no foreign exception, error texts, header shape, `FormatMinor`, `MaxDepth`, reflection test, allocation),
  `tests/Vandox.Core.Tests/WireContractTests.cs` (shared cases runner `DecodeSharedCaseGivesExpectedResult`).
- Shared: `testdata/wire/decoder-cases.json` (Tester).

Existing tests whose expected result changes with the new rules (Tester, step 5; they then fail on the current code):
- `internal/wire/decode_test.go`: `TestNewDecoder_RejectsUnsupportedVersion` rows "upper-case duplicate wins" and "later duplicate wins" and
  the subtest "folded duplicate with major 1 wins" become `ErrMalformed`; `acceptedFormCases` rows "duplicate keys, last wins",
  "Kelvin sign and long s in keys" and "folded duplicate key wins" move to a rejection table as `ErrMalformed`, line 2.
- `tests/Vandox.Core.Tests/BatchDecoderTests.cs`: `BatchDecoderRefusesBadHeader` rows `[1]` and `{"format_major":"1"}` become `Malformed`.

Existing call sites of a changed signature: `WireHeader.FormatMinor` (`int` → `long`) at `BatchDecoderTests.cs:590`, `:592` and `:899`. They
compile unchanged (`Assert.AreEqual` infers `long`, the interpolation formats either); if the build or an analyzer objects, the **Dev** adapts
them mechanically in step 4 (`3L`, `0L`), no assertion changed. Go `Header.FormatMinor` (`int` → `int64`): every existing use assigns or
compares an untyped constant or formats with `%d` (`wire.go:91`, `:103`; `wire_test.go:104-105`, `:134`, `:176`; `decode_test.go:201`,
`:837`, `:1178`) and compiles unchanged; if one does not, the **Dev** adapts it mechanically in step 4. No other Go signature changes.

## Areas

- [Wire format](../../docs/areas/wire-format.md) — done by the Lead with this plan: *Scope* names the shared fixture; header table
  (`format_major`, `format_minor` ranges); new *Common rules* entry **Keys**; *Null* no longer says "for a key given once"; `metric` labels and
  `mariadb_status` keys unique ignoring ASCII case; *Limits* names `MaxDepth`; *Versioning* states the line rules come first and a
  misshapen header is malformed; *Accepted forms* rows for key case, non-ASCII keys, duplicate keys, sibling keys, nesting, `format_major`,
  `format_minor` and the non-object line; the `jq` note; *Related decisions* 0090; *Implementation*. After the challenge: *Common rules*
  and the *Accepted forms* row on invalid UTF-8 state that C# still rejects such string values (open, F1); *Producer size contract*
  makes a collector record a gap for a refused record; *Duties of the ingest API* names F1 as a blocker of #40.

## Documentation updates

- `docs/areas/wire-format.md` — Lead, done (above).
- `docs/decisions/0090-...md` (new), index row in `docs/decisions/README.md`; in-place edits of the unreleased records 0042 (key matching
  sentence), 0043 (line rules bind every major), 0075 (second shared fixture) and 0089 (the duplicate-key bullet) — Lead, done.
- `docs/UNIT_TESTS.md`, the bullet on `WireContractTests` (line 43): add that both decoders run the shared decoder cases in
  `testdata/wire/decoder-cases.json` (`TestDecoder_SharedCases`, `WireContractTests`), and that a new JSON-level decoding rule gets a case
  there — **Tester**.
- `.squad/project.md`, `docs/ARCHITECTURE.md`, `README.md`: none (no entry becomes untrue; area 10 still names `BatchDecoder` and
  `internal/wire`).

## Architecture check

No guarantee of `docs/ARCHITECTURE.md` / `.squad/project.md` is touched: gapless collection, the non-blocking agent and live-only alerting
do not depend on decoding details. The producer contract holds: the Go encoder writes nothing the new rules reject (golden batch checked; the
model now also refuses case-equal map keys, AC12), so no batch can get stuck in a spool. The record such a refusal costs is recorded as a
gap by its collector (area document, *Producer size contract*; 0028), so "no data gaps unless explicitly recorded" holds. The wire stays 1.0: the rules only reject input no
version writes. Area 10's goal is strengthened (fewer readings of one line, fail closed on ambiguity, bounded extra memory).

## Security considerations

- [x] Limits: `MaxDepth` applies to the parsed structure of the decompressed line, after the existing `MaxLineBytes` cut; the ASCII and
  duplicate tests apply to keys after unescaping, so no escape spelling bypasses them (table above). The key sets hold at most the keys of one
  object of one line (<= `MaxLineBytes`); both runtimes seed string hashing per process (Marvin in .NET, Go's map seed), so crafted keys cannot
  force collisions. Recursion in C# is bounded by `MaxDepth`. Extra allocation is bounded by AC14 / AC15.
- [x] Exceptions reaching a user-visible text: System.Text.Json's `InvalidOperationException` on a key with an unpaired surrogate or invalid
  UTF-8 is caught in `WireKeyCheck` only around `JsonProperty.Name` and becomes `WireException` (`Malformed`, `key not ASCII`); no other new
  exception type can leave the decoder (AC8). Error texts are fixed and never contain a key (AC11); Go's wrapped `encoding/json` syntax
  errors keep today's behavior (area 12 sanitization applies, as the area document says).

## Decision records

- `docs/decisions/0090-wire-keys-matched-ignoring-ascii-case-duplicates-rejected-header-shape-malformed.md` (Proposed, indexed).
- 0042, 0043, 0075, 0089 edited in place (unreleased: no `v*` tag exists).

## Challenge

Devil's Advocate, 0 major, 4 minor objections; all accepted.

1. **The fixture case table lost its JSON escapes** — accepted. Cause: the Write and Edit tools turn a single-backslash u-escape into its
   character, so K4 and D9 read as plain `kind`, N4 as a raw U+212A and N9 as a raw emoji. Revised: K4, K5, N4, N7-N10 and D9 name the
   exact fixture spelling with the backslash doubled (`\\u006bind`, `\\u004BIND`, `\\u212aind`, `\\ud800`, `a\\udc00`,
   `\\ud83d\\ude00`), a new section *Fixture spellings* gives the complete JSON string of each such line, *Fixture format* states the rule
   (doubled backslash for an escape, raw UTF-8 for a raw character, never a single-escaped unpaired surrogate) and warns about the tool
   behavior; AC2, AC3 and the guard table name the escapes the same way. Because K4 and D9 would pass vacuously with the escape resolved,
   AC1 now makes both runners fail when the line under test of K4, K5, N4 or D9 holds no backslash. New case N11 (`\\ud800\\u0041`, a high
   surrogate followed by a non-surrogate escape) covers the guard-table form that had no case.
2. **Go `Header.FormatMajor` / `FormatMinor` are `int`** — accepted with the type change rather than a stated assumption: an assumption
   no CI job checks is the kind of difference this issue removes. `readHeader` reads the major as `*int64`, `Header.FormatMinor` becomes
   `int64` (signature, *Approach*, affected types; call sites compile unchanged, Dev adapts if not); `Header.FormatMajor` stays `int`
   (bound only after the major is 1), matching C#. AC10 adds a `GOARCH=386` run of `internal/wire` by the Tester. Record 0090 names the
   rejected assumption under *Options considered*.
3. **F1 leaves the decoders divergent on string values** — accepted. F1's body now says it blocks #40, and the orchestrator comments
   "Blocked by" on #40 after opening it; the area document lists it as the first duty of the ingest API and states the C# behavior in
   *Common rules* and the *Accepted forms* row, so it is no longer false for C#; 0090 names the blocker. Settling it here stays out of
   scope (it touches 0042 and 0063's storage rule).
4. **AC12 can refuse a whole record in the producer** — accepted. The area document's *Producer size contract* now says a collector records
   a `gap` (cause `unknown`, `collector` set) for a record `CheckRecord` refuses, so the loss is recorded (0028); 0090 and the architecture
   check say so. No collector exists yet, so the rule is tested in each collector's issue.

## Out of scope / follow-ups

Follow-up issues for the orchestrator to open:

- **F1** — title `[Wire] C# decoder rejects invalid UTF-8 and unpaired surrogate escapes in string values`; body: "Found while planning
  #175. The Go decoder, record 0042 and the area row 'Strings with invalid UTF-8 ... replaced by U+FFFD' replace invalid UTF-8 and unpaired
  surrogate escapes (`\ud800`) in string values with U+FFFD; the C# decoder reports `Malformed` (`"message":"a\xffb"`, `"unit":"\ud800"`),
  and for `"kind":"\ud800"` or `"agent_id":"\ud800"` it reports `Malformed` where Go reports `UnknownKind` or a field error. Keys are
  settled by #175 (a non-ASCII key is malformed in both). Decide one rule (replace in C#, matching Go's per-byte replacement, or reject in
  both and amend 0042/0063), add the cases to `testdata/wire/decoder-cases.json`, and remove the C# caveat from the *Common rules* and
  *Accepted forms* of `docs/areas/wire-format.md`. **Blocks #40**: the ingest API must not pass agent input to `BatchDecoder` before this
  is settled, because until then the two decoders read such a batch differently (the area document lists it under *Duties of the ingest
  API*)." Labels: `type: bug`. After opening it, the orchestrator comments on #40: "Blocked by #<F1>: the wire decoders must agree on
  invalid UTF-8 and unpaired surrogate escapes in string values before the ingest API calls `BatchDecoder` (found in #175)."
- **F2** — title `[Storage] Stored JSON payloads carry a redundant "Kind" member`; body: "`[JsonIgnore]` on `IPayload.Kind` is not
  inherited by the implementing properties, so `PayloadRegistry.Serialize` writes `"Kind":"<kind>"` into every JSON payload
  (`BatchWriter.cs:103`; `BatchDecoderTests.cs:680-681` pin it). Decide whether the stored payload should match the wire payload, and fix
  the attribute or the expectation." Labels: `type: bug`.
- Note for #40: the per-decoder memory figures under *Duties of the ingest API* in the area document (about 270 MiB allocated, a heap peak of
  160-170 MB for a 1 MiB line) were measured on the Go decoder; for C# this plan measured 6.5 to 11.9 times the line length allocated for four
  hostile shapes. Re-measure the C# peak when sizing the ingest concurrency.
