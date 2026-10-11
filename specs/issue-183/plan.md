# Plan: Stored JSON payloads carry a redundant "Kind" member

Source: Issue #183
Status: Draft
Tier: standard — the JSON the store writes into `records.data` changes for every non-metric, non-log kind (a change in stored
behavior), and no security area is touched (`BatchWriter`'s deduplication and insert path stay unchanged).

## Problem / root cause

`IPayload.Kind` carries `[JsonIgnore]` (`src/Vandox.Core/Model/IPayload.cs:15`), but System.Text.Json reads attributes from the
members of the type it serializes, not from an interface those members implement. `PayloadRegistry.Serialize`
(`src/Vandox.Core/Model/PayloadRegistry.cs:72`) serializes with the runtime type (`payload.GetType()`), so the implementing
property `public string Kind => RecordKind.<X>;` of every payload type (`MetricPoint.cs:43`, `ProcessSnapshot.cs:50`,
`ConnectionSnapshot.cs:162`, `ServiceState.cs:74`, `MariaDbStatus.cs:128`, `KernelEvent.cs:100`, `LogLine.cs:71`, `Gap.cs:56`),
which has no attribute of its own, is written as `"Kind":"<kind>"` at the end of every payload.

Decision taken here: **the stored payload is the wire `data` object, without the kind.** The interface attribute shows that
`Kind` was never meant to be serialized; the kind is already stored once in `records.kind` (`BatchWriter.cs:94`), and the
wire format carries it in the envelope, never in `data` (`docs/areas/wire-format.md`, record line table). So the attribute is
fixed, and the test expectations that pin the bug are corrected. This is the evident intent of existing code, so no decision
record is needed.

Claims of the issue, checked against the code:

- `[JsonIgnore]` on `IPayload.Kind` is not inherited by the implementing properties — **confirmed** (no implementing property
  carries an attribute; serialization uses the runtime type).
- `PayloadRegistry.Serialize` writes `"Kind":"<kind>"` into every JSON payload — **confirmed** for all eight payload types (the
  pinned expectations below show it for each kind).
- `BatchWriter.cs:103` stores it — **confirmed**: line 103 stores `PayloadRegistry.Serialize(payload)` for every kind except
  `metric` and `log_line` (those two go to typed tables, so their stored rows are unaffected; the serialized text still carries
  `Kind`).
- `BatchDecoderTests.cs:680-681` pin it — **refuted as to the lines, confirmed as to the fact**: the pinning `DataRow`s of
  `BatchDecoderAcceptsNullOfOptionalPayloadField` are now at `tests/Vandox.Core.Tests/BatchDecoderTests.cs:701-715` (15 rows,
  every kind), the file having shifted since the issue was filed.

Related finding: `tests/Vandox.Core.Tests/NullAsAbsentConverterFactoryTests.cs:219-221` pin `"Kind"` too (metric, connection
snapshot, process snapshot); the issue does not name them.

Effect on reading: a `kind`/`Kind` key inside a payload is ignored both before and after the fix. Before, it binds to the
get-only `Kind` property, which System.Text.Json skips on deserialization; after, it is an unknown member, and unknown members
are ignored (`PayloadRegistry.Options` sets no `UnmappedMemberHandling`; the wire format ignores unknown keys at any depth,
`docs/areas/wire-format.md`). The decoder's duplicate-key check runs on the raw line before binding and is unaffected. Rows
already stored with `"Kind"` therefore still read back through `RecordQueries` (`RecordQueries.cs:276`); no migration is needed
(nothing is released yet: `git tag` lists no tag).

## Acceptance criteria

- [ ] AC1: For each of the eight kinds (`metric`, `process_snapshot`, `connection_snapshot`, `service_state`, `mariadb_status`,
  `kernel_event`, `log_line`, `gap`), `PayloadRegistry.Serialize` of a payload read with `PayloadRegistry.Deserialize` from a
  valid wire payload yields a JSON object with no property whose name equals `kind` ignoring ASCII case, at the top level.
- [ ] AC1b (guard for future payload types, reflection-driven, no hand-kept list): (i) every `public const string` field of
  `RecordKind` maps through `PayloadRegistry.TypeOf` to a non-null type; (ii) every concrete (non-abstract) class in
  `typeof(IPayload).Assembly` that implements `IPayload` is the `TypeOf` of one of those kinds; (iii) for every type so found,
  the public instance property `Kind` declared on it carries `System.Text.Json.Serialization.JsonIgnoreAttribute`
  (`GetCustomAttribute<JsonIgnoreAttribute>()` is not `null`) and its `Condition` is `JsonIgnoreCondition.Always`. A ninth
  payload type added without the attribute, or added without a registry entry or a `RecordKind` constant, fails this test.
- [ ] AC2: The serialized payload otherwise stays as before: the 15 expectations of `BatchDecoderAcceptsNullOfOptionalPayloadField`
  (`BatchDecoderTests.cs:701-715`) and the three of `NullAsAbsentConverterFactoryTests.cs:219-221` hold with the trailing
  `,"Kind":"<kind>"` removed and nothing else changed (e.g. `{"name":"cpu","value":0,"unit":""}` for the first row).
- [ ] AC3: Reading a payload that carries a `Kind` member still works and the member has no effect: `PayloadRegistry.Deserialize`
  of `{"name":"cpu","value":1,"Kind":"metric"}` and of `{"name":"cpu","value":1,"Kind":"gap"}` with kind `metric` returns a
  `MetricPoint` whose `Kind` is `metric` and whose `Validate()` returns `null`; the same for a stored row in the shape written
  before this fix, e.g. kind `gap` with `{"from":"2026-03-01T09:00:00+00:00","to":"2026-03-01T10:00:00+00:00","cause":"unknown","collector":"","Kind":"gap"}`.
- [ ] AC4: `IPayload.Kind` itself is unchanged: each payload's `Kind` still returns its `RecordKind` constant (already asserted by
  `PayloadValidationTests.PayloadValidateValidPayloadReturnsNull`; it must stay green).

