# Plan: Import holds a forged maximum legacy snapshot several times in memory

Source: Issue #212
Status: Draft
Tier: security — the change bounds an allocation driven by external input (security area 10, *Parsing of external input*) in
`BatchWriter`, the deduplicating write path of security area 1, and adds a store-wide refusal rule.

## Problem / root cause

`BatchWriter.Write` (`src/Vandox.Storage/BatchWriter.cs:105`) stores every payload other than a metric or a log line as
`PayloadRegistry.Serialize(payload)` (`src/Vandox.Core/Model/PayloadRegistry.cs:72-75`), a UTF-16 `string`, and binds it as text
(`BatchWriter.cs:48`, `$data`). Nothing bounds the size of that JSON: the model allows 4,096 processes with command lines of
16,384 bytes (`ModelLimits.MaxItems`, `MaxTextBytes`), and the legacy parser's caps allow about 105 MB of escaped JSON. The
serializer builds it in pooled buffers that grow by doubling, then the string doubles it, then the driver makes a UTF-8 copy
and SQLite copies again while inserting.

Claims of the issue, checked:

- `BatchWriter` stores the payload as `PayloadRegistry.Serialize(payload)`, a `string` — **confirmed** (`BatchWriter.cs:105`).
- Caps 4,096 rows, 1,024-byte commands, 2,048-byte command lines, 4,096 programs — **confirmed** (`docs/areas/log-import.md`,
  *Process snapshot* and *Bounds*).
- About 105 MB of JSON when all texts are `<`, six bytes per `<` — **confirmed**: 4,096 × (1,024 + 2,048 + 1,024) × 6 is about
  100.7 MB plus field names and numbers; the six-byte escape is pinned by `SqliteStoreRecordsRoundTripsEscapedPayloadText`.
- About 200 MiB as a UTF-16 string — **confirmed** (a scratch run: `JsonSerializer.Serialize` of a 50.4 MB document allocated
  100.8 MB, `SerializeToUtf8Bytes` 50.4 MB).
- Parsed snapshot about 40 MiB — **confirmed** by the documented bound (`log-import.md`, *Bounds*).
- SQLite's UTF-8 copy when the parameter is bound, about 100 MiB — **confirmed and understated**: binding a `string` allocates
  a managed UTF-8 copy of the same size (scratch run: 50.4 MB for a 50.4 MB document; `Microsoft.Data.Sqlite` 10.0.12 calls
  `sqlite3_bind_text`), and the insert adds native copies of about twice the document (peak resident size in a scratch run).
- `vandoxd import` runs in the backend container (512 MiB, shared) — **confirmed** (records 0060, 0079).
- Proposal "serialize to UTF-8 bytes and bind them" — **refuted as a fix by itself**: a scratch run of
  `SerializeToUtf8Bytes` of a 101 MB document under `DOTNET_GCHeapHardLimit` of 384 MiB (the default for a 512 MiB container:
  75 %) ran out of memory during serialization, as the string path did; the serializer's pooled buffer chain (up to the next
  power of two, plus the smaller ones it returned to `ArrayPool<byte>.Shared` while growing) dominates. Binding a `byte[]`
  directly fails as well: `SQLite Error 19: 'cannot store BLOB value in TEXT column'` (`records` is `STRICT`). Binding
  `CAST($data AS TEXT)` stores type `text` with identical bytes (scratch run).
- Proposal "or cap the stored payload size" — taken, together with the bytes (*Approach*).

Related defects found on the way (not fixed here, see *Out of scope*): `IncidentRepository.Validate`/`SaveAsync` serialize and
bind incident data as a string (bounded at 8 MiB by `StorageLimits.MaxIncidentDataBytes`); the serializer's pooled buffers
stay in `ArrayPool<byte>.Shared` after a large payload (a scratch run kept about 224 MiB after two 50 MB serializations until
the pool trims).

## Acceptance criteria

- [ ] AC1: `PayloadJson.Serialize(payload, maxBytes)` returns exactly the bytes of
  `JsonSerializer.SerializeToUtf8Bytes(payload, payload.GetType(), PayloadRegistry.Options)` for payloads whose texts hold `<`,
  `>`, `&`, `'`, `+`, `ä`, U+202E, a surrogate pair and a lone high surrogate U+D800, for a gap, a
  service state and a process snapshot. Tests: `PayloadJsonSerializeMatchesTheSerializerOutput` (data rows per payload).
