# 0160: Log shipping keeps a journal position per seqnum ID

- **Status:** Accepted
- **Date:** 2026-10-31
- **Area:** Agent log shipping
- **Source:** Issue #37
- **Supersedes:** —

## Context

#37 ships the journal live, without loss or duplicates across a restart. The kernel collector keeps one journal position
([0119](0119-kernel-messages-from-the-binary-journal-files-read-in-pure-go-without-decompression.md)). Three copies of the server's
journal (an online and an archived system file and a user file) showed every transport and a `user-10000.journal` whose seqnum ID
differs from that of both system files while its sequence numbers continue the system counter: the files are not one ordered
sequence. The positions live in the agent's state file, whose size is bounded.

## Options considered

1. **One journal position, as the kernel collector keeps** — simple, but it assumes one ordered sequence across files, which the
   server's user file contradicts.
2. **A position per seqnum ID, the files of one ID merged by sequence number** (chosen) — needs no assumption about how journald
   assigns IDs to user files.

## Decision

The `logs` collector reads every system and user journal file of the machine's journal directory and keeps a position per seqnum
ID. How many positions are saved, where an ID without a position starts and when a span is a gap are in
[Agent log shipping](../areas/agent-log-shipping.md); the state file in [Agent](../areas/agent.md) (*Agent state*).

## Consequences

- Positions per seqnum ID need room for many user journals, so the state file's bound had to rise.
- An ID without a position is read from its start only when it is newer than the newest position held, so a forgotten ID, a journal
  turned on later or more IDs than the bound never make a file be shipped twice; an ID started at its end records a gap where the
  skipped part may hold newer lines (a clock step back, an unsaved ID after a restart), so such lines are never lost silently.
- The positions favor no loss over no gap: the newest position is never forgotten (forgetting it would let the time rule read a
  forgotten ID again), and a vacuum is judged only where no file of the ID holds the position, so it may report a gap where only
  another ID's sequence numbers lay between.
- Lines are ordered per source, not across sources.
- The same principle holds in the rest of the collector: what a local writer controls (repeated item names, very long lines, a
  rotation during a read) must not hide lines without a gap, and the agent refuses to start when the collector cannot be built rather
  than run without it.
