# 0090: Both wire decoders check every line for valid UTF-8, paired surrogate escapes, ASCII and unique keys and bounded nesting, match keys ignoring ASCII case, and report a misshapen header as malformed

- **Status:** Accepted
- **Date:** 2026-10-10
- **Area:** Wire format
- **Source:** Issues #175, #182
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
- **Invalid text in string values (issue #182):** Go's `encoding/json` replaces every byte that does not start a valid UTF-8 sequence,
  and every `\u` escape of a surrogate that is not half of a pair, with U+FFFD. System.Text.Json parses such a line but throws when it reads
  a bound string, so C# reported `Malformed` (`"message":"a\xffb"`, `"unit":"\ud800"`) where Go accepted the text (`a`, U+FFFD, `b`), or
  reported a field error (`unit`, `agent_id`) or `UnknownKind` (`kind`). C# accepted the same text in the value of an unknown key, which it
  never reads, while Go replaced it there too.

Go's `encoding/json` has no option for exact key matching or duplicate detection; `encoding/json/v2`, which has both, is still behind
`GOEXPERIMENT=jsonv2` in Go 1.27, and a third-party JSON library is a new dependency that 0042 rules out. The only producer, the Go
encoder, writes every key once, in lower case and ASCII, and nests at most five levels, and its `json.Marshal` writes valid UTF-8 only
(an invalid byte becomes `\ufffd`) and never a surrogate escape.

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
   header that breaks that is not a batch of any version. A `format_minor` up to 2^63 - 1 is accepted, as in Go. Both numbers are read
   as 64-bit integers; keeping Go's `int` and stating a 64-bit platform as an assumption was rejected, because the result would change on
   a 32-bit target that no CI job builds.
8. **`UnsupportedVersion` for anything but the integer 1** — what C# does; it would tell an operator to upgrade for input that no version
   writes.

Nesting depth:

9. **At most 64 in both** (chosen) — C#'s parser default, made explicit; Go counts while checking keys.
10. **10,000 in both** — C# would have to raise its parser limit for nesting no producer writes.

Keys seen by the duplicate test:

11. **The walk's state per decoder; per depth one set and the list of keys the open object added, emptied by removing exactly those keys;
    an object of more than 64 keys drops its depth's set instead** (chosen) — emptying costs the object's own keys, never a capacity
    another object reached, so the check stays linear in the line; nothing is created again per line or per small object, so neither a
    list of small objects nor a batch of many short, deeply nested lines multiplies a fixed cost; and a decoder keeps, from one line to the
    next, at most one empty set and list per depth, sized for at most 64 keys. A list's entries are zeroed before its length is set to 0
    (Go `clear` on the slice, which costs its own length): a Go slice merely cut to length 0 keeps its old key strings referenced, which
    Security measured at 13.0 MiB after 15 lines with keys of about 1 MiB; C#'s `List.Clear` and `HashSet.Remove` drop the references
    themselves. In C# an object reserves room for its own property count first, so its set grows once. Go compares an object's first 8
    keys by scanning its key list and fills its depth's set only from the 9th key on, so a small object does no map work; the list and
    the set still belong to the decoder and are emptied the same way, unlike option 14. Measured with
    the check in copies of both decoders on the hostile shapes of the area's tests: C# at most 17.3 times the line or batch allocated
    (today up to 13.4), Go at most 33.7 (today up to 2.0). A decoder is used by one caller at a time already, so the shared state needs
    no lock.
12. **A set per depth, pooled, cleared and reused** — clearing costs the capacity the set once reached (Go `clear`, depending on the
    toolchain; .NET `HashSet.Clear` whenever non-empty), so one large object followed by many small ones in the same list makes the check
    quadratic: measured 36 to 59 times a plain parse in C# for a 1 MiB line and 218 to 254 times for a 4 MiB line, and about 250 times
    in Go under an older language version.
13. **A fresh set for every object** — linear, but a list of `{"a":0}` then allocates about 35 times the line in C#, beyond the decoder's
    memory bound in the area's tests.
14. **An object's first 8 keys in a short array, a fresh set from the 9th key on, the walk's state created per line** — what this record
    first chose; it pays a set per object of more than 8 keys and the frame stack or key arrays once per line: a list of objects of 12
    to 48 one-character keys allocated 23.9 to 26.0 times the line in C#, and a batch of short lines nested 63 arrays deep 95.7 times in
    Go.
15. **Option 11 without dropping large sets** — slightly less allocation (no new set after an object of more than 64 keys), but a
    hostile batch would keep, per depth, a set as large as the
    largest object seen at that depth until the batch ends.

Invalid UTF-8 and unpaired surrogate escapes in a line:

16. **Reject in both, as a line rule on the raw bytes** (chosen) — a line that is not valid UTF-8, or that holds a `\u` escape of a
    surrogate other than a high half directly followed by an escaped low half, is `ErrMalformed`. RFC 8259 (section 8.1) requires UTF-8
    and RFC 7493 (I-JSON, section 2.1) forbids invalid UTF-8 and unpaired surrogates; System.Text.Json already refuses both when it reads
    a string, and Go's `encoding/json/v2` does by default. No batch of the only producer changes (see above). The rule needs no schema, so
    unknown keys and a header of any major are covered alike, and it costs one pass over the line without allocating. It amends what
    0042 and 0063 said about the decoder, which were corrected in place (both unreleased).
17. **Replace in C# as Go does, one U+FFFD per invalid byte** — what 0042 first said. .NET's decoder replaces a sequence that is cut
    short with one U+FFFD (`F0 9F 98` gives one, Go three), so C# would need its own pass that rewrites every line before parsing: a
    quirk of Go's `encoding/json` v1 reproduced in a second language, like option 6; one input byte becomes three, and a corrupt batch is
    stored with altered text instead of being refused.
18. **Replace with .NET's own decoder** — less code, but the two decoders would store different text for the same batch, which the
    shared fixture exists to prevent.
19. **Reject only where C# binds a string** (C#'s behavior before) — the result depends on the schema (an unknown key passes), and Go
    would need a schema-aware pass to match it.
20. **I-JSON in full, also noncharacters such as U+FFFF** — wider than any difference between the decoders, and a noncharacter is valid
    UTF-8 a log line may hold.

The time bound on the hostile shape is tested as the ratio of two durations measured in one test, the one exception to the rule that tests
read no real clock: an operation count would test the implementation's own report, not its cost. In C# it uses a 4 MiB line, because
under coverage instrumentation the chosen check came within a factor of about 2.3 of the quadratic one at 1 MiB. Go's `clear` is not
quadratic under this module's `go 1.27`, so no Go time bound can catch a cleared set; review does.

## Decision

Options 1, 4, 7, 9, 11 and 16: before binding, both decoders check every line, the header included: no key outside ASCII after unescaping, no two
keys in one object equal ignoring ASCII case, no nesting deeper than 64, then, on a line that is one complete JSON value, valid UTF-8
and no unpaired surrogate escape — otherwise
`ErrMalformed`, at a cost in proportion to the line and with the check's state kept per decoder. Keys
then match fields ignoring ASCII case, and a header that is not an object or whose `format_major` is not a 64-bit JSON integer is
`ErrMalformed`. The rules are in the
[Wire format](../areas/wire-format.md) area (*Common rules*, *Versioning*, *Accepted forms*), and a shared fixture of decoder cases that both
decoders run pins them.

## Consequences

- The two decoders return the same error class and line, or the same records, for every case of the shared fixture
  `testdata/wire/decoder-cases.json`; a new JSON-level rule gets a case there, so a difference shows up in one of the two test suites.
- The line rules run before the major is read, so they bind every future major (0043): a future header with a duplicate or non-ASCII key,
  invalid UTF-8 or an unpaired surrogate escape is `ErrMalformed` in this backend, not `ErrUnsupportedVersion`. This is deliberate, also
  for the text rule: it does not depend on the major, and a later major can relax it only through a record that supersedes this one.
- A `null` always stands for a key given once (0089): a key repeated with `null` is a duplicate.
- The Go model refuses map keys (metric labels, MariaDB status and variables) that are equal ignoring ASCII case, so `CheckRecord` and
  `EncodeBatch` never produce a line the decoders reject; the C# model needs no such rule, because only the decoders read wire input there.
  A record refused that way is lost to the batch, so its collector records a gap instead (0028); the area document states this as part of
  the producer contract.
- The header's `format_minor` is a 64-bit integer in both languages (Go `int64`, C# `long`) and Go reads the major as `int64`, so the
  header rules hold on every Go target, not only where `int` has 64 bits.
- Case-insensitive binding lives in the shared `PayloadRegistry.Options`, so the storage read path matches keys ignoring case too; that
  changes nothing for the rows the store writes, whose keys come from the same model.
- The check costs one pass over the parsed line. The cost of emptying or reusing the keys seen of an object is bounded by the keys that
  object held, never by the capacity a container once reached for another object, and no key set is ever cleared (a key list is zeroed,
  at the cost of its own length, so no key of an earlier line stays referenced); the state lives in
  the decoder, which is why a decoder must stay with one caller at a time. Go seeds the hash of every map, and .NET switches a string
  set to its randomized hash once one insert walks a chain of more than 100 entries, so crafted keys cannot degrade the sets beyond that.
- `jq` and the decoder can still read one spelling differently (`.kind` misses `"Kind"`), but never two values for one field.
- A batch with invalid UTF-8 or an unpaired surrogate escape anywhere in a line, also in the value of an unknown key or in a header of
  another major, is refused as a whole (0044); neither decoder replaces text any more. A key that breaks the rule is still reported as
  `key not ASCII`, because the key check runs first. A line that is not one complete JSON value (cut short, or with content after the
  value) keeps its syntax error whatever bytes it holds, because C#'s parser refuses it before any line rule runs; Go checks the text only
  after its key walk found one complete value, so both decoders give a syntax reason for it, and the binding still refuses it. Every
  string C# binds is then valid UTF-16, so reading one no longer throws.
- The encoder still replaces invalid UTF-8 with U+FFFD (0042), so a log line with invalid bytes reaches the backend with U+FFFD in its
  text; the agent never sends a line the decoders refuse for this rule. The ingest API (#40) is no longer blocked by the difference.
