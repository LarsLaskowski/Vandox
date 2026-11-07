# 0155: The kernel collector commits its journal position only after a kept run, checks its own records and caps the re-reads

- **Status:** Accepted
- **Date:** 2026-10-22
- **Area:** Agent kernel events and boots
- **Source:** Issue #36
- **Supersedes:** —

## Context

The kernel collector reads the kills from the journal
([0119](0119-kernel-messages-from-the-binary-journal-files-read-in-pure-go-without-decompression.md)) and keeps its position in the
agent's state ([0156](0156-one-agent-state-file-in-the-spool-directory-written-by-a-state-job.md)). A run's result can be dropped
after it returned (a timeout, an error, a sink error), and the scheduler tells the next run so through its lost-sample flag
([0110](0110-agent-runtime-frame-runner-per-collector-gaps-for-every-missed-sample.md)). Storage deduplicates records by sequence
number, not by kill identity. The question came to the Product Manager as escalation B6.

## Options considered

1. **Commit the position when the run returns** — simpler, but moves past kills whose records were lost without a gap (rejected by
   the Product Manager as option 3 of the B6 escalation).
2. **Commit the position at the next run when the previous one was kept** (chosen) — a run whose result was dropped is read again
   (same identities). A record the scheduler would refuse must not hold the position back, or one such record makes the same span
   resend forever.
3. **A scheduler signal telling a refused record from a dropped result** (a new field of a run) — the same effect, but a change to the
   contract `processes` and `connections` also rely on.
4. **The collector checks its own records and caps the re-reads** (chosen by the Product Manager): every record passes the
   scheduler's own check before it is returned, a failing one becomes a gap that is valid by construction, so the lost-sample flag
   can only mean a dropped result, and a cap on re-reads of the same position ends any other loop with a gap; the scheduler contract
   stays as it is.

## Decision

The kernel collector commits what a run reached as one unit at the next run, only when that run's result was kept, checks its own
records before returning them and gives a span up with a gap after a fixed number of dropped re-reads. The rules are in
[Agent kernel events and boots](../areas/agent-kernel-events.md) (*Kernel events*, **Commit and re-read**).

## Consequences

- What a run reached (the position, the drift window, the files of other seqnum IDs already read) is committed as one unit, so a
  re-read repeats the run exactly; only files with a head time and the online file set the position, so a file of another seqnum ID
  can neither move it back in time nor hide the online file, and a file that keeps failing is given up with a gap instead of
  holding the position.
- A span whose result is dropped more than the cap allows in a row is given up with a gap; its kills are then lost although the
  journal may still hold them, for example during a sink error that outlasts the re-reads even when the sink recovers soon after.
