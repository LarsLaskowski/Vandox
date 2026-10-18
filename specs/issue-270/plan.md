# Plan: Model text limits must hold after the encoder's UTF-8 replacement

Source: Issue #270
Status: Draft
Tier: security — the changed length check is the model validation that the Go wire decoder (`internal/wire` `Decoder.Next`,
`decode.go:522`) runs on external input (*Security areas* 10 in `.squad/project.md`), and the change adds a fixture to the wire
contract between the two languages (integration surface); when in doubt, the higher tier.

## Problem / root cause

`internal/model/model.go:211-216` (`checkLen`) compares `len(s)`, the raw bytes of the Go string, with the limit, and
`checkPattern` (`model.go:185`) does the same for names and UUIDs. The encoder (`internal/wire/encode.go:25`, `json.Marshal`) writes
every byte that does not start a valid UTF-8 sequence as `�`, which decodes to U+FFFD, three UTF-8 bytes. The C# model measures
the decoded string (`src/Vandox.Core/Model/Check.cs:130` `Length`, `:69` `Pattern`: `Encoding.UTF8.GetByteCount(value)`), and the Go
decoder validates the decoded string with the same `checkLen`. So a `log_line.message` of 8,193 bytes `0xFF` passes `wire.CheckRecord`
(8,193 <= 16,384) and decodes to 24,579 bytes: `too long` at the backend, and because `BatchDecoder.NextAsync` throws
(`BatchDecoder.cs:352-356`, `WireErrorKind.Invalid`) the whole batch is refused.

Claims of the issue, checked:

- *`model.go` around 211-220 measures text limits by the bytes of the Go string* — confirmed (`checkLen`, also `checkPattern`).
- *The encoder replaces every invalid UTF-8 byte with U+FFFD* — confirmed: `json.Marshal` decodes with `utf8.DecodeRuneInString`
  and replaces each `(RuneError, 1)`, one U+FFFD per byte; pinned today by
  `TestEncodeBatch_InvalidTextDecodesAsReplacementCharacters` (`internal/wire/encode_test.go:637`, `"\xed\xa0\x80"` decodes to three
  U+FFFD). Not unverified: an existing test measures it on the module's Go version.
- *The backend checks the decoded length* — confirmed (`Check.Length`, UTF-8 bytes of the decoded string), as the area document says
  (`docs/areas/wire-format.md`, *Common rules*: "lengths are in UTF-8 bytes of the decoded string").
- *A field that passes `wire.CheckRecord` can make the backend refuse the whole batch* — confirmed (above).
- *The process collector cleans its own texts* — confirmed (`internal/procstats/cmdline.go:32-52`, valid UTF-8 cut to the limit), so its
  records keep `encodedLen == len` and are unaffected.
