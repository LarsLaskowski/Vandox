# 0154: The journal reader reads the COMPACT layout with the same code, chosen per file from the header's flag

- **Status:** Accepted
- **Date:** 2026-10-22
- **Area:** Agent kernel events and boots
- **Source:** Issue #286, Issue #307
- **Supersedes:** —

## Context

The journal reader ([0119](0119-kernel-messages-from-the-binary-journal-files-read-in-pure-go-without-decompression.md)) was built for
the layout of systemd 249 on the server. systemd 252 and later write the `COMPACT` layout by default (systemd 255 `journal-file.c`:
`SYSTEMD_JOURNAL_COMPACT` unset means compact): 32-bit offsets in entry items and entry arrays, the item hashes dropped, and two
32-bit tail fields before the payload of a data object. An upgrade of the server (Ubuntu 24.04 ships systemd 255) would make the reader
refuse every file (#286). The compact layout was read from two files that `systemd-journal-remote` of systemd 255 wrote from the
committed, anonymized exports; the real compact files of the server are checked later on a developer's machine, like the 249 files.
The header is untrusted input like every other field, and the online file's header is read again while journald writes (#307).

## Options considered

1. **Read `COMPACT` with the same reader, the item widths and the payload offset taken per file from the header's flag** (chosen) —
   one set of bounds checks for both layouts; a flipped flag must end in `malformed file` or a misread within the file's bounds,
   never a panic or an unbounded read.
2. **A second reader for the compact layout** — no layout switch in the shared code, but every bounds check twice.
3. **Use the tail fields of the larger header and of the compact data objects** — they let the writer append without walking a
   chain; the reader's bisection already needs a fixed number of reads, and trusting them would add input to bound for nothing.
4. **Fixtures: commit `systemd-journal-remote` output** — easy to produce, and for the compact layout exactly the layout to test, so
   it is committed as its real sample with the header's machine ID replaced (chosen for `COMPACT`); for the server's layout it would
   test the wrong variant, and an anonymized copy of a real 80 MiB file cannot be made, because rewriting a field breaks its keyed
   hash. Cutting the samples after their last object would save 7.6 MiB each in the working tree, but they would no longer be as
   systemd wrote them; Git stores the 8 MiB of mostly zeros in about 20 KB each.
5. **Re-read an entry array's size before every slot a cursor reads from it, or re-check a cursor's cached array after each header
   re-read** (#307) — would also catch an array shrunk in place, but costs a read per entry or per poll and extra state, and buys
   nothing: the bytes behind a shrunk array are validated like any slot value. Rejected for checking every read from a cached array
   against the current header's arena and the file like every other read (chosen).

## Decision

The reader supports exactly the two keyed-hash layouts journald writes, the server's and `COMPACT`, chosen per file from the
header's flag, refuses every other layout as a recorded gap, and bounds every read, also from an array a cursor kept across a header
re-read, by the current header's arena and the file. The rules are in
[Agent kernel events and boots](../areas/agent-kernel-events.md) (*Layout (security area 10)*).

## Consequences

- A systemd release after 255 that changes the layout again is a reason to revisit this record.
- The compact samples are `systemd-journal-remote` output and hold some entries twice (it ran twice into one file), so `journalctl
  --verify` reports their timestamps out of order although every object is well-formed; the reader's result is pinned against the
  committed exports the samples were written from. Residual: they are not journald's own output and hold no set-aside file.
- The fixtures of both layouts are generated from the anonymized export by the test-only writer; the server's own compact files are
  checked once they exist.
