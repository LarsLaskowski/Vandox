# Plan: Both wire decoders refuse invalid UTF-8 and unpaired surrogate escapes in a line

Source: Issue #182
Status: Draft
Tier: security — the change alters what the ingest wire decoders (`Vandox.Core.Wire.BatchDecoder`, Go `internal/wire`) accept, which is
*Security areas* 10 (parsing of external input) in `.squad/project.md`.

## Problem / root cause

The two decoders read text in string values differently, and neither has a rule for it:

- **C#:** `JsonDocument.Parse` (`src/Vandox.Core/Wire/BatchDecoder.cs:237` header, `:292` record) accepts a string with invalid UTF-8 or an
  unpaired surrogate escape; only `Utf8JsonReader.GetString` throws (`InvalidOperationException`, which System.Text.Json rethrows as
  `JsonException`) when the binding reads a field. So the error appears wherever a bound string holds it: `:265-267` "header does not fit
  the format", `:318-320` "record does not fit the format", `:339-341` "payload does not fit its kind", all `Malformed`. A string the
  binding never reads (the value of an unknown key) is never checked.
- **Go:** `encoding/json` (`internal/wire/decode.go:339`, `:349`, `:422`, `:433`, and `dec.Token()` in the line check at `:179`) replaces
  every byte that does not start a valid UTF-8 sequence, and every surrogate escape that is not half of a pair, with U+FFFD, one per byte.
  Validation then runs on the replaced text.
- The line check of 0090 (`decode.go:148` `checkLine`, `WireKeyCheck.FindViolation`) covers keys only.

Claims of the issue, checked (reading, plus one probe of each decoder in a scratch copy, because neither System.Text.Json's reader nor Go's
per-byte rule is in this repository):