- *Other producers are affected* — confirmed in principle: the scheduler drops any record `CheckRecord` refuses before the spool
  (`internal/scheduler/runner.go:266-283`), so after the fix such a record is lost on the agent (with the collector's gap) instead of
  costing the batch; no producer of raw log text exists yet (#37).

Related findings on the way (no separate defect): names (`checkPattern`) have the same raw-byte measure; an invalid byte there fails the
ASCII name pattern anyway, so the only effect is the reason (`invalid characters` in Go, `too long` in C# for, e.g., 43 bytes `0xFF`).
The plan measures names the same way so one length rule holds for the whole model. The C# side needs no change.

## Acceptance criteria

The forms of text a Go string can hold, and how `json.Marshal` and therefore the new measure treat them (Go `unicode/utf8`, which gives
width 1 for every byte that does not start a valid sequence):

| Form (raw bytes) | Replacements on encode | Counted bytes |
| ---------------- | ---------------------- | ------------- |
| stray continuation `80` | 1 | 3 |
| `FF` (also `FE`, `F5`..`FF`) | 1 | 3 |
| overlong 2-byte `C0 AF` | 2 | 6 |
| overlong 3-byte `E0 80 80` | 3 | 9 |
| UTF-8-encoded surrogate `ED A0 80` | 3 | 9 |
| above U+10FFFF `F4 90 80 80` | 4 | 12 |
| cut short `E2 82` before `a`, and `E2 82` at the end of the string | 2 | 6 |
| valid U+FFFD `EF BF BD` (decodes as `RuneError` with width 3) | 0 | 3 (not 9) |
| valid multi-byte `é`, `€`, `😀` | 0 | 2, 3, 4 |

The guard that fails when the implementation is wrong: counting every `utf8.RuneError` (instead of only width 1) turns the valid U+FFFD
row into 9 and AC2 fails; counting raw bytes fails AC1.

- [ ] AC1: In `internal/model`, a text (`log_line.message`, limit `MaxTextBytes`) and a short text (`log_line.host`, limit
  `MaxShortTextBytes`) that hold one invalid form of the table followed by ASCII `a` up to exactly the limit in counted bytes are
  accepted; the same text with one more `a` is refused with field `message` / `host` and reason `too long` — although its raw length
  is below the limit. One row per invalid form of the table; the row "`E2 82` at the end of the string" puts the `a` filler first
  and the form last (its over-the-limit variant adds the extra `a` at the front).
  Tests: `TestLogLine_Validate_InvalidUTF8CountsAsReplacement` (`internal/model/logline_test.go`).
- [ ] AC2: A valid U+FFFD and valid multi-byte characters count their own UTF-8 bytes: `"�"` (and `"é€😀"`) plus `a` up to
  exactly the limit is accepted, one more `a` is refused (`too long`).
  Tests: `TestLogLine_Validate_ValidMultiByteCountsItsOwnBytes` (`internal/model/logline_test.go`).
- [ ] AC3: Names are measured the same way: a `source` of 43 bytes `0xFF` (raw 43, counted 129 > `MaxNameBytes`) is refused with field
  `source`, reason `too long`; 42 bytes `0xFF` (counted 126) is refused with reason `invalid characters`.
  Tests: `TestMeta_Validate_NameLengthCountsInvalidBytesAsReplacement` (`internal/model/model_test.go`).
- [ ] AC4: A field accepted by `wire.CheckRecord` never exceeds its limit after decoding: for every invalid form of the table, a
  `log_line` whose `message` is that form plus `a` up to exactly `MaxTextBytes` counted bytes passes `wire.CheckRecord`, is encoded with
  `wire.EncodeBatch` and decoded with the Go decoder, and the decoded `message` is exactly `MaxTextBytes` bytes long and valid UTF-8;
  with one more `a`, `wire.CheckRecord` returns a `*model.FieldError` with field `data.message`, reason `too long`.
  Tests: `TestCheckRecord_InvalidUTF8AtTheLimitDecodesWithinTheLimit`, `TestCheckRecord_InvalidUTF8OverTheLimitIsRefused`
  (`internal/wire/encode_test.go`).
- [ ] AC5: The Go encoder and the C# decoder agree at the limit: a golden batch `testdata/wire/replaced-text.jsonl` is written by the
  Go encoder from one `log_line` record (source `journal`, `log` `journal`, seq 1, `testBootID`, agent `agent-1`, mode `live`) whose
  `host` is exactly `MaxShortTextBytes` and whose `message` is exactly `MaxTextBytes` counted bytes, each holding every invalid form of
  the table once plus a valid U+FFFD and `é€😀`, filled with `a` (message: raw 16,352 bytes, counted 16,384 — the raw length below the
  limit is asserted in the Go test, so the fixture really exercises the replacement). The Go test compares against the file
  (`VANDOX_UPDATE_GOLDEN=1` rewrites it, as for `all-kinds.jsonl`).
  Tests: `TestEncodeBatchGolden_ReplacedText` (`internal/wire/golden_test.go`).
- [ ] AC6: The C# decoder accepts that golden batch: one `log_line` whose `Message` has `Encoding.UTF8.GetByteCount` 16,384 and whose
  `Host` has 1,024, both containing U+FFFD; and the same batch with one `a` inserted at the start of the message value is refused with a
  `WireException` of kind `Invalid` whose `FieldError` has field `data.message` and reason `too long` (the Tester confirms the exact field
  path the C# model reports and uses it).
  Tests: `DecodeReplacedTextBatchAcceptsTextsAtTheLimit`, `DecodeReplacedTextBatchOneByteOverTheLimitIsRefused`
  (`tests/Vandox.Core.Tests/WireContractTests.cs`).
- [ ] AC7: Existing behavior for valid UTF-8 is unchanged: every existing Go model and wire test, the golden `all-kinds.jsonl` test and
  the shared decoder cases pass unchanged.
  Tests: the existing suites (*Test*).

Tests-first: AC1, AC3 and AC4's over-the-limit test fail on the current code (raw length below the limit is accepted); AC2, AC5 and AC6
pin behavior that already holds and guard against a wrong fix.

## Approach

1. `internal/model/model.go`: add `encodedLen(s string) int` — `len(s)` when `utf8.ValidString(s)` (the fast path for every cleaned
   text and for everything a decoder hands over), otherwise a loop over `utf8.DecodeRuneInString` that adds `utf8.RuneLen(utf8.RuneError)`
   (3) for each `(RuneError, 1)` and the width otherwise. No allocation.
2. `checkLen` compares `encodedLen(s)` with the limit; `checkPattern` uses `encodedLen(s)` in its `too long` case. Error reasons and
   field paths stay as they are.
3. Doc comments of `MaxNameBytes`, `MaxShortTextBytes`, `MaxTextBytes` and of `checkPattern`: the length is in UTF-8 bytes after
   encoding, a byte that does not start a valid UTF-8 sequence counting as the 3 bytes of U+FFFD.
4. No change to `internal/wire`, the C# model or decoder. `QuoteName` (display cut by raw bytes) is not a limit and stays.

## Options considered

1. **Measure the encoded length** (chosen) — matches what both decoders measure, keeps a record with stray bytes as 0042 intends
   (invalid bytes are replaced on encode, not refused), no change for valid UTF-8.
2. **Refuse invalid UTF-8 in the model** — simpler and allowed by the issue, but every raw-byte producer (log shipping, #37) would have
   to clean its text or lose the record, since the scheduler drops refused records; 0042's replacement would become dead code. Lost.
3. **Clean (replace) invalid bytes before validation, in the model or every producer** — validation does not mutate records, and
   per-producer cleaning is exactly the duty the issue moves into the model. Lost.
4. **Measure only texts, leave names on raw bytes** — names fail the ASCII pattern anyway, but two length rules in one model and a
   reason that differs from the C# model. Lost; the change to `checkPattern` is one expression.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Go `internal/model` | `model.go`: `encodedLen` (new), `checkLen`, `checkPattern`, doc comments of the limit constants | length after the encoder's replacement |
| Go `internal/model` | `logline_test.go`, `model_test.go` | AC1-AC3 |
| Go `internal/wire` | `encode_test.go`, `golden_test.go` | AC4, AC5 |
| testdata | `testdata/wire/replaced-text.jsonl` (new, written by the Go golden test) | AC5, AC6 |
| .NET `tests/Vandox.Core.Tests` | `WireContractTests.cs` | AC6 |

## Signatures (for the Dev's skeleton)

```go
// encodedLen returns the length of s in UTF-8 bytes after the wire encoder's replacement: every byte that does not start a valid
// UTF-8 sequence counts as the 3 bytes of U+FFFD.
func encodedLen(s string) int
```

`checkLen(field, s string, limit int) error` and `checkPattern(field, s string, re *regexp.Regexp, limit int) error` keep their
signatures. The skeleton rewrites no file beyond `internal/model/model.go`.

## Test files

- `internal/model/logline_test.go` (AC1, AC2), `internal/model/model_test.go` (AC3) — Tester.
- `internal/wire/encode_test.go` (AC4), `internal/wire/golden_test.go` (AC5, a helper `replacedTextRecord()` there builds the record
  deterministically) — Tester; the Tester generates `testdata/wire/replaced-text.jsonl` with `VANDOX_UPDATE_GOLDEN=1` and checks it holds
  no raw byte outside ASCII where `�` is expected (the replaced bytes appear as the escape `�`; the valid characters raw).
- `tests/Vandox.Core.Tests/WireContractTests.cs` (AC6) — Tester; the gzip step reuses the pattern of `DecodeGoldenBatchAcceptsEveryKind`.

Existing test code that calls a changed signature: none (no signature changes).

Files of the change no unit test can cover: none (`testdata/wire/replaced-text.jsonl` is test data, not code).

## Areas

- [Wire format](../../docs/areas/wire-format.md) — *Records*, *Common rules*, the bullet on short text and text: lengths are in UTF-8
  bytes of the decoded string, and the Go model measures a record before encoding the same way, counting each byte that does not start a
  valid UTF-8 sequence as the 3 bytes of its U+FFFD (names included), so a record `wire.CheckRecord` accepts never exceeds a limit at
  either decoder (0114). *Scope* and *Implementation*: name the second golden batch `testdata/wire/replaced-text.jsonl` next to
  `all-kinds.jsonl` (written by the Go golden test, decoded by `WireContractTests`). *Related decisions*: add 0114. Owner: Dev.

## Documentation updates

- `docs/areas/wire-format.md` as above — Dev.
- `docs/decisions/0114-model-text-limits-measured-after-the-encoders-replacement.md` (Proposed) and its index row — Lead (done).
- No change to `README.md`, `docs/ARCHITECTURE.md`, `.squad/stack.md` (its *Layout* names the golden batch as an example; still true),
  `docs/UNIT_TESTS.md` (template-managed).

## Architecture check

The guarantees of `docs/ARCHITECTURE.md` / `.squad/project.md` are kept: the decoders' text rule (no replacement, invalid UTF-8 refused,
0090) and the batch-as-a-whole rule (0044) are unchanged; the producer size contract (0044) gets stricter on the agent only, which is
its purpose — a refused record is dropped with a gap (0028), never silently, and never poisons a batch. No flow changes, so
`docs/ARCHITECTURE.md` is not edited.

## Security considerations

- [x] Every byte or length limit says whether it applies before or after decoding: all model limits are now measured after the
  encoder's replacement, which equals the decoded length both decoders check; the line limit (`MaxLineBytes`) was and stays measured
  on the encoded line in `CheckRecord` / `EncodeBatch`. The replacement grows an invalid byte to 6 bytes on the wire (`�`) and 3
  after decoding; the worst-case line estimate of the producer size contract (every byte as a 6-byte escape) already covers it.
- [x] `encodedLen` is linear in the string, allocation-free, and adds one `utf8.ValidString` pass over texts the decoder hands over
  (bounded by `MaxLineBytes` per line), so the Go decoder's time stays proportional to the line (existing
  `TestDecoder_TimeIsInProportionToTheLine` and the allocation tests keep passing).
- [x] Exceptions or errors that reach a user-visible message: none new; the reason stays `too long` with the existing field paths and
  no text content.

## Decision records

- `docs/decisions/0114-model-text-limits-measured-after-the-encoders-replacement.md` (Proposed) — measure rather than refuse.

## Out of scope / follow-ups

- Log shipping (#37) must cut texts by the encoded length or clean them first; 0114 states it. No new issue: #37 is the producer and
  reads the area document.

## Challenge notes

Devil's Advocate verdict: OBJECTIONS 0 major, 2 minor (verbatim).

1. **Minor.** The AC5 arithmetic is off. The plan says the message is "raw 16,352 bytes, counted 16,384", a difference of 32. The eight invalid rows in its own table add (counted minus raw) 2+2+4+6+6+8+4+4 = 36 bytes. The valid U+FFFD and `é€😀` add 0. The Tester should compute the raw and counted sizes from the final form list, not copy 16,352. The Go test must still assert that raw length is below `MaxTextBytes` and counted length equals it.
2. **Minor.** `encodedLen` stays unexported, but the plan and 0114 tell #37 to "cut by the encoded length". #37 will then have to reimplement the rule, which risks drift. The Dev may export it as `model.EncodedLen` at no extra cost, or the plan can state why not. Either choice is acceptable.

Security plan review: APPROVED. Non-blocking notes (verbatim):

1. The table could add a cut-short 4-byte prefix such as `F0 9F 98` (3 replacements, 9 bytes) as an AC1 row, so a 4-byte truncation is pinned too. My measurement shows the algorithm already handles it.
2. AC6 leaves the exact C# field path (`data.message`) for the Tester to confirm. That is fine; just make sure the test asserts the path the C# side actually reports and does not copy it from the Go side.
