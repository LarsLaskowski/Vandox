# 0075: The wire contract between the Go encoder and the C# decoder is pinned by golden fixtures

- **Status:** Accepted
- **Date:** 2026-10-06
- **Area:** Wire format
- **Source:** Product Manager request: move the backend to .NET 10 with Blazor, keep the agent in Go
- **Supersedes:** —

## Context

The agent (Go) writes batches and the backend (C#) reads them (0042, 0043, 0044). Before the change both sides were one Go package; now two
independent implementations must agree on every field name, time format and number format.

## Options considered

1. **Specification text only** — drifts silently.
2. **A shared schema generated into both languages** — a tool and a build step for a small format.
3. **Golden fixtures** (chosen) — the Go encoder is the reference producer; its decompressed output for a batch with one record of every kind is committed and decoded by the C# decoder.

## Decision

Option 3. The fixture is plaintext so a review sees format changes in the diff; the C# decoder keeps the limits and validation rules of the Go
decoder, and the [Wire format](../areas/wire-format.md) area stays the specification.

## Consequences

- An encoder change that is not mirrored in the decoder fails the C# test, and a decoder change that rejects the encoder's output fails it as well.
- The fixture covers well-formed batches; the rejection rules are tested in each language's own unit tests.
- A wire change is an integration-surface change: Go encoder, fixture, C# decoder and the area document move together (`.squad/project.md`).