- [ ] AC2: at `maxBytes` equal to the length of that output `Serialize` returns it and `Fits` returns `true`; at one byte less
  `Serialize` returns `null` and `Fits` returns `false`. Tests: `PayloadJsonSerializeAcceptsJsonOfExactlyTheLimit`,
  `PayloadJsonSerializeReturnsNullAboveTheLimit`, `PayloadJsonFitsTellsTheLimitApart`.
- [ ] AC3: memory does not follow the payload above the limit: `Serialize` and `Fits` of a process snapshot whose JSON is at
  least 6,000,000 bytes, with `maxBytes` 65,536, each allocate less than 1,048,576 bytes on the calling thread
  (`GC.GetAllocatedBytesForCurrentThread`, measured after the payload is built). Test:
  `PayloadJsonAboveTheLimitAllocatesInProportionToTheLimit` (one data row per method).
- [ ] AC4: `RecordRules.CheckPayloadSize` returns `null` for a record whose payload JSON is exactly 16,777,216 bytes, the text
  `data is larger than 16777216 bytes as JSON` for one of 16,777,217 bytes, and `null` for a metric and a log line whatever
  their size (typed records keep no JSON). Tests: `RecordRulesCheckPayloadSizeAcceptsDataOfExactlyTheLimit`,
  `RecordRulesCheckPayloadSizeRefusesDataAboveTheLimit`, `RecordRulesCheckPayloadSizeIgnoresTypedRecords`.
- [ ] AC5: the import skips a parsed record whose JSON is above the limit: the file completes, its `Skipped` count includes it,
  its problems list `record refused: data is larger than 16777216 bytes as JSON` (line 0), and the records before and after it
  are stored. Test: `ImporterRunAsyncSkipsARecordWhoseDataIsLargerThanTheLimit`.
- [ ] AC6: `SqliteStore.WriteBatchAsync` of a batch without an import step (origin `backend`) that holds such a record throws
  `InvalidBatchException` with the message `store: invalid batch: data is larger than 16777216 bytes as JSON`, and nothing of
  the batch is stored (also not its records before that one). Test: `SqliteStoreWriteBatchRefusesDataLargerThanTheLimit`.
- [ ] AC7: a stored JSON payload has SQLite type `text` and its bytes equal the serializer output of AC1 (`<` as six bytes);
  typed records keep `NULL`; `RecordsAsync` reads it back equal. Test: `SqliteStoreWriteBatchStoresPayloadJsonAsText` (a pin:
  written in step 6). The existing `SqliteStoreRecordsRoundTripsEscapedPayloadText` and
  `SqliteStoreWriteBatchStoresAndReadsBackEveryPayloadStyle` must stay green unchanged.
- [ ] AC8: the area documents state the limit and the memory bound (*Areas*); checked by the Reviewer.

AC1–AC6 fail on the skeleton (new members throw, the rule and the writer do not refuse); AC7 is a pin.

## Approach

1. `StorageLimits.MaxPayloadBytes = 16777216` (16 MiB): above any legitimate payload — an agent record arrives in a wire line
   of at most 1 MiB (`WireLimits.MaxLineBytes`), and re-serializing expands a byte by at most six, so at most 6 MiB; a legacy
   snapshot printed by `top -w 512` has command lines under 512 bytes and is a few MB in practice — only texts made mostly of
   escaped characters (forged, or 4,096 processes with long non-ASCII names and no spaces) reach the limit. It is small next to
   the container: building, copy and SQLite's copies stay under about 6 × 16 MiB.
2. `BoundedBufferWriter` (new, `Vandox.Storage`, internal): an `IBufferWriter<byte>` over its own growing array (doubling, not
   pooled). It keeps bytes until more than `maxBytes` were advanced; from then on `Exceeded` is `true`, the written bytes are
   dropped, and `GetMemory`/`GetSpan` hand out one reused scratch array of at least the requested size, so serialization runs to
   its end without growing. Its kept array never exceeds `maxBytes` plus the largest single size hint (a hint is the escape bound
   of one value, at most six times a 16 KiB text). No exception is used for the limit.
