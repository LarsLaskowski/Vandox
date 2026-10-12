# Plan: IncidentsAsync reads incident data as UTF-16 strings

Source: Issue #213
Status: Draft
Tier: security — the read path of `IncidentRepository`, named in security area 10 with the 8 MiB bound on stored incident
data (external text), changes how much memory a read of that data takes.

## Problem / root cause

`SqliteStore.IncidentsAsync` (`src/Vandox.Storage/SqliteStore.cs:465-497`) collects every row of the page into a list through
`IncidentRepository.Read` (`src/Vandox.Storage/IncidentRepository.cs:139-159`), which decodes `data` from
`reader.GetString(7)` (line 145): a UTF-16 copy of up to 8 MiB of stored JSON (16 MiB) per row, made only to be parsed.

Claims of the issue, checked:

- `IncidentsAsync` (added with #23) accepts a `Limit` of up to 10,000 — **confirmed**: `RecordQueries.ValidateWindow(query.From,
  query.To, query.Limit)`, `StorageLimits.MaxQueryLimit = 10000`.
- Each row's `data` holds up to 8 MiB of stored JSON — **confirmed**: `IncidentRepository.Validate` refuses more than
  `StorageLimits.MaxIncidentDataBytes` (8,388,608) UTF-8 bytes.
- It is read with `GetString`, a UTF-16 copy — **confirmed** (`IncidentRepository.cs:145`).
- No product code calls it yet — **confirmed**: the only callers are tests (`SqliteStoreIncidentTests`,
  `IncidentReconstructorTests`) and the decorator `RefusingIncidentStore`; `IncidentReconstructor` calls only
  `CountIncidentsAsync` and `SaveIncidentAsync`.
- "The incidents view of #27 would hold many such documents at once" — **confirmed, and the main cost is not the string**: the
  string is garbage after its row is decoded (one at a time), while the list keeps every decoded `Incident` of the page until
  the caller drops it. Reading bytes halves the per-row transient copy; it does not bound the page. That bound is the caller's
  limit, which #27 has to choose.
- `RecordQueries.ReadJson` reads UTF-8 bytes since #23 — **confirmed** (`RecordQueries.cs:287`, `GetFieldValue<byte[]>`).
- Side note: `IncidentConfig.GetWindowBefore`/`GetWindowAfter` throw `InvalidOperationException`, which `IncidentsCommand` does
  not catch — **confirmed, unreachable**: `IncidentsCommand` gets its `BackendConfig` only from `BackendConfigLoader`, which
  refuses a window that does not parse or lies outside 1 minute to 24 hours (`BackendConfigLoader.cs:182-193`) before the
  command runs; the getters can throw only for a configuration built by code. **No change** (see *Out of scope*).

Related defect found on the way: the incident write path (`Validate`, `SaveAsync`, `FindOverlappingAsync`) serializes to a
string and binds it three times (each bind makes a managed UTF-8 copy) — bounded at 8 MiB, not changed here (*Options*).

## Acceptance criteria

- [ ] AC1: reading allocates the stored bytes once instead of a UTF-16 copy: for the stored worst-case incident (the existing
  `WorstCase()` of `SqliteStoreIncidentTests`, more than 1,000,000 bytes of JSON, S = `length(CAST(data AS BLOB))`), the bytes
  `IncidentRepository.Read` allocates on the calling thread minus the bytes `JsonSerializer.Deserialize<Incident>(byte[],
  IncidentJson.Options)` allocates for the same stored bytes are less than 1.5 × S (measured with
  `GC.GetAllocatedBytesForCurrentThread` around synchronous calls on a reader of one row, after one warm-up read; `GetString`
  costs about 2 × S, bytes about 1 × S). Test: `IncidentRepositoryReadAllocatesTheStoredBytesOnce`.
- [ ] AC2: an incident whose texts hold `<`, `ä`, U+202E and a surrogate pair is read back by `IncidentsAsync` equal to the
  saved one, and the stored data holds `<` as its six-byte escape. Test: `SqliteStoreIncidentsRoundTripsEscapedText` (a pin:
  written in step 6).
- [ ] AC3: stored data that does not decode is still refused with `store: reading incidents: decoding incident data` and the
  `JsonException` as the cause — the existing `SqliteStoreIncidentsRefusesUndecodableData` stays green unchanged.
- [ ] AC4: `docs/areas/storage.md` states the bound of an incident read (*Areas*); checked by the Reviewer.

## Approach

`IncidentRepository.Read` decodes `JsonSerializer.Deserialize<Incident>(reader.GetFieldValue<byte[]>(7), IncidentJson.Options)`,
as `RecordQueries.ReadJson` does; the `try`/`catch (JsonException)` and both error texts stay. Stored data that is not valid
UTF-8 (only by editing the file) now fails as a `JsonException` instead of being read with U+FFFD — the same
`StoreException`. The page bound is documented, not enforced: `IncidentsAsync` keeps its limit rules (1 to 10,000), and the
storage area names the memory a page takes and that a caller serving a user pages with a small limit (open limit for #27).

## Options considered

- **No change, all of it left to #27** (the issue's own timing) — the area document would keep not stating the bound the issue
  asks for, and the read would stay unlike `RecordQueries.ReadJson`; the code change is one call.
- **Enforce a page bound now** (a lower maximum limit, or a byte budget per call) — the view's paging is #27's design (list
  columns, detail page); a number chosen now guesses its API and would change the query rules of a public interface without a
  caller. Documented as an open limit instead.
- **Also switch the incident write path to bytes** — bounded at 8 MiB already, about 40 MiB transient per save; it needs
  `CAST($data AS TEXT)` in the insert, the update and the comparison `data = $data` of `FindOverlappingAsync`, where a blob
  never equals text (scratch run), so a missed cast would silently turn every unchanged save into an update. Risk without need.
- Chosen: read bytes, document the page bound (record 0096, consequence on `IncidentsAsync`).

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Vandox.Storage | `IncidentRepository.cs` | `Read` decodes the column's UTF-8 bytes |

## Signatures (for the Dev's skeleton)

None: no member is added or changed (`IncidentRepository.Read(SqliteDataReader)` keeps its signature). Step 4 is skipped.

## Test files

- `tests/Vandox.Storage.Tests/IncidentRepositoryTests.cs` (new): AC1. It reuses the worst-case incident; if `WorstCase()` stays
  private to `SqliteStoreIncidentTests`, the Tester moves it to `tests/Vandox.Storage.Tests/Samples.cs` as an internal helper
  and adapts its one caller.
- `tests/Vandox.Storage.Tests/SqliteStoreIncidentTests.cs`: AC2; AC3 is the existing test.

Existing test code that calls a changed signature: none.

## Areas

- `docs/areas/storage.md`, *Reading* (Dev), the incidents bullet: the `data` column is read from its UTF-8 bytes, like a
  payload; a page holds every returned incident decoded at once, up to the limit, each from at most 8 MiB of JSON, so the
  memory of a read grows with the limit; a caller that serves a user pages with a small limit — open limit for #27, which adds
  the first caller. #212 edits *Writing* of the same document; the two edits touch different bullets.

## Documentation updates

- `docs/areas/storage.md` as above (Dev).
- `.squad/project.md`, security area 10 (Dev): after "`IncidentRepository` refuses an incident of more than 8 MiB of serialized
  JSON", add that it reads stored incident data as UTF-8 bytes. #212 edits other sentences of the same entry.
- Record 0096 (Lead, done in this plan step).
- `docs/ARCHITECTURE.md`: none.

## Architecture check

No guarantee changes: results, ordering, limits and error texts of `IncidentsAsync` are unchanged; only the transient copy per
row shrinks.

## Security considerations

- [x] Limits: no new limit; the existing 8 MiB counts UTF-8 bytes of the serialized JSON (after escaping), unchanged.
- [x] Texts that reach the user: unchanged — `store: reading incidents: decoding incident data` (fixed text, the decoder error
  only as the inner exception).

## Decision records

- [0096](../../docs/decisions/0096-outage-incidents-rebuilt-from-stored-data-merged-by-overlapping-windows.md) (Accepted,
  unreleased, edited in place): a consequence that `IncidentsAsync` holds a page decoded at once and that #27 pages with a small
  limit. No new record: reading the bytes repeats option 25 of 0096 for the incident column.

## Out of scope / follow-ups

- `IncidentConfig.GetWindowBefore`/`GetWindowAfter`: no change. The exception is unreachable through `vandoxd incidents`, the
  getters document it, and catching it in `IncidentsCommand` would add a branch no test can reach through the command. No
  follow-up issue.
- The page bound itself: #27. Proposed comment for the orchestrator to post on #27: "When the incidents view reads
  `IIncidentStore.IncidentsAsync`, page with a small limit: a page keeps every returned incident decoded at once, each from up
  to 8 MiB of stored JSON (see *Reading* in docs/areas/storage.md, #213)."
- The incident write path: see *Options considered*.

## Challenge notes

Devil's Advocate verdict: OBJECTIONS 0 major, 3 minor. Security plan review: APPROVED (non-blocking notes below).

1. #213 is implemented after #212. The Dev re-reads the current `docs/areas/storage.md` and `.squad/project.md` (area 10) and applies the #213 edits as separate, bullet-local edits.
2. AC1: use `GC.GetAllocatedBytesForCurrentThread` around fully synchronous calls with the reader already positioned; run `Read` and `Deserialize` once each beforehand (warm-up of JIT and options cache); confirm `GetFieldValue<byte[]>` on a TEXT column allocates about 1 x S, not 2 x S, before relying on the threshold.
3. AC3: add one row with invalid UTF-8 in the stored data (blob literal cast to text, e.g. `CAST(x'7B22...FF...' AS TEXT)`), expecting the same `StoreException` text with a `JsonException` cause.
4. (Security) `storage.md` *Reading* states the worst case plainly: up to 10,000 incidents per page, each decoded from up to 8 MiB. The proposed comment for #27 is posted on that issue.
