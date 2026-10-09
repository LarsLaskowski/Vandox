# 0042: Wire format is gzip-compressed JSON Lines, built on the standard library only

- **Status:** Accepted
- **Date:** 2026-10-04
- **Area:** —
- **Source:** Issue #10
- **Supersedes:** —

## Context

The agent sends its records to the backend in batches (0045, refined by 0045). The batch format has to be
compact on the link and on the 2 GB server, streamable so the backend never has to hold a whole batch to
start validating it, and easy to evolve, because 0045 makes the format a compatibility concern from v0.1.0
on. The same
record types are produced by the agent, by the log importer and by the backend itself (gap records, 0028),
so the Go model is shared under `internal/` (0073). The ingest wire format is external input, a security
area (`.squad/project.md`, *Security areas* 10): the decoder must turn hostile input into an error, never a
crash, a hang or an unbounded allocation. Every new dependency is a `security`-tier change and has to be
watched by `govulncheck` for the life of the project.

## Options considered

1. **JSON Lines (one JSON object per line), gzip-compressed, `encoding/json` + `compress/gzip`** — no
   dependency; streamable line by line with a hard per-line bound; readable with `zcat | jq` while
   debugging an outage; gzip of repetitive JSON keys reaches a ratio that makes the larger raw size
   irrelevant at the volumes of one server. Cons: larger and slower than a binary encoding; `encoding/json`
   matches keys case-insensitively, with Unicode case folding (`"Kind"` is read as `kind`), and lets the last duplicate key win (both handled, see the format
   description).
2. **JSON Lines with zstd (`github.com/klauspost/compress/zstd`)** — better ratio and speed than gzip. Cons:
   a dependency on both binaries for a gain that does not matter at this volume; zstd decoders need
   explicit window and memory limits against hostile input.
3. **CBOR (`github.com/fxamacker/cbor`)** — compact, binary, schema-less, has decoder limits. Cons: a
   dependency; not readable without tooling; the compactness is mostly absorbed by compression anyway.
4. **Protocol Buffers** — compact, explicit schema evolution through field numbers. Cons: a dependency plus
   code generation in the build; `protoc` tooling in CI; not readable without tooling; length-delimited
   streaming is not part of the core format.
5. **One JSON document per batch** — simplest to write. Cons: not streamable; the whole batch must be
   parsed before the first record can be validated, and a per-record size bound is not possible.

## Decision

Option 1. A batch is a gzip stream (RFC 1952) whose decompressed content is UTF-8 JSON Lines: the first
line is the batch header, every further line is one record envelope (`kind`, `source`, `seq`,
`captured_at`, `data`). Only the Go standard library is used (`encoding/json`, `compress/gzip`,
`bufio`, `net/netip`, `time`). The shared record types live in `internal/model`, the header, encoder and
decoder in `internal/wire`. Like `encoding/json`, the API works on a caller-supplied `io.Reader` /
`io.Writer` without a `context.Context`: it performs no I/O of its own, and the caller bounds blocking
through the reader (for the ingest API the HTTP server's timeouts and body size limit). The exact format is
described in `docs/WIRE_FORMAT.md`.

## Consequences

- No new dependency; `go.mod` is unchanged, and `govulncheck` covers the codec through the standard library.
- Batches can be inspected with standard tools during an incident.
- The encoding is larger before compression than a binary one; if the volume ever matters (for example
  several monitored servers, #89), zstd or a binary encoding can be introduced as a new major version
  (0043).
- `encoding/json` v1 replaces invalid UTF-8 in strings with U+FFFD on encode and decode, so a log line
  with invalid bytes is not stored byte for byte; 0021 (no pseudonymization) is not affected.
