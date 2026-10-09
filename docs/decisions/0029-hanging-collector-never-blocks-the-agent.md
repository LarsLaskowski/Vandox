# 0029: A hanging collector or database never blocks the agent

- **Status:** Accepted
- **Date:** 2026-10-04
- **Area:** —
- **Source:** Issue #7
- **Supersedes:** —

## Context

The failure Vandox is built to explain is memory exhaustion followed by the OOM killer and MariaDB, Plesk and
mail going down (`docs/ARCHITECTURE.md`, *Monitored server*). In exactly that situation a read from `/proc`,
a systemd D-Bus call, a journal read or a query on the MariaDB socket can hang for a long time. If one
collector or the database stalls the agent, the agent stops recording at the moment its data matters most.

Not every source can be cancelled. `database/sql` and a D-Bus client take a `context.Context`, but a file
read from `/proc` or `/sys` (`os.ReadFile`, `fs.File.Read`) takes none: when the kernel blocks the read
under memory pressure, Go cannot interrupt it, and a deadline can only stop waiting for the goroutine that
is stuck in it.

## Options considered

1. **Collect sequentially without limits** — simple; one hung source stops all collection, spooling and
   sending.
2. **Sequential collection with per-call timeouts** — bounded, but a slow source still delays every other
   collector of the same cycle.
3. **Cancel every call to a source at its deadline** — rejected: it promises something Go cannot do for
   file reads from `/proc` and `/sys`, which take no context and cannot be interrupted while the kernel
   blocks them.
4. **Each collector isolated and abandoned at its deadline** — every collector runs in its own goroutine
   under a deadline; the agent waits for it at most until the deadline, then abandons it and records its
   sample as a gap (0028), while the other collectors, the spool and the sender keep running. Sources that
   accept a context (MariaDB, D-Bus, the journal reader where its API allows) also receive it, so they can
   stop early.

## Decision

Option 4 is a guarantee: a hanging collector or a hanging database never blocks the agent. Each collector
is abandoned at its deadline and its missed sample is recorded as a gap; a context with that deadline is
passed to every source whose API accepts one. While a collector is still stuck in a previous run it is not
started again; each cycle in which it is skipped is recorded as a gap as well.

## Consequences

- Every collector needs a deadline and a test with a hanging double of its source: for `/proc` and `/sys` a
  filesystem wrapper whose reads block until the test releases them; for MariaDB, D-Bus and the journal a
  fake that blocks until its context is cancelled. The test checks that the cycle completes on time, a gap
  is recorded and the stuck collector is not started a second time.
- To make that possible, collectors read `/proc` and `/sys` through a filesystem interface (`io/fs.FS`,
  in production `os.DirFS("/proc")`), never through hard-coded paths.
- A hung source costs at most one goroutine per collector, not one per cycle; an abandoned goroutine ends
  only when the kernel returns from the read.
- Missed samples become recorded gaps (0028) instead of silent holes or a stalled agent.