## Approach

Add `[JsonIgnore]` to the implementing `Kind` property in each of the eight payload types (each file already has
`using System.Text.Json.Serialization;`). Keep the attribute on `IPayload.Kind` only as documentation of intent: System.Text.Json
does not read it for any serialization in the code base, because the only serialization of a payload is
`PayloadRegistry.Serialize` (`PayloadRegistry.cs:74`), which always uses the runtime type; nothing serializes `DataRecord` or
an `IPayload`-typed value (`grep` for `JsonSerializer.Serialize`/`Serialize(` in `src/`: only `PayloadRegistry.cs:74`,
`BatchWriter.cs:103` through it, and `NullElementListConverter{TItem}.cs:47` for list items, which are not payloads).

Option rejected: a `DefaultJsonTypeInfoResolver` modifier in `PayloadRegistry.Options` that drops a property named `Kind` from
every payload type. It would hold for a future payload type without an attribute, but it hides a per-type contract in a
name-based rule and adds code; the guard for a future type is the reflection-driven test of AC1b instead, which needs no
per-kind row and fails for a new payload type that lacks the attribute.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Vandox.Core | `src/Vandox.Core/Model/MetricPoint.cs` | `[JsonIgnore]` on `Kind` |
| Vandox.Core | `src/Vandox.Core/Model/ProcessSnapshot.cs` | `[JsonIgnore]` on `Kind` |
| Vandox.Core | `src/Vandox.Core/Model/ConnectionSnapshot.cs` | `[JsonIgnore]` on `Kind` |
| Vandox.Core | `src/Vandox.Core/Model/ServiceState.cs` | `[JsonIgnore]` on `Kind` |
| Vandox.Core | `src/Vandox.Core/Model/MariaDbStatus.cs` | `[JsonIgnore]` on `Kind` |
| Vandox.Core | `src/Vandox.Core/Model/KernelEvent.cs` | `[JsonIgnore]` on `Kind` |
| Vandox.Core | `src/Vandox.Core/Model/LogLine.cs` | `[JsonIgnore]` on `Kind` |
| Vandox.Core | `src/Vandox.Core/Model/Gap.cs` | `[JsonIgnore]` on `Kind` |
| Vandox.Storage | `src/Vandox.Storage/BatchWriter.cs` | none (writes what `PayloadRegistry.Serialize` returns) |

