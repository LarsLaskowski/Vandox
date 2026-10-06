# 0075: The wire contract between the Go encoder and the C# decoder is pinned by golden fixtures

- **Status:** Accepted
- **Date:** 2026-10-06
- **Source:** Product Manager request: move the backend to .NET 10 with Blazor, keep the agent in Go
- **Supersedes:** —

## Context

The agent (Go) writes batches and the backend (C#) reads them
([0042](0042-wire-format-gzip-json-lines-standard-library.md), [0043](0043-wire-format-major-minor-versioning.md),
[0044](0044-batch-validated-as-a-whole-agent-records-only.md)). Before the change both sides were one Go
package; now two independent implementations must agree on every field name, time format and number format.

## Options considered

1. **Specification text only** — drifts silently.
2. **A shared schema generated into both languages** — a tool and a build step for a small format.
3. **Golden fixtures** — the Go encoder is the reference producer; its decompressed output for a batch with
   one record of every kind is committed and decoded by the C# decoder.

## Decision

Option 3. `internal/wire/golden_test.go` encodes a batch with a record of every kind and compares the
decompressed result with `testdata/wire/all-kinds.jsonl`; setting `VANDOX_UPDATE_GOLDEN=1` rewrites the file.
`WireContractTests` (`tests/Vandox.Core.Tests`) gzips that same file and decodes it with `BatchDecoder`,
expecting every record. The file is plaintext so a review sees format changes in the diff. The C# decoder
keeps the limits and validation rules of the Go decoder; `docs/WIRE_FORMAT.md` stays the specification.

## Consequences

- An encoder change that is not mirrored in the decoder fails the C# test; a decoder change that rejects
  the encoder's output fails it as well.
- The fixture covers well-formed batches; the rejection rules are tested in each language's own unit tests.
- A wire change is an integration-surface change: Go encoder, fixture, C# decoder and `docs/WIRE_FORMAT.md`
  move together (`.squad/project.md`).
