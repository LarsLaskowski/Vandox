# 0090: Both wire decoders check every line for ASCII, unique keys and bounded nesting, match keys ignoring ASCII case, and report a misshapen header as malformed

- **Status:** Proposed
- **Date:** 2026-10-10
- **Area:** Wire format
- **Source:** Issue #175
- **Supersedes:** —

## Context

The wire format is external input (`.squad/project.md`, *Security areas* 10), and the backend's C# decoder is the one at the trust
boundary; the Go decoder has no production caller and is the reference the area document was written against. Both decoders bind JSON
through their standard libraries, which disagree on JSON-level details that the format rules left to the library (0042: "the standard JSON
parser's lenient key matching ... is handled in the format rules"). Running the same lines through both decoders (issue #175) showed:

- **Key case:** Go's `encoding/json` matches a key to a field with Unicode case folding (`Kind`, `FORMAT_MAJOR`, `Kind` with U+212A KELVIN
  SIGN, `ſeq` with U+017F); System.Text.Json matches exactly, so C# ignores the key and reports `UnknownKind` or `UnsupportedVersion`. Even
  System.Text.Json's case-insensitive mode does not fold U+212A or U+017F, so no library switch makes the two equal for non-ASCII keys.
- **Duplicate keys:** Go replaces a repeated scalar, keeps it when the later occurrence is `null`, merges a repeated object or map, and decodes
  a repeated list into the elements of the first occurrence (fields the second occurrence lacks survive); C# replaces. A batch can therefore
  decode to different records in the two languages, and to a third reading in tools such as `jq`.
- **Header shape:** a header that is not an object, or whose `format_major` is not a JSON integer (`[1]`, `"1"`, `1.0`, `1e0`, `true`,
  `1e400`, an integer beyond 64 bits) is `ErrMalformed` in Go and in the area document, but `UnsupportedVersion` in C#; a `format_minor`
  beyond 32 bits is accepted by Go and `Malformed` in C#.
- **Nesting depth:** C# parses a line with a maximum depth of 64 and reports a deeper line as malformed; Go accepts up to 10,000 levels.

Go's `encoding/json` has no option for exact key matching or duplicate detection; `encoding/json/v2`, which has both, is still behind
`GOEXPERIMENT=jsonv2` in Go 1.27, and a third-party JSON library is a new dependency that 0042 rules out. The only producer, the Go
encoder, writes every key once, in lower case and ASCII, and nests at most five levels.

## Options considered

Key case:

1. **ASCII keys only, matched ignoring ASCII case in both decoders** (chosen) — C# switches on case-insensitive binding; rejecting every
   non-ASCII key removes the only cases where the two libraries fold differently. A key the producer never writes changes from "ignored" or
   "folded" to `ErrMalformed`.
2. **Exact key matching in both decoders** — the strictest reading, but Go can only get it from a schema-aware pass over every line (the
   format's maps carry upper-case keys such as `Uptime`, so a schema-free rule cannot tell a misspelled field from a map key), which is a
   second hand-written parser in a security area, or from json/v2 or a dependency, both ruled out above.
3. **Unicode case folding in both** — C# would need its own property matching to fold like Go; more code for spellings no producer writes.

Duplicate keys:

4. **Reject a line with two keys in one object that are equal ignoring ASCII case, at any depth** (chosen) — schema-free, fails closed,
   removes every merge rule and every reading difference with other JSON tools, costs nothing for the producer. Map keys that differ only in
   case are duplicates too; the producer's validation refuses them before a record is spooled.
5. **Last one wins with replacement in both** — Go would have to rewrite lines or replace its binding to stop merging.
6. **Go's merging in C#** — reproduces library quirks (list elements merged into the first occurrence, `null` that keeps a scalar but
   clears a list) in a second language.

Header shape:

7. **`ErrMalformed`** (chosen) — what Go and the area document already do; 0043 fixes `format_major` as a JSON integer for every major, so a
   header that breaks that is not a batch of any version. A `format_minor` up to 2^63 - 1 is accepted, as in Go.
8. **`UnsupportedVersion` for anything but the integer 1** — what C# does; it would tell an operator to upgrade for input that no version
   writes.

Nesting depth:

9. **At most 64 in both** (chosen) — C#'s parser default, made explicit; Go counts while checking keys.
10. **10,000 in both** — C# would have to raise its parser limit for nesting no producer writes.

## Decision

Options 1, 4, 7 and 9: before binding, both decoders check every line, the header included: no key outside ASCII after unescaping, no two keys
in one object equal ignoring ASCII case, no nesting deeper than 64 — otherwise `ErrMalformed`. Keys then match fields ignoring ASCII case, and
a header that is not an object or whose `format_major` is not a 64-bit JSON integer is `ErrMalformed`. The rules are in the
[Wire format](../areas/wire-format.md) area (*Common rules*, *Versioning*, *Accepted forms*), and a shared fixture of decoder cases that both
decoders run pins them.

## Consequences

- The two decoders return the same error class and line, or the same records, for every case of the shared fixture
  `testdata/wire/decoder-cases.json`; a new JSON-level rule gets a case there, so a difference shows up in one of the two test suites.
- The line rules run before the major is read, so they bind every future major (0043): a future header with a duplicate or non-ASCII key
  is `ErrMalformed` in this backend, not `ErrUnsupportedVersion`.
- A `null` always stands for a key given once (0089): a key repeated with `null` is a duplicate.
- The Go model refuses map keys (metric labels, MariaDB status and variables) that are equal ignoring ASCII case, so `CheckRecord` and
  `EncodeBatch` never produce a line the decoders reject; the C# model needs no such rule, because only the decoders read wire input there.
- Case-insensitive binding lives in the shared `PayloadRegistry.Options`, so the storage read path matches keys ignoring case too; that
  changes nothing for the rows the store writes, whose keys come from the same model.
- The check costs one pass over the parsed line and one set of keys per object, bounded by the line; hashing of both runtimes is seeded per
  process, so crafted keys cannot degrade the sets.
- `jq` and the decoder can still read one spelling differently (`.kind` misses `"Kind"`), but never two values for one field.
- Not settled here: C# rejects invalid UTF-8 and unpaired surrogate escapes in string values, where Go and 0042 replace them with U+FFFD
  (follow-up issue); keys are covered, since such a key is not ASCII.
