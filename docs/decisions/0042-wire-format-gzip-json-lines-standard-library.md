# 0042: Wire format is gzip-compressed JSON Lines, built on the standard library only

- **Status:** Accepted
- **Date:** 2026-10-04
- **Area:** Wire format
- **Source:** Issue #10
- **Supersedes:** —

## Context

The agent sends its records to the backend in batches (0045). The format has to be compact on the link and on the 2 GB server, streamable so
the backend never holds a whole batch to start validating it, and easy to evolve, because it is a compatibility concern from v0.1.0 on. The
ingest wire format is external input, a security area (`.squad/project.md`, *Security areas* 10): the decoder must turn hostile input into an
error, never a crash, a hang or an unbounded allocation. Every new dependency is a `security`-tier change that has to be watched for the life
of the project.

## Options considered

1. **JSON Lines, gzip-compressed, standard library only** (chosen) — no dependency; streamable line by line with a hard per-line bound; readable with `zcat | jq` during an outage; gzip of repetitive keys makes the larger raw size irrelevant at the volume of one server. Larger and slower than binary; the standard JSON parser's lenient key matching (case-insensitive, last duplicate wins) is narrowed by the format rules (ASCII keys matched ignoring case, duplicates rejected, [0090](0090-wire-keys-matched-ignoring-ascii-case-duplicates-rejected-header-shape-malformed.md)).
2. **JSON Lines with zstd** — better ratio and speed, but a dependency on both binaries for a gain that does not matter at this volume, and decoders need explicit window and memory limits against hostile input.
3. **CBOR** — compact and schema-less, but a dependency, unreadable without tooling, and compression absorbs most of the compactness.
4. **Protocol Buffers** — explicit schema evolution, but a dependency plus code generation in the build, and no readable form.
5. **One JSON document per batch** — simplest, but not streamable and no per-record size bound.

## Decision

Option 1. The layout and all rules are in the [Wire format](../areas/wire-format.md) area.

## Consequences

- No new dependency; the vulnerability scan covers the codec through the standard library.
- Batches can be inspected with standard tools during an incident.
- The encoding is larger before compression; if the volume ever matters (several monitored servers, #89), zstd or a binary encoding can come in as a new major version (0043).
- Invalid UTF-8 in strings is replaced by U+FFFD on encode and decode, so a log line with invalid bytes is not stored byte for byte; 0021 (no pseudonymization) is not affected.
