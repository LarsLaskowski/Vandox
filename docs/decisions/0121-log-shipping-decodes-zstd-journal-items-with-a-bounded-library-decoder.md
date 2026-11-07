# 0121: Log shipping decodes zstd journal items with a bounded library decoder

- **Status:** Accepted
- **Date:** 2026-10-31
- **Area:** Agent log shipping
- **Source:** Issue #37
- **Supersedes:** —

## Context

#37 ships the journal live, without loss or duplicates across a restart. The agent already reads the binary journal in pure Go for
the kernel collector ([0119](0119-kernel-messages-from-the-binary-journal-files-read-in-pure-go-without-decompression.md)), which
decompresses nothing. Three copies of the server's journal (inspected read-only, not committed) held 340 compressed data objects, all
zstd (`MESSAGE`, `SYSLOG_RAW`, `_CMDLINE`): every frame single-segment with a stated content size, no checksum, no dictionary and one
compressed block, the content 512 to 19,718 bytes. The agent must stay static, start no program (0019) and bound its memory (0025),
and a local writer controls what the decoder reads (security area 10).

## Options considered

1. **Decode with `github.com/klauspost/compress/zstd`** (chosen) — pure Go, maintained, refuses a frame whose window or content size
   exceeds a configured bound before allocating; one new dependency and decoder surface in security area 10.
2. **A hand-written zstd decoder** — no dependency, but Huffman and FSE decoding of every compressed block the server writes is a
   large, new parser of hostile input for one feature.
3. **Leave compressed items unread** (as 0119) — no new code, but every message of 512 bytes or more would be a gap, against the spec
   (S9).
4. **`journalctl -f -o export` as a child, or `sd-journal` through cgo** — ruled out by 0019 and 0119 (a program under memory
   pressure, a non-static binary).

## Decision

The `logs` collector decodes zstd journal items through option 1's library under a fixed bound, and any other compression, or a
frame outside the bound, is a gap. The bounds are in [Agent log shipping](../areas/agent-log-shipping.md).

## Consequences

- The agent gains its first dependency besides the YAML library; `govulncheck` covers it, and its decoder bounds are a security
  property checked by a heap test.
- The bounds are chosen against hostile input, not only against the frames the server writes: only an object that holds exactly one
  single-segment frame with a stated size is decoded, into one fixed buffer, and the items per entry are capped, because the library
  stops other frames only after their output has passed the bound, and one crafted entry could otherwise use most of a run.
- Messages of the server's real size are shipped whole; a writer that crafts a larger or differently framed item gets a recorded gap,
  never an unbounded allocation.
