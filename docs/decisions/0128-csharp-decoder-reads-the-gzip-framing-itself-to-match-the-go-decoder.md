# 0128: The C# decoder reads the gzip framing itself, so trailing bytes and header rules match the Go decoder

- **Status:** Proposed
- **Date:** 2026-10-25
- **Area:** Wire format
- **Source:** Issue #40
- **Supersedes:** 0076 (amends it: the decoder no longer relies on `GZipStream`; the strict validation switch stays for every other
  gzip reader)

## Context

The [Wire format](../areas/wire-format.md) area says its rules hold for both decoders and that bytes after the last gzip member are
rejected. `GZipStream` with strict validation (record 0076) still accepts trailing bytes that do not start a gzip header (measured on
.NET 10.0.402 for 20 garbage bytes, one byte and 16 zero bytes) and imposes none of the Go reader's header limits, so the backend,
which takes the agent's input, would accept batches the Go decoder refuses. `GZipStream` does not report how many input bytes a member
used.

## Options considered

1. **Keep `GZipStream` and document the divergence** — the documented rule would be false for the decoder that faces the network.
2. **Inflate twice: `GZipStream` for the content, a second walk to find the members' ends** — double the CPU per batch.
3. **Read the header and trailer by hand and inflate each member with a raw `DeflateStream` fed one byte per read** (chosen) — a raw
   inflater fed that way stops exactly at the member's end (measured: 2,000,615 of 2,000,615 bytes consumed, the three trailing bytes
   left), so the reader checks the trailer, the next header or the end exactly as Go's `compress/gzip` does; one-byte reads cost about
   0.5 s for 2 MB of incompressible input on the development host and a few milliseconds for an agent batch.
4. **CRC-32 from the `System.IO.Hashing` package** instead of a table in code — a new dependency for 20 lines.

## Decision

Option 3 with a CRC-32 of its own: `BatchDecoder` reads gzip through a reader of the RFC 1952 framing that follows Go's
`compress/gzip` rules, and the shared decoder cases pin the framing for both decoders. The rules are in the Wire format area
(*Accepted forms*).

## Consequences

- Both decoders refuse the same gzip inputs, and the shared fixture checks it.
- Inflating costs more CPU per compressed byte; the ingest body is at most 2 MiB, so a request costs well under its 30-second bound.
- The log import keeps `GZipStream` with the strict switch: trailing bytes after a log archive are not a wire-format concern.
