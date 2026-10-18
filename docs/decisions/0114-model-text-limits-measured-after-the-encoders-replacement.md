# 0114: The Go model measures text limits as the encoder writes them, invalid UTF-8 counted as its replacement

- **Status:** Proposed
- **Date:** 2026-10-18
- **Area:** Wire format
- **Source:** Issue #270
- **Supersedes:** —

## Context

The Go model (`internal/model`) bounded names and texts by the bytes of the Go string, while the encoder (`json.Marshal`) replaces every
byte that does not start a valid UTF-8 sequence by U+FFFD, three bytes on decode, and the C# decoder bounds the decoded string's UTF-8
bytes. A text of raw bytes with invalid UTF-8 could therefore pass `wire.CheckRecord` and still exceed a limit at the backend, which refuses
the whole batch for one invalid record. Producers that clean their texts (the process collector) are not affected; producers that ship
raw bytes, such as log shipping (#37), are. Invalid bytes in a log line are deliberately kept and replaced on encode rather than refused
([0042](0042-wire-format-gzip-json-lines-standard-library.md)).

## Options considered

1. **Measure the encoded length** (chosen) — every limit counts a byte that does not start a valid UTF-8 sequence as the three bytes of its
   U+FFFD, exactly what the decoders measure after decoding; a record with invalid bytes stays valid as long as it fits, as 0042 intends.
   An extra linear pass over texts that are not valid UTF-8 only.
2. **Refuse invalid UTF-8 in the model** — the simplest rule and the same limits on both sides, but every producer of raw bytes then has to
   clean its texts first or lose the record (the scheduler drops a record `CheckRecord` refuses), which turns 0042's replacement into
   dead code and makes one stray byte in a log line cost the line.
3. **Replace invalid bytes in the model or the producers before validation** — would make the stored and checked text identical, but
   validation does not change records, and cleaning in every producer is the duty this issue moves into the model.

## Decision

Option 1: the Go model measures every name and text limit in the UTF-8 bytes the record has after encoding and decoding, so a record the
model accepts never exceeds a limit at either decoder. The rule is in the [Wire format](../areas/wire-format.md) area (*Records*, *Common rules*).

## Consequences

- A record that the backend would have refused for a text limit is now refused by `wire.CheckRecord` on the agent, so the scheduler drops
  that one record instead of the backend refusing its batch.
- For valid UTF-8, which is all either decoder ever passes to validation, nothing changes.
- A producer that cuts long texts (log shipping, #37) has to cut by the encoded length, or make its text valid UTF-8 first as the process
  collector does; a cut by raw bytes may still produce a refused record.
- A second golden batch, `testdata/wire/replaced-text.jsonl`, pins the agreement of the Go model and the C# decoder at the limit
  ([0075](0075-wire-contract-pinned-by-golden-fixtures.md)).