| Claim | Result |
| ----- | ------ |
| Go, record 0042 and the area row replace invalid UTF-8 and unpaired surrogate escapes with U+FFFD | **Confirmed**: 0042 *Consequences*, `wire-format.md` *Common rules* and *Accepted forms*; Go test `internal/wire/decode_test.go:448` pins `a\xffb` decoding to `a`, U+FFFD, `b`. Go replaces per byte: `E2 82` before the quote gives two U+FFFD, `F0 9F 98` three (.NET's decoder gives one for each). |
| C# reports `Malformed` for `"message":"a\xffb"` and `"unit":"\ud800"` | **Confirmed** ("payload does not fit its kind", line 2). Go accepts the message and gives `data.unit: invalid characters` for the unit. |
| `"kind":"\ud800"`: C# `Malformed`, Go `UnknownKind`; `"agent_id":"\ud800"`: C# `Malformed`, Go a field error | **Confirmed**: C# "record does not fit the format" / "header does not fit the format"; Go `unknown record kind` / `agent_id: must be 1 to 64 characters ...`. |
| Keys are settled by #175 (a non-ASCII key is malformed in both) | **Confirmed**: `WireKeyCheck.cs:53-60` maps the `InvalidOperationException` of `property.Name` to `key not ASCII`; Go `decode.go:238`. |
| The area document lists it under *Duties of the ingest API* and it blocks #40 | **Confirmed** (`wire-format.md`, *Error text and consumer duties*, first bullet before this plan). |
| "The C# decoder rejects" such strings | **Only partly true (related defect)**: C# accepts invalid UTF-8 and unpaired surrogate escapes in the value of an unknown key, at any depth, in the header and in records, because nothing reads them; Go accepts them too, after replacing. Fixed by this change (cases S13-S16, S30, S31). |

## Decision (Lead)

**Reject in both decoders, as a line rule** — record 0090, option 16 (extended in this change). A line that is not valid UTF-8, or that
holds a `\u` escape of a surrogate other than a high half directly followed by an escaped low half, is `ErrMalformed`, anywhere in the
line. Reasons, in short (the full weighing is in 0090, options 16-20): RFC 8259 requires UTF-8 and I-JSON (RFC 7493) forbids unpaired
surrogates; System.Text.Json and Go's `encoding/json/v2` both refuse them by default; the only producer, the Go encoder, never writes
either (`json.Marshal` turns an invalid byte into `�` and escapes no surrogate), so no honest batch changes; the rule is schema-free
and allocation-free and needs the same few lines in both languages, whereas "replace in C#" would reproduce Go v1's per-byte quirk in a
second language (0090 rejected that pattern for duplicate keys, option 6). 0042 and 0063 said the decoder replaces; both are unreleased
and were corrected in place. Not escalated: no user-visible behavior changes for batches the agent writes, no guarantee is weakened (the
decoder only gets stricter on input no producer writes), and the issue names this option.

## Acceptance criteria

The fixture cases below are referred to as S1-S38. `H` is the standard fixture header
`{"format_major":1,"format_minor":0,"agent_id":"agent-1","boot_id":"0123abcd-0123-0123-0123-0123456789ab","mode":"live"}`, `L(m)` the
record `{"kind":"log_line","source":"journal","seq":1,"captured_at":"2026-10-01T10:00:00Z","data":{"log":"journal","message":"m"}}`,
`M` the record `{"kind":"metric","source":"host","seq":1,"captured_at":"2026-10-01T10:00:00Z","data":{"name":"cpu","value":1}}`.
Bytes are given in hex; escapes as they stand **in the line** (the fixture file doubles each of their backslashes).

- [ ] AC1: `testdata/wire/decoder-cases.json` holds cases S1-S38 below, and both runners (C# `WireContractTests.DecodeSharedCaseGivesExpectedResult`,
  Go `TestDecoder_SharedCases`) give the result each case names. Every other case of the fixture keeps its result.

  Raw bytes (cases with `"latin1": true`, see AC2):

  | Case | Lines | Want |
  | ---- | ----- | ---- |
  | S1 message with byte FF | `H`, `L(a FF b)` | Malformed, line 2 |
  | S2 message with a stray continuation byte | `H`, `L(a 80 b)` | Malformed, line 2 |
  | S3 message with overlong C0 AF | `H`, `L(C0 AF)` | Malformed, line 2 |
  | S4 message with overlong E0 80 80 | `H`, `L(E0 80 80)` | Malformed, line 2 |
  | S5 message with an encoded surrogate ED A0 80 | `H`, `L(ED A0 80)` | Malformed, line 2 |
  | S6 message above U+10FFFF F4 90 80 80 | `H`, `L(F4 90 80 80)` | Malformed, line 2 |
  | S7 message with byte F5 | `H`, `L(F5 80 80 80)` | Malformed, line 2 |
  | S8 message cut short before the quote | `H`, `L(a E2 82)` | Malformed, line 2 |
  | S9 message cut short before a letter | `H`, `L(a F0 9F 98 b)` | Malformed, line 2 |
  | S10 kind with byte FF | `H`, `M` with `"kind":"metric FF"` | Malformed, line 2 |
  | S11 header agent_id with byte FF | `H` with `"agent_id":"agent-1 FF"`, `M` | Malformed, line 1 |
  | S12 label value with byte FF | `H`, `M` with `"labels":{"a":"FF"}` in `data` | Malformed, line 2 |
  | S13 unknown envelope value with byte FF | `H`, `M` with `"x":"FF"` before `"data"` | Malformed, line 2 |
  | S14 unknown payload value in a list with byte FF | `H`, `M` with `"x":["FF"]` in `data` | Malformed, line 2 |
  | S15 unknown header value with byte FF | `H` with `"x":"FF"` added, `M` | Malformed, line 1 |
  | S16 header major 2 with byte FF | `{"format_major":2,"x":"FF"}`, `M` | Malformed, line 1 (not UnsupportedVersion) |
  | S17 byte FF after the record object | `H`, `M` followed by FF | Malformed, line 2 |
  | S18 second record with byte FF | `H`, `L(a)`, `L(a FF b)` with `"seq":2` | records `log_line:1`, then Malformed, line 3 |

  Escapes (each line under test holds a backslash):

  | Case | Lines | Want |
  | ---- | ----- | ---- |
  | S19 message high surrogate alone | `H`, `L(\ud800)` | Malformed, line 2 |
  | S20 message low surrogate alone | `H`, `L(\udc00)` | Malformed, line 2 |
  | S21 message high then a non-surrogate escape | `H`, `L(\ud800A)` | Malformed, line 2 |
  | S22 message high then a letter | `H`, `L(\ud800x)` | Malformed, line 2 |
  | S23 message low then high | `H`, `L(\udc00\ud800)` | Malformed, line 2 |
  | S24 message high before a valid pair | `H`, `L(\ud800𐀀)` | Malformed, line 2 |
  | S25 message high surrogate in upper-case hex | `H`, `L(\uD800)` | Malformed, line 2 |
  | S26 message high surrogate before an escaped newline | `H`, `L(\ud800\n)` | Malformed, line 2 |
  | S27 metric unit with a high surrogate | `H`, `M` with `"unit":"\ud800"` in `data` | Malformed, line 2 |
  | S28 kind with a high surrogate | `H`, `M` with `"kind":"\ud800"` | Malformed, line 2 |
  | S29 header agent_id with a high surrogate | `H` with `"agent_id":"\ud800"`, `M` | Malformed, line 1 |
  | S30 unknown envelope value with a high surrogate | `H`, `M` with `"x":"\ud800"` before `"data"` | Malformed, line 2 |
  | S31 header major 2 with a low surrogate | `{"format_major":2,"x":"\udc00"}`, `M` | Malformed, line 1 |
  | S32 message escaped surrogate pair | `H`, `L(😀)` | records `log_line:1` |
  | S33 message escaped pair in upper-case hex | `H`, `L(😀)` | records `log_line:1` |
  | S34 message escaped pair in mixed case | `H`, `L(😀)` | records `log_line:1` |
  | S35 message escaped replacement character | `H`, `L(�)` | records `log_line:1` |
  | S36 message escaped noncharacter | `H`, `L(￿)` | records `log_line:1` |
  | S37 message escaped backslash before ud800 | `H`, `L(\\ud800)` (the line holds two backslashes, the file four) | records `log_line:1` |

  Raw valid text (no `latin1`, the characters stand raw in the fixture):

  | Case | Lines | Want |
  | ---- | ----- | ---- |
  | S38 message with raw multi-byte UTF-8 | `H`, `L(` U+00E9 U+20AC U+1F600 `)` | records `log_line:1` |

- [ ] AC2: a fixture case may carry `"latin1": true`. Both runners then turn every line into bytes one character per byte (U+0000-U+00FF
  give the bytes 00-FF; the fixture writes such a byte as the JSON escape `ÿ` with a single backslash, or as the raw character). A
  `latin1` case with a character above U+00FF fails the run, and a `latin1` case none of whose lines holds a character from U+0080 to
  U+00FF fails the run (guard: a byte whose escape was lost or doubled in the file). Go: the field joins `sharedCase` (the loader uses
  `DisallowUnknownFields`). Cases without `latin1` are encoded as UTF-8, as today.
- [ ] AC3: the escape guard that already exists in both runners (`_escapedCaseIds` in C#, `escapedCaseIDs` in Go: the case fails when no
  line holds a backslash) lists S19-S37 as well; the required-case lists (`groups` in `SharedCasesHaveUniqueNamesAndHoldEveryRequiredCase`,
  `requiredCaseIDs` in Go) gain `("S", 38)`.
- [ ] AC4: error texts. C#: `WireException.Kind` is `Malformed`, `Reason` is exactly `line not valid UTF-8` (S1-S18 class) or `unpaired
  surrogate escape` (S19-S31 class), `Message` exactly `wire: line <n>: <reason>`. Go: `errors.Is(err, wire.ErrMalformed)`, text exactly
  `wire: line <n>: malformed batch: line not valid UTF-8` or `... malformed batch: unpaired surrogate escape`. Neither text holds
  any of the line's content: a line with the text `SENTINELTEXT` next to the offending byte or escape gives a message without `SENTINEL`
  (header and record line, both reasons, both languages).
- [ ] AC5: order of the line rules. The key check runs first, so a key that is invalid UTF-8 or holds an unpaired surrogate escape is still
  reported as `key not ASCII` (the existing tests `BatchDecoderRefusesNonAsciiKey`, `BatchDecoderRefusesKeyOfInvalidUtf8`,
  `TestDecoder_RejectsRawInvalidUTF8Keys`, `TestDecoder_ErrorTextsNameNoKey` stay as they are and pass). A line with a non-ASCII key and
  a byte FF in a value gives `key not ASCII`; a line with a duplicate key and `\ud800` in a value gives `duplicate key`. A line that holds
  both an invalid byte and an unpaired surrogate escape gives `line not valid UTF-8` (validity is checked before escapes).
- [ ] AC6: `WireTextCheck.FindViolation` (C#) returns, and never throws:
  - `null` for: an empty span; ASCII text; raw valid multi-byte UTF-8; `😀`, `😀`, `😀`; `�`;
    `￿`; `\\ud800` (escaped backslash); `\"`, `\/`, `\b`, `\f`, `\n`, `\r`, `\t`; and, because the parser rejects such lines
    itself, a backslash as the last byte, `\u` with fewer than four bytes after it, `\uZZZZ` (non-hex).
  - `line not valid UTF-8` for each byte form of S1-S9, also at the very start and the very end of the span.
  - `unpaired surrogate escape` for each escape form of S19-S26, for `\\\ud800` (an escaped backslash, then an escape), for a high
    surrogate escape as the last six bytes, and for a high surrogate escape followed by `\u` with fewer than four bytes after it, by
    `\` alone, or by `\uZZZZ`.
- [ ] AC7: `WireTextCheck.FindViolation` allocates nothing: on a 1 MiB line of valid text holding escaped pairs, after one warm-up call,
  `GC.GetAllocatedBytesForCurrentThread()` does not change across the call.
- [ ] AC8: Go: the accepted-forms case "invalid UTF-8 in a string" (`internal/wire/decode_test.go:448`) is moved to the rejected forms
  and gives `ErrMalformed` at line 2. A Go table test runs the forms of AC6 that are complete JSON (the S1-S9 bytes and S19-S26 escapes
  in a `log_line` message, plus the accepted escapes) through `NewDecoder`/`Next` with the AC4 texts.
- [ ] AC9: the producer never trips the rule: Go `EncodeBatch` of a batch with a `log_line` whose `Message` is `"a\xffb"`, one whose
  `Message` is `"\xed\xa0\x80"`, and a metric label value `"\xff"`, decoded by `NewDecoder`, gives the records with `a`, U+FFFD, `b`,
  three U+FFFD, and U+FFFD respectively; `CheckRecord` accepts each record.
- [ ] AC10: only a `WireException` leaves the C# decoder for every case S1-S38 (the shared runner treats any other exception as a failure,
  as today).

## Approach

1. **C#** — new `internal static class WireTextCheck` in `src/Vandox.Core/Wire/WireTextCheck.cs`. `FindViolation(line)`: first
   `System.Text.Unicode.Utf8.IsValid(line)`; if false, `NotUtf8`. Then one left-to-right scan: on a byte `\`, if the next byte is not `u`
   skip both bytes; else read the escape with `TryReadEscape`: incomplete or non-hex, skip the backslash and go on (the parser has
   accepted the line, so this cannot happen there; the rule is only made total); a code unit DC00-DFFF is `UnpairedSurrogate`; D800-DBFF
   needs a complete escape DC00-DFFF in the next six bytes, else `UnpairedSurrogate`, and both escapes are skipped; any other unit skips
   six bytes. In valid JSON every backslash starts an escape inside a string, so the scan sees exactly the escapes the parser sees.
   `BatchDecoder.ReadHeaderAsync` and `DecodeRecord` call it right after `_keyCheck.FindViolation` returned `null`, inside the same block:
   `var violation = _keyCheck.FindViolation(document.RootElement) ?? WireTextCheck.FindViolation(<line bytes>);`.
2. **Go** — in `internal/wire/decode.go`, `checkText(line)` with the same algorithm (`utf8.Valid`, then the scan, `escapedUnit` reading
   one escape) and the two new fixed errors; `keyCheck.checkLine` calls it after `walk` returned nil (and after `unwind`). The walk can stop
   early (at the end of the first value, or at the end of a line cut short); the text rule then sees the rest of the line too, and the
   binding that follows rejects such a line anyway, so the class and line do not change.
3. Fixture and runners (Tester): cases S1-S38, the `latin1` field, the guards (AC1-AC3).
4. Documentation is already written by the Lead in this step (see *Documentation updates*); the Dev checks it against the code and reports a
   mismatch instead of editing it.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Vandox.Core | `src/Vandox.Core/Wire/WireTextCheck.cs` | new: the text rule |
| Vandox.Core | `src/Vandox.Core/Wire/BatchDecoder.cs` | `ReadHeaderAsync`, `DecodeRecord` call `WireTextCheck.FindViolation` after the key check; class summary unchanged |
| Go `internal/wire` | `decode.go` | `errNotUTF8`, `errUnpairedSurrogate`, `checkText`, `escapedUnit`; `checkLine` calls `checkText`; its doc comment names the new rule |
| testdata | `testdata/wire/decoder-cases.json` | cases S1-S38 |

`WireKeyCheck.cs` stays as it is (final: read, it checks keys only and keeps its `InvalidOperationException` mapping, still needed for keys).

## Signatures (for the Dev's skeleton)

C# (`src/Vandox.Core/Wire/WireTextCheck.cs`, new file; skeleton carries `#pragma warning disable RH2003, S2325`):

```csharp
/// <summary>
/// Checks the raw bytes of a wire line for valid UTF-8 and for surrogate escapes that are not a high half directly followed by an escaped low half.
/// </summary>
internal static class WireTextCheck
{
    #region Constants

    /// <summary>The reason for a line that is not valid UTF-8.</summary>
    internal const string NotUtf8 = "line not valid UTF-8";

    /// <summary>The reason for a line with an unpaired surrogate escape.</summary>
    internal const string UnpairedSurrogate = "unpaired surrogate escape";

    #endregion // Constants

    #region Methods

    /// <summary>Finds the first violation of the text rule in a line.</summary>
    /// <param name="line">The bytes of the line</param>
    /// <returns><c>null</c> when the line keeps the rule, otherwise <see cref="NotUtf8"/> or <see cref="UnpairedSurrogate"/></returns>
    internal static string? FindViolation(ReadOnlySpan<byte> line);

    /// <summary>Reads the <c>\uXXXX</c> escape that starts at <paramref name="index"/>.</summary>
    /// <param name="line">The bytes of the line</param>
    /// <param name="index">The position of the backslash</param>
    /// <param name="codeUnit">The UTF-16 code unit of the escape</param>
    /// <returns><c>true</c> when six bytes of a complete escape with four hex digits start there</returns>
    private static bool TryReadEscape(ReadOnlySpan<byte> line, int index, out int codeUnit);

    #endregion // Methods
}
```

`BatchDecoder`: no signature changes.

Go (`internal/wire/decode.go`):

```go
var (
	errNotUTF8           = fmt.Errorf("%w: line not valid UTF-8", ErrMalformed)
	errUnpairedSurrogate = fmt.Errorf("%w: unpaired surrogate escape", ErrMalformed)
)

// checkText checks the text rule of one line: valid UTF-8, and every \u escape of a surrogate a high half directly followed by an
// escaped low half. It returns nil, errNotUTF8 or errUnpairedSurrogate.
func checkText(line []byte) error

// escapedUnit returns the UTF-16 code unit of the \uXXXX escape at the start of b, and false when b does not start with a complete one.
func escapedUnit(b []byte) (rune, bool)
```

`keyCheck.checkLine(line []byte) error`: signature unchanged, behavior extended.

## Test files

- `tests/Vandox.Core.Tests/WireTextCheckTests.cs` (new, class `WireTextCheckTests`): AC6, AC7.
- `tests/Vandox.Core.Tests/BatchDecoderTests.cs`: AC4 (C#), AC5.
- `tests/Vandox.Core.Tests/WireContractTests.cs`: runner for AC1-AC3, AC10.
- `internal/wire/decode_test.go`: AC4 (Go), AC5 (Go), AC8, runner for AC1-AC3.
- `internal/wire/encode_test.go`: AC9.
- `testdata/wire/decoder-cases.json`: AC1 (written by the Tester, checked on disk: `grep -n '\\\\u' testdata/wire/decoder-cases.json`).

Existing test code affected: no signature changes. Existing expectations that change: the Go accepted-forms case at
`internal/wire/decode_test.go:448` (Tester, step 5, moves it per AC8); the required-case lists in both runners (Tester, step 5).

## Areas

- `docs/areas/wire-format.md` — *Common rules*: new bullet **Text**, the C# caveat removed from *Short text*; *Versioning*: line rules
  include *Text*; *Accepted forms*: the row "Strings with invalid UTF-8" replaced by three rows (rejected bytes, rejected escapes,
  accepted escapes); *Duties of the ingest API*: the #182 bullet removed; *Implementation*: names `WireTextCheck`. **Done by the Lead in
  this step.**
- `docs/areas/storage.md` — *Writing*: why an agent record never holds invalid UTF-8 (encoder replaces, decoder refuses). **Done by the
  Lead in this step.**

## Documentation updates

All by the Lead, already in the working tree; no further owner:

- `docs/decisions/0090-...md` extended (Context bullet, options 16-20, Decision, Consequences), status `Proposed`, title and index row
  updated.
- `docs/decisions/0042-...md` *Consequences* and `docs/decisions/0063-...md` option text corrected: the decoder no longer replaces
  (unreleased records, edited in place, status stays `Accepted`).
- The two area documents above.

`docs/ARCHITECTURE.md`, `.squad/project.md` (area 10 already names `BatchDecoder` and `internal/wire`), `README.md`: no change.

## Architecture check

- *Parsing of external input* (area 10): malformed input still yields an error, never a crash, an unbounded allocation or a hang. The new
  check is one linear pass over a line already bounded by `MaxLineBytes`, allocates nothing and throws nothing.
- *Batch valid only as a whole* (0044): unchanged; a line refused by the rule refuses the batch like any malformed line.
- *Versioning* (0043): the rule is a line rule like 0090's and binds every major; no producer ever wrote such a line, so the format stays
  1.0, as for 0090.
- No guarantee is weakened; the decoder only becomes stricter on input no producer writes. The ingest API (#40) no longer waits on #182.

## Security considerations

- [x] Limits: no text is replaced any more, so no byte grows into three. `MaxLineBytes` and `MaxBatchBytes` count raw decompressed bytes
  before decoding (unchanged); the field limits (`MaxShortTextBytes`, `MaxTextBytes`, `MaxNameBytes`) count UTF-8 bytes of the decoded
  string (unchanged, stated in *Common rules*).
- [x] Exceptions reaching a user-visible reason: none new. The rule returns the fixed reasons `line not valid UTF-8` and `unpaired
  surrogate escape`, which carry no input. The C# path that turned `GetString`'s `InvalidOperationException` into a `JsonException` and
  "... does not fit ..." is no longer reached by invalid text; it stays for other binding errors.
- Forms the parsers accept (the guard's coverage, from RFC 8259 section 7 and the two parsers): inside a string, `\"`, `\\`, `\/`, `\b`,
  `\f`, `\n`, `\r`, `\t` and `\u` with four hex digits of either case; any other backslash sequence, a raw control character and a
  backslash outside a string are syntax errors in both parsers (`Malformed` already). Surrogates can enter a string only as `\uXXXX` (the
  scan: S19-S31, S37, AC6) or as raw bytes `ED A0..BF xx` (UTF-8 validity: S5; Go `utf8.Valid` and .NET `Utf8.IsValid` both refuse
  encoded surrogates, overlong forms such as CESU-8's `C0 80`, and code points above U+10FFFF). Keys are covered by the key check first
  (AC5). A BOM or a UTF-16 line is already `Malformed`. Escapes in numbers or literals do not exist in JSON.
- Equality of the two scans: the algorithm is specified once (Approach 1) and both decoders run the same 38 fixture cases.

## Decision records

- `docs/decisions/0090-wire-keys-matched-ignoring-ascii-case-duplicates-rejected-header-shape-malformed.md` (Proposed; extended: options
  16-20, option 16 chosen). Step 9 sets it and its index row back to `Accepted` after checking it against what was built.
- `docs/decisions/0042-...` and `0063-...`: one sentence each corrected, status unchanged (`Accepted`).

## Out of scope / follow-ups

- Noncharacters (U+FFFE, U+FFFF and the others) stay accepted (0090 option 20).
- How the storage write path treats a lone surrogate in a C# string from the log import is not touched; the wire path cannot produce one
  any more.
- Moving Go to `encoding/json/v2` (still an experiment in Go 1.27).
