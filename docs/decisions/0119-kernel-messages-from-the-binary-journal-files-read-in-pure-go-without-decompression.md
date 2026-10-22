# 0119: Kernel messages from the binary journal files, read in pure Go without decompression

- **Status:** Proposed
- **Date:** 2026-10-22
- **Area:** Agent
- **Source:** Issue #36
- **Supersedes:** —

## Context

#36 asks for OOM kills "from the kernel messages in the journal", recorded within seconds. The agent is a static binary
(`CGO_ENABLED=0`), starts no programs (0019), holds only the rights 0030 names (the group `systemd-journal` among them) and must not
add memory pressure (0025). No journal reader existed. The server's journal was inspected on two real files the Product Manager
copied from it (not committed): systemd 249.11, a 256-byte header, the incompatible flags `COMPRESSED-ZSTD` and `KEYED-HASH`
without `COMPACT`, zstd-compressed data objects only for other entries (none of the 3,563 kernel entries references a compressed
object; the kill lines are 159 to 171 bytes), and 221 entries of the kernel transport whose facility is not 0 (userspace writes to
`/dev/kmsg`).

## Options considered

1. **Read the binary journal files in Go, through `pread`, with a keyed-hash lookup of `_TRANSPORT=kernel` and its entry list**
   (chosen) — no program, no cgo, no new right, memory independent of the journal; the format reader is the agent's own (security
   area 10) and must be built from the real files.
2. **Run `journalctl -f -o export` as a child** — a parser of a text format only, but a program started and kept alive, which 0019
   rules out: it fails or is killed under exactly the memory pressure that matters.
3. **Read `/dev/kmsg`** — a simple format and no journal, but reading it needs `CAP_SYSLOG` on a kernel with `dmesg_restrict`, a new
   capability against 0030, and the ring buffer loses what happened while the agent was stopped.
4. **Link `sd-journal` through cgo** — the reference implementation, but the binary would no longer be static and would carry
   systemd's C library.
5. **Follow rsyslog's `/var/log/kern.log`** — plain text, but rsyslog may not be installed, the line carries no proof of kernel
   origin and the times have whole seconds.
6. **Decompress fields with a zstd library** (with option 1) — would read every field, but adds a dependency and a decompression
   surface for nothing the agent needs: journald compresses only fields of at least 512 bytes, and no kill line comes near it.
7. **Find the kernel entries by a full scan of each file's objects** (instead of the hash lookup) — no hash function to get right,
   but 80 MiB read per file and per start.
8. **Commit `systemd-journal-remote` output as the fixture** — easy to produce, but systemd 255 here writes only the `COMPACT`
   variant, which the server never writes, so it would test the wrong layout; an anonymized copy of a real 80 MiB file cannot be
   made either, because rewriting a field breaks its keyed hash.

## Decision

The agent reads the system journal files itself (option 1). It counts only entries of the kernel transport with facility 0,
decompresses nothing (a needed field that is compressed makes the entry unreadable, recorded as a gap), and supports only the
keyed-hash, non-compact layout the server writes; any other layout fails closed, also as a gap. The committed fixtures are generated
in that layout from an anonymized export by a test-only writer whose output `journalctl --verify` and `journalctl -o export` must
accept. The rules are in the [Agent](../areas/agent.md) area (*Kernel events*).

## Consequences

- A journal written with a compression threshold below the size of a kill line, in the `COMPACT` layout (systemd 252 and later) or
  without keyed hashes loses its kernel events to recorded gaps until the reader learns that layout; an upgrade of the server's
  systemd is a reason to revisit this record.
- journald's entry about missed kernel messages is not in the real files; it is built from the systemd 249 source (residual).
- The reader is checked against the real files only on a developer's machine (`VANDOX_REAL_JOURNAL_DIR`); CI sees the generated
  fixtures.
- #37 (log shipping) can reuse the reader for other fields.
