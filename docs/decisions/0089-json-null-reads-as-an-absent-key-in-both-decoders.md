# 0089: A JSON null in a batch reads as an absent key in both decoders

- **Status:** Proposed
- **Date:** 2026-10-10
- **Area:** Wire format
- **Source:** Issue #166
- **Supersedes:** —

## Context

The wire format is external input (`.squad/project.md`, *Security areas* 10): hostile input must yield an error, never a crash. The Go
decoder reads a JSON `null` through `encoding/json`, which leaves a non-pointer field at its zero value and sets a pointer, list or map to
nil, so the record's validation decides. The C# decoder reads it through System.Text.Json, which ignores nullable annotations: a `null`
lands in a non-nullable `string` and the validation helpers throw `NullReferenceException` or `ArgumentNullException`, a `null` list element
stays a null reference, and a `null` for a non-nullable number, flag or time throws `JsonException` (reported as `ErrMalformed`, where Go
reports a field error or accepts). The exceptions leave `BatchDecoder.OpenAsync` and `NextAsync`, whose contract is `WireException`. The
area document already describes `null` as absent in several rows (`format_major`, `data`, a record line that is `null`), and the Go encoder
never writes `null`: every list, map and optional field it emits is omitted when empty or unset.

## Options considered

1. **`null` reads as an absent key in both decoders** (chosen) — one rule that the Go decoder already follows and that the existing rows of
   the area document already state; `null` gives a producer nothing that omitting the key does not; no Go change. A `null` in a required
   number reads 0 exactly as an omitted key does.
2. **`null` is malformed wherever the format has no `null`** — fails closed, but the Go decoder would need schema-aware `null` detection
   (custom unmarshalling per type, since `encoding/json` has no option for it), unknown keys must still accept `null` for forward
   compatibility (0043), and four documented rows and their Go tests would change.
3. **C# only: catch every exception of the validation and report it as malformed** — the smallest diff, but it swallows programming errors,
   keeps the two decoders apart (Go accepts `"host":null`) and leaves null list elements in decoded records.
4. **C# only: `RespectNullableAnnotations`** — turns a `null` in a non-nullable string into `ErrMalformed`, again apart from Go, and leaves
   null list elements and map values unhandled.

## Decision

Option 1: in both decoders a JSON `null` reads as if the key were absent; a `null` list element reads as an element with every field at its
zero value, a `null` map value as the zero value of the map's value type, and validation decides as for an omitted key. The C# decoder gets
this from converters in `PayloadRegistry.Options`; the rules are in the [Wire format](../areas/wire-format.md) area.

## Consequences

- No exception other than `WireException` leaves the C# decoder for a `null` anywhere in a batch, and both decoders return the same result,
  down to the field path and reason, for a key given once.
- A `null` in a required number or flag (`value`, `restarts`, `complete`) is accepted as 0 or false, as an omitted key already is. Making
  required numbers detectably present would be a separate change to both decoders; it is not needed while the only producer is the Go
  encoder, which always writes them.
- Duplicate keys combined with `null` are not covered: Go keeps the earlier value of a scalar when a later occurrence is `null` and merges
  repeated objects, C# replaces. Duplicate keys, key case and the header's shape are settled in a separate issue.
- A `null` list element allocates one empty element, which is no more per byte than an empty object `{}` already allocates, so the decoder's
  memory bound does not change.