## Signatures (for the Dev's skeleton)

None: no member is added or changes its signature; only an attribute is added to the existing member
`public string Kind => RecordKind.<X>;` in the eight files above. Step 4 (*Skeleton*) is not needed.

## Test files

- `tests/Vandox.Core.Tests/PayloadRegistryTests.cs` (new; class `PayloadRegistryTests` for `PayloadRegistry.cs`): AC1 (one
  `DataRow` per kind, the valid payloads of `PayloadValidationTests.PayloadValidateValidPayloadReturnsNull` may be reused as
  input), AC1b (one test method, no `DataRow`s, e.g. `PayloadRegistryEveryPayloadTypeIgnoresKindProperty`, enumerating by
  reflection as stated in AC1b), AC3.
- `tests/Vandox.Core.Tests/BatchDecoderTests.cs`: AC2, the 15 `DataRow`s at lines 701-715.
- `tests/Vandox.Core.Tests/NullAsAbsentConverterFactoryTests.cs`: AC2, the three expectations at lines 219-221.
- `tests/Vandox.Core.Tests/PayloadValidationTests.cs`: AC4, unchanged.

Existing test code that calls a changed signature: none. The pinned expectations in `BatchDecoderTests.cs` and
`NullAsAbsentConverterFactoryTests.cs` are adapted by the **Tester** in step 5 (they then fail on the current code, which is
the tests-first evidence for AC2).

## Areas

- `docs/areas/storage.md` (*Schema*, `records` row, line 34): `data` is "the record's payload as the `data` object of the wire
  format, without the kind (that is the `kind` column)"; and one bullet under *Reading*: a stored payload is read with the
  payload options of the wire decoder, so an unknown member is ignored — this includes the `Kind` member that rows written
  before #183 carry. Owner: **Dev**.

## Documentation updates

Only the area document above (owner: Dev). `docs/areas/wire-format.md`: none (the wire format is unchanged).

## Architecture check

No guarantee of `docs/ARCHITECTURE.md` or `.squad/project.md` is touched. Deduplication (security area 1) is unchanged:
`BatchWriter` and its `ON CONFLICT DO NOTHING` insert are not edited; only the text of the `data` column of newly written rows
gets shorter. Reading of existing rows is preserved (AC3).

## Security considerations

- [x] No limits: the change adds or alters no byte or length limit.
- [x] No exception: no new exception path; `PayloadRegistry.Deserialize` throws exactly as before.

## Decision records

None: no decision beyond the obvious fix — the existing `[JsonIgnore]` on `IPayload.Kind` already states that the kind is not
part of the payload JSON; record 0063 (stored JSON payloads) is unaffected in substance.

## Out of scope / follow-ups

- No migration that strips `"Kind"` from rows already stored: nothing is released, and reading ignores the member (AC3).

## Challenge

Devil's Advocate, round 1 (two minor objections):

1. *The Approach claims the interface `[JsonIgnore]` still applies when a payload is serialized through its declared interface
   type, e.g. `DataRecord.Data`, but nothing serializes `DataRecord` or an `IPayload`-typed value.* — **Accepted.** Confirmed by
   reading: the only payload serialization is `PayloadRegistry.Serialize` with `payload.GetType()` (`PayloadRegistry.cs:74`).
   The example is dropped; the Approach now says the interface attribute is kept only as documentation of intent.
2. *The future-type guard is only a review convention; make AC1 reflection-driven over all registered payload types.* —
   **Accepted.** New AC1b enumerates the `RecordKind` constants, the registry and the concrete `IPayload` implementations of
   `Vandox.Core` by reflection and requires `[JsonIgnore]` (condition `Always`) on each type's `Kind` property; a ninth type
   without the attribute (or without registration) fails a test. The per-kind behavioral check (AC1) stays, because the
   attribute check alone would not catch a serialization change through options or converters. The rejected resolver option
   under *Approach* now names AC1b as its replacement guard. Tier, scope and signatures are unchanged.
