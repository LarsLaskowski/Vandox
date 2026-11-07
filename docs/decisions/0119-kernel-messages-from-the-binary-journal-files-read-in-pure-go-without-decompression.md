# 0119: Kernel messages from the binary journal files, read in pure Go without decompression

- **Status:** Accepted
- **Date:** 2026-10-22
- **Area:** Agent kernel events and boots
- **Source:** Issue #36
- **Supersedes:** —

## Context

#36 asks for OOM kills "from the kernel messages in the journal", recorded within seconds. The agent is a static binary
(`CGO_ENABLED=0`), starts no programs (0019), holds only the rights 0030 names (the group `systemd-journal` among them) and must not
add memory pressure (0025). No journal reader existed. The server's journal was inspected on two real files the Product Manager
copied from it (not committed): systemd 249.11, a 256-byte header, the incompatible flags `COMPRESSED-ZSTD` and `KEYED-HASH`
without `COMPACT`, zstd-compressed data objects only for other entries (none of the 3,563 kernel entries references a compressed
object; the kill lines are 159 to 171 bytes), and 221 entries of the kernel transport whose facility is not 0 (userspace writes to
`/dev/kmsg`). How a kill is timed is decided in
[0153](0153-a-kill-is-timed-by-its-receipt-corrected-for-the-kernel-clocks-drift.md), the compact layout of newer systemd versions
in [0154](0154-the-journal-reader-reads-the-compact-layout-chosen-per-file-from-the-headers-flag.md).

## Options considered

1. **Read the binary journal files in Go, through reads at offsets, with a keyed-hash lookup of `_TRANSPORT=kernel` and its entry
   list** (chosen) — no program, no cgo, no new right, memory independent of the journal; the format reader is the agent's own
   (security area 10) and must be built from the real files. A full scan of each file's objects instead of the hash lookup needs no
   hash function to get right, but reads 80 MiB per file and per start.
2. **Run `journalctl -f -o export` as a child** — a parser of a text format only, but a program started and kept alive, which 0019
   rules out: it fails or is killed under exactly the memory pressure that matters.
3. **Read `/dev/kmsg`** — a simple format and no journal, but reading it needs `CAP_SYSLOG` on a kernel with `dmesg_restrict`, a new
   capability against 0030, and the ring buffer loses what happened while the agent was stopped.
4. **Link `sd-journal` through cgo** — the reference implementation, but the binary would no longer be static and would carry
   systemd's C library.
5. **Follow rsyslog's `/var/log/kern.log`** — plain text, but rsyslog may not be installed, the line carries no proof of kernel
   origin and the times have whole seconds.
6. **Decompress fields with a zstd library** (with option 1) — would read every field, but adds a dependency and a decompression
   surface for nothing the agent needs: journald compresses only fields whose whole `NAME=value` payload is at least 512 bytes
   (systemd v249 `DEFAULT_COMPRESS_THRESHOLD`), and no kill line comes near it.
7. **Skip set-aside files (`*.journal~`)** — simpler, but journald sets a file aside after every unclean close (a crash, a hard
   reset, journald itself killed) and starts the next one with a new seqnum ID, so exactly the kills before an outage would be lost;
   they are read as archived files, a torn tail ends the file (chosen).

## Decision

The agent reads the system journal files itself, set-aside files included, counts only entries of the kernel transport with
facility 0 as kills and decompresses nothing: an entry it cannot read that way is a recorded gap, never a kill. The rules are in
[Agent kernel events and boots](../areas/agent-kernel-events.md).

## Consequences

- A journal written with a compression threshold below the size of a kill line or without keyed hashes loses its kernel events to
  recorded gaps until the reader learns that layout.
- Every chain in a file is followed only to larger offsets, a bounded number of entry arrays per chain, and a poll finds its
  position by bisection, so neither a damaged file nor a flood of kernel lines (a firewall's log lines are remote-triggered) can
  make a run loop or grow with the entries before the position.
- An item's name is looked for in the first 64 bytes of its payload, so a wanted name of exactly 64 bytes (systemd's maximum)
  would never match; accepted, because every name the kernel and log collectors want is far shorter and an unmatched item is
  passed over like any unwanted one.
- journald's entry about missed kernel messages is not in the real files; it is built from the systemd 249 source (residual).
- The committed fixtures are generated from an anonymized export by a test-only writer whose output `journalctl --verify` and
  `journalctl -o export` must accept; the reader is checked against the real files only on a developer's machine
  (`VANDOX_REAL_JOURNAL_DIR`), and CI sees the generated fixtures.
- Log shipping reuses the reader for other fields and adds a bounded zstd path that only the log collector uses
  ([0121](0121-log-shipping-decodes-zstd-journal-items-with-a-bounded-library-decoder.md)); the kernel collector still
  decompresses nothing.