3. `PayloadJson` (new, internal static): `Serialize` and `Fits` write the payload with
   `JsonSerializer.Serialize(Utf8JsonWriter, payload, payload.GetType(), PayloadRegistry.Options)` into a `BoundedBufferWriter`.
   The `Utf8JsonWriter` must be created with `JsonWriterOptions` taken from `PayloadRegistry.Options` (`Encoder`, `Indented`
   from `WriteIndented`, `MaxDepth`): the writer, not the serializer options, decides the escaping when serializing into a
   writer (AC1 catches a mismatch). `Serialize` returns an exact-length copy (`WrittenSpan.ToArray()`), or `null` when exceeded;
   `Fits` makes no copy.
4. `RecordRules.CheckPayloadSize(record)`: `null` for a metric or log line (or no payload), else `PayloadJson.Fits` against
   `StorageLimits.MaxPayloadBytes`, the text `data is larger than 16777216 bytes as JSON` built from the constant.
5. `RecordEmitter.RecordAsync` (`src/Vandox.Import/RecordEmitter.cs:189`): `RecordRules.CheckImportRecord(record) ??
   RecordRules.CheckPayloadSize(record)` — a refused record takes the existing path (`record refused: …`, before `_drop`, so a
   resumed import refuses it again and counts the same). It is **not** added to `CheckImportRecord` or `Check`, which
   `BatchValidator` calls again for every record (a third serialization).
6. `BatchWriter`: the insert binds `CAST($data AS TEXT)`; `Write` sets `$data` to
   `PayloadJson.Serialize(payload, StorageLimits.MaxPayloadBytes)` and throws
   `InvalidBatchException("store: invalid batch: data is larger than 16777216 bytes as JSON")` on `null`. The exception leaves
   `WriteBatchAsync` before the commit, so the transaction rolls back (it is not a `SqliteException`, so it is not rewrapped).
   This is the store's own guard for callers other than the import (later ingest, tools); the import never reaches it.
7. `PayloadRegistry.Serialize` stays (tests and decoder tests use it); no production code calls it any more.

## Options considered

