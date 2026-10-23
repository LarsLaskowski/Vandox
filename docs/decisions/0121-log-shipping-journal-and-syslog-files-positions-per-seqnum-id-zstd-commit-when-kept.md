# 0121: Log shipping reads the journal per seqnum ID with bounded zstd decoding and syslog files without links, and commits its positions when the scheduler keeps a run

- **Status:** Proposed
- **Date:** 2026-10-23
- **Area:** Agent
- **Source:** Issue #37
- **Supersedes:** —

## Context

#37 ships the journal and configured `syslog`-format files live, without loss or duplicates across a restart
([spec](../../specs/feature-37-log-shipping/spec.md)). The agent already reads the binary journal in pure Go for the kernel collector
([0119](0119-kernel-messages-from-the-binary-journal-files-read-in-pure-go-without-decompression.md)), which decompresses nothing and
keeps one position. Three copies of the server's journal (an online and an archived system file and a user file, inspected read-only and
not committed) showed: every transport (`syslog`, `stdout`, `journal`, `kernel`, `audit`, `driver`) and audit entries without
`PRIORITY`; 340 compressed data objects, all zstd (`MESSAGE`, `SYSLOG_RAW`, `_CMDLINE`), every frame single-segment with a 2-byte
content size, no checksum, no dictionary and one compressed block, content (and so window) 512 to 19,718 bytes, messages up to 19,710
bytes; and a `user-10000.journal` whose seqnum ID differs from that of both system files while its sequence numbers continue the system
counter. The agent must stay static, start no program (0019), add no right (0030) and bound its memory (0025); a service user can
replace its own log file with a link, and the agent can read every file.

## Options considered

1. **Decode zstd items with `github.com/klauspost/compress/zstd`** (chosen) — pure Go, maintained, refuses a frame whose window or
   content size exceeds a configured bound before allocating; one new dependency and decoder surface in security area 10.
2. **A hand-written zstd decoder** — no dependency, but Huffman and FSE decoding of every compressed block the server writes is a large,
   new parser of hostile input for one feature.
3. **Leave compressed items unread** (as 0119) — no new code, but every message of 512 bytes or more would be a gap, against the spec
   (S9).
4. **`journalctl -f -o export` as a child, or `sd-journal` through cgo** — ruled out by 0019 and 0119 (a program under memory pressure,
   a non-static binary).
5. **One journal position, as the kernel collector keeps** — simple, but the user file's own seqnum ID means files are not one ordered
   sequence; a position per seqnum ID (chosen), with the files of one ID merged by sequence number, needs no assumption about how
   journald assigns IDs to user files.
6. **Commit at the start of the next run only, as the kernel collector does** — no change to the scheduler, but a stop cannot know
   whether the last run was kept, so a restart repeats or loses that run (S4); a scheduler callback when a run's records were kept
   (chosen) commits at once and lets the agent save the state once more when it stops.
7. **Store the configured paths in the state file** — readable, but 16 paths of 1,024 bytes at JSON's worst escaping exceed the 16 KiB
   bound of the state file; storing a SHA-256 of the path (chosen) keeps that bound unchanged.
8. **Refuse links with `openat2(RESOLVE_NO_SYMLINKS)`** — one call, but needs `golang.org/x/sys/unix` as a direct dependency; opening
   each element of the path with `openat` and `O_NOFOLLOW` from `/` (chosen) gives the same refusal with the standard library, without
   a window between check and open. `os.Root` was not taken: it follows links inside the root.

## Decision

The `logs` collector reads every system and user journal file of the machine's journal directory with a position per seqnum ID,
decodes zstd items through option 1's library under a fixed bound (any other compression is a gap), reads configured files only
through a link-free `openat` walk, and commits its positions when the scheduler reports the run kept (a new optional callback), giving
up a span after the fourth dropped run as the kernel collector does. The rules are in the [Agent](../areas/agent.md) area (*Log
shipping*) and the [Configuration and secrets](../areas/configuration-and-secrets.md) area (`log_shipping`).

## Consequences

- The agent gains its first dependency besides the YAML library; `govulncheck` covers it, and its decoder bounds are a security
  property checked by a heap test.
- Positions per seqnum ID cost up to 32 entries in the state file; an ID whose files are all archived and read to their end is forgotten.
- A crash repeats at most one state interval plus one collector interval of lines (the spec's E1); exact delivery across a crash needs
  the spool (#38) to store positions with the records.
- Live lines carry no classification (`event`, mail markers) until #290; the error log formats follow in #289.
- A systemd upgrade to the `COMPACT` layout loses log lines to gaps, as for the kernel events (0119).
