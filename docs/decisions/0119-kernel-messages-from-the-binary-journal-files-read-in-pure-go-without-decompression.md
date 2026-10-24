# 0119: Kernel messages from the binary journal files, read in pure Go without decompression

- **Status:** Accepted
- **Date:** 2026-10-22
- **Area:** Agent
- **Source:** Issue #36, issue #286
- **Supersedes:** —

## Context

#36 asks for OOM kills "from the kernel messages in the journal", recorded within seconds. The agent is a static binary
(`CGO_ENABLED=0`), starts no programs (0019), holds only the rights 0030 names (the group `systemd-journal` among them) and must not
add memory pressure (0025). No journal reader existed. The server's journal was inspected on two real files the Product Manager
copied from it (not committed): systemd 249.11, a 256-byte header, the incompatible flags `COMPRESSED-ZSTD` and `KEYED-HASH`
without `COMPACT`, zstd-compressed data objects only for other entries (none of the 3,563 kernel entries references a compressed
object; the kill lines are 159 to 171 bytes), and 221 entries of the kernel transport whose facility is not 0 (userspace writes to
`/dev/kmsg`).

systemd 252 and later write the `COMPACT` layout by default (systemd 255 `journal-file.c`: `SYSTEMD_JOURNAL_COMPACT` unset means
compact): 32-bit offsets in entry items and entry arrays, the item hashes dropped, and two 32-bit tail fields before the payload of a
data object. An upgrade of the server (Ubuntu 24.04 ships systemd 255) would make the reader refuse every file (#286). The compact
layout was read from two files that `systemd-journal-remote` of systemd 255 wrote from the committed, anonymized exports; the real
compact files of the server are checked later on a developer's machine, like the 249 files.

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
   surface for nothing the agent needs: journald compresses only fields whose whole `NAME=value` payload is at least 512 bytes (systemd v249
   `DEFAULT_COMPRESS_THRESHOLD`), and no kill line comes near it.
7. **Find the kernel entries by a full scan of each file's objects** (instead of the hash lookup) — no hash function to get right,
   but 80 MiB read per file and per start.
8. **Skip set-aside files (`*.journal~`)** — simpler, but journald sets a file aside after every unclean close (a crash, a hard reset,
   journald itself killed) and starts the next one with a new seqnum ID, so exactly the kills before an outage would be lost; they are
   read as archived files, a torn tail ends the file.
9. **Kill time as the boot time plus the kernel's stamp** — exact in principle, but the kernel's printk clock runs slow against the
   monotonic and real-time clocks (about 33 µs per second on the server, a minute after three weeks), so the time comes out early; the
   receipt time minus the delay, corrected by the drift seen on recent lines, is used, and liveness is judged on the journal's monotonic
   clock. "Recent" is a span of receipt time, not a count of lines: the drift only grows, so on a quiet boot the smallest lag of the
   last 64 lines is that of a line days old and overstates the delay (by 5.8 s for the 2 Oct `pzstd` kill).
10. **Commit `systemd-journal-remote` output as the fixture** — easy to produce, but systemd 255 here writes only the `COMPACT`
   variant, which the server never writes, so it would test the wrong layout; an anonymized copy of a real 80 MiB file cannot be
   made either, because rewriting a field breaks its keyed hash. For the `COMPACT` layout (#286) this output is exactly the layout to
   test and is committed as its real sample, with the header's machine ID (that of the machine that ran it) replaced; residual: it is
   `systemd-journal-remote`'s output, not journald's, and holds no set-aside file.
11. **Read `COMPACT` with the same reader, the item widths and the payload offset taken per file from the header's flag** (chosen,
   #286) — one set of bounds checks for both layouts; the flag is untrusted input like every other header field, so a flipped flag must
   end in `malformed file` or a misread within the file's bounds, never a panic or an unbounded read.
12. **A second reader for the compact layout** — no layout switch in the shared code, but every bounds check twice.
13. **Use the tail fields of the larger header and of the compact data objects** (`tail_entry_array_offset`, `tail_entry_offset`) —
   they let the writer append without walking a chain; the reader's bisection already needs a fixed number of reads, and trusting them
   would add input to bound for nothing.
14. **Commit the journal-remote samples cut after their last object** — 7.6 MiB less each in the working tree, but the files would no
   longer be as systemd wrote them; Git stores the 8 MiB of mostly zeros in about 20 KB each.

## Decision

The agent reads the system journal files itself, set-aside files included (options 1 and 8), and times a kill by its receipt corrected for the kernel clock's drift (option 9). It counts only entries of the kernel transport with exactly one facility `0`,
decompresses nothing (a compressed item, whose name cannot be known without decompressing it, makes a kernel entry unreadable,
recorded as a gap; so do items beyond the per-entry cap of kept fields), and supports only the two
keyed-hash layouts journald writes, the non-compact one of the server (systemd 249) and `COMPACT` (systemd 252 and later), chosen per
file from the header's flag (option 11); any other layout fails closed, also as a gap. The committed fixtures are generated in both
layouts from an anonymized export by a test-only writer whose output `journalctl --verify` and `journalctl -o export` must accept,
and the compact layout is also read from the committed `systemd-journal-remote` samples. The rules are in the
[Agent](../areas/agent.md) area (*Kernel events*).

## Consequences

- A journal written with a compression threshold below the size of a kill line or without keyed hashes loses its kernel events to
  recorded gaps until the reader learns that layout; a systemd release after 255 that changes the layout again is a reason to revisit
  this record.
- The compact samples are `systemd-journal-remote` output and hold some entries twice (it ran twice into one file), so `journalctl
  --verify` reports their timestamps out of order although every object is well-formed; the reader's result is pinned against the
  committed exports the samples were written from.
- Every chain in a file is followed only to larger offsets, at most 64 entry arrays per chain, and a poll finds its position by
  bisection, so neither a damaged file nor a flood of kernel lines (a firewall's log lines are remote-triggered) can make a run loop or
  grow with the entries before the position.
- journald's entry about missed kernel messages is not in the real files; it is built from the systemd 249 source (residual).
- The reader is checked against the real files only on a developer's machine (`VANDOX_REAL_JOURNAL_DIR`), the server's compact files
  once they exist; CI sees the generated fixtures and the compact samples.
- #37 (log shipping) reuses the reader for other fields and adds a bounded zstd path that only the log collector uses
  ([0121](0121-log-shipping-journal-and-syslog-files-positions-per-seqnum-id-zstd-commit-when-kept.md)); the kernel collector still
  decompresses nothing.