- **UTF-8 bytes without a limit** (the issue's first proposal) — refuted by the scratch run: still out of memory under the
  container's default heap limit, and the model allows payloads four times larger than the legacy parser's.
- **A relaxed encoder** — no bound by itself (controls and supplementary characters still expand), and the stored text would
  differ from what `IncidentBuilder` measures with (record 0096, option 24).
- **Lower legacy parser caps** — changes what 0093 keeps from real logs and bounds one parser, not the store.
- **The limit in `RecordRules.Check`/`CheckImportRecord`, so `BatchValidator` checks it with the record index** — a third
  serialization of every JSON payload of an import batch; the writer serializes anyway and a refused batch rolls back whole.
- **Serialize with `SerializeToUtf8Bytes` in the writer after the emitter's check** — relies on every caller checking first;
  the bounded buffer bounds every caller.
- **A larger container memory limit** — moves the limit instead of bounding the allocation (record 0060).
- Chosen: store-wide limit measured in a bounded buffer, checked by the importer before buffering and by the writer while it
  serializes, bound as UTF-8 bytes cast to text (record 0063, option g).

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Vandox.Storage | `StorageLimits.cs` | new constant `MaxPayloadBytes` |
| Vandox.Storage | `BoundedBufferWriter.cs` (new) | bounded `IBufferWriter<byte>` |
| Vandox.Storage | `PayloadJson.cs` (new) | bounded payload serialization |
| Vandox.Storage | `RecordRules.cs` | new `CheckPayloadSize` |
| Vandox.Storage | `BatchWriter.cs` | `CAST($data AS TEXT)`, bounded bytes, refusal |
| Vandox.Import | `RecordEmitter.cs` | calls `CheckPayloadSize` after `CheckImportRecord` |

## Signatures (for the Dev's skeleton)

```csharp
// src/Vandox.Storage/StorageLimits.cs
/// <summary>The most UTF-8 bytes of a payload stored as JSON.</summary>
public const int MaxPayloadBytes = 16777216;

// src/Vandox.Storage/BoundedBufferWriter.cs
internal sealed class BoundedBufferWriter : IBufferWriter<byte>
{
    internal BoundedBufferWriter(int maxBytes);              // ArgumentOutOfRangeException for maxBytes < 0
    internal bool Exceeded { get; }                         // more than maxBytes were advanced
    internal ReadOnlySpan<byte> WrittenSpan { get; }         // the bytes written; empty once exceeded
    public void Advance(int count);                          // ArgumentOutOfRangeException for a negative count
    public Memory<byte> GetMemory(int sizeHint = 0);
    public Span<byte> GetSpan(int sizeHint = 0);
}

// src/Vandox.Storage/PayloadJson.cs
internal static class PayloadJson
{
    internal static byte[]? Serialize(IPayload payload, int maxBytes);
    internal static bool Fits(IPayload payload, int maxBytes);
}

// src/Vandox.Storage/RecordRules.cs
public static string? CheckPayloadSize(DataRecord record);
```

`BatchWriter.Write(RecordBatch, DataRecord, CancellationToken)` keeps its signature. No existing file is final as it stands;
the skeleton rewrites none beyond adding these members.

## Test files

- `tests/Vandox.Storage.Tests/PayloadJsonTests.cs` (new): AC1, AC2, AC3.
- `tests/Vandox.Storage.Tests/BoundedBufferWriterTests.cs` (new, step 6 only if the coverage gate reports lines of
  `BoundedBufferWriter` uncovered, e.g. the argument checks).
- `tests/Vandox.Storage.Tests/RecordRulesTests.cs` (new): AC4.
- `tests/Vandox.Storage.Tests/SqliteStoreWriteTests.cs`: AC6, AC7.
- `tests/Vandox.Import.Tests/ImporterStoreTests.cs`: AC5; the parser double `tests/Vandox.Import.Tests/LineParser.cs` gets a
  line kind `huge` that becomes a process snapshot above the limit (e.g. 200 processes with 16,384-byte command lines of `<`,
  valid under the model rules), its summary comment extended (Tester).

Existing test code that calls a changed signature: none. `RecordingEmitter` and `CountingEmitter` (`tests/Vandox.Core.Tests`)
emulate the model and range rules of the importer, not the new size rule; no Core parser test can produce a payload near
16 MiB except the legacy heap tests, which use `DiscardingEventCollection`/counting doubles — they stay as they are.

## Areas

- `docs/areas/storage.md`, *Writing* (Dev): a new bullet — a JSON payload is built in a bounded buffer and stored as UTF-8
  text; one above 16,777,216 bytes of JSON is refused (the exact texts of AC5 and AC6, the import skips it, any other batch is
  refused whole and nothing of it is stored); an agent record cannot reach it (1 MiB wire line, at most six bytes per byte);
  memory per payload: the JSON as bytes (at most the limit), the buffer while it is built (at most about twice the limit),
  and SQLite's copies while it is inserted (about twice the JSON). The sentence "Every record is validated before anything is
  written" gets the exception: the size rule is checked while the record is written, in the same transaction.
- `docs/areas/log-import.md` (Dev): in the legacy monitoring log's *Bounds*, that a process or connection snapshot whose stored
  JSON would exceed 16,777,216 bytes (possible only for forged text: 0093's caps allow about 105 MB) is skipped with
  `record refused: data is larger than 16777216 bytes as JSON`, and that a legitimate snapshot stays far below; where the
  general import rules describe refused records ("record refused: …"), the size rule is named once.
- `docs/areas/detection-and-reports.md`, *Limits of incidents* (Dev): the read of one process snapshot is bounded by the
  16 MiB limit for data stored by this version; the 240 MiB figure applies only to a snapshot stored by an earlier build.

## Documentation updates

- The three area documents above (Dev).
- `.squad/project.md`, security area 10 (Dev): add the payload limit (`StorageLimits.MaxPayloadBytes`,
  `RecordRules.CheckPayloadSize`, `PayloadJson`, `BoundedBufferWriter`, `BatchWriter`) and adjust the sentence on
  `RecordQueries.ReadJson` and the 240 MiB peak like *Limits of incidents*; the *Test doubles* row of `RecordingEmitter` and
  `CountingEmitter` says they validate like the importer — add "except the payload size".
- Records 0063 and 0096 (Lead, done in this plan step).
- `docs/ARCHITECTURE.md`: none (no guarantee or flow changes; the limit is an area-document value).

## Architecture check

Deduplication (security area 1) is untouched: the insert keeps `ON CONFLICT DO NOTHING`, and a refused batch commits nothing.
"No data gaps unless recorded" (0028): an agent record cannot reach the limit (wire line bound), so no acknowledged agent data
is refused; an import record above it is a reported skip, as every refused import record already is. Readers are unchanged:
`RecordQueries.ReadJson` reads the column's bytes, and SQLite's JSON functions still see text, not JSONB.

## Security considerations

- [x] Limits: `MaxPayloadBytes` counts the UTF-8 bytes of the serializer output **after** escaping and after the encoder
  replaced a lone surrogate — exactly the bytes stored, so no form of text bypasses it; typed records (metric, log line) are
  exempt and bounded by the model's text limits.
- [x] Texts that reach the user: the import summary prints the fixed reason `record refused: data is larger than 16777216 bytes
  as JSON` through `Terminal.Quote`; `InvalidBatchException` carries the fixed text of AC6. No runtime exception text and no
  input text reaches either.
- Determinism of a resumed import: the rule depends only on the record, so a resume refuses the same records; an import
  interrupted by an earlier build that stored an oversized record and resumed by this one would drop one record too many
  (unreleased builds only).

## Decision records

- [0063](../../docs/decisions/0063-storage-schema-records-table-typed-metric-and-log-tables-json-payloads.md) (Accepted,
  unreleased, extended in place): options d–i and the decision for the payload limit and binding.
- [0096](../../docs/decisions/0096-outage-incidents-rebuilt-from-stored-data-merged-by-overlapping-windows.md) (Accepted,
  unreleased): the consequence on storing a forged maximum snapshot no longer points to a follow-up issue.

## Out of scope / follow-ups

- The incident write path (`IncidentRepository.Validate`, `SaveAsync`, `FindOverlappingAsync`) keeps binding a string: bounded
  at 8 MiB of JSON, about 40 MiB of transient allocation per save; switching it would need `CAST` in three statements,
  including the comparison `data = $data`, where a blob never equals text (see the plan of #213).
- `ArrayPool<byte>.Shared` keeping the serializer's buffers after a large serialization: with the limit the largest is about
  32 MiB; no change.
- Planned verification (Tester, Dev — not run by the Lead): AC1–AC7 as named; no end-to-end import of a 105 MB forged legacy
  log is required, AC3 and AC5 cover the bound with small inputs.

## Challenge notes

Devil's Advocate verdict: OBJECTIONS 0 major, 2 minor. Security plan review: APPROVED (non-blocking notes below).

1. AC6 must prove the rollback itself: valid records before the oversized one, a read-back showing none of them stored, and a normal batch written afterwards to show the writer connection and lock stay usable. The Dev confirms in `SqliteStore.WriteBatchAsync` that the transaction is disposed without commit on a non-`SqliteException`.
2. Every JSON payload in an import is serialized twice (in `Fits` in the emitter, then in the writer). Deliberate trade-off: keep `Fits` copy-free, name the double serialization as a known cost in `docs/areas/storage.md`; the Reviewer does not flag it.
3. (Security) Step 2 claims the kept array never exceeds `maxBytes` plus the largest size hint, while plain doubling can reach 2 x the limit and the area document says "about twice the limit": clamp growth to `min(2 x capacity, maxBytes + hint)` or align the text. The Tester adds one row pinning allocation for a payload that just fits (total allocation of `Serialize` <= 4 x output length).
4. (Security) When the limit is crossed the Dev drops the reference to the kept array (sets it to null), not just stops using it.
5. (Security) The byte counter stops counting once `Exceeded` is set (sticky flag) or is a `long`, so it cannot wrap.
6. (Security) The 6 MiB bound for agent records is untested; the ingest plan must check it when ingest lands.
7. Shared docs with #213 (`docs/areas/storage.md`, `.squad/project.md` area 10): #212 is implemented first; #213 re-reads the current text and edits bullet-locally.
