# 0068: Write throughput is measured by a Go benchmark and never asserted in tests; the DS918+ measurement is a follow-up issue

- **Status:** Superseded by 0077
- **Date:** 2026-10-06
- **Source:** Issue #14
- **Supersedes:** —

## Context

Issue #14's acceptance criterion: "Writing 10,000 records in one transaction takes under one second on the
reference host, a DS918+ (measured and noted)". The DS918+ (Intel Celeron J3455, 4 cores, 1.5 GHz, burst
2.3 GHz) is the maintainer's NAS; the squad's sessions and CI run on other hardware. `docs/UNIT_TESTS.md`
forbids a real clock in tests, and 0062 forbids comparing elapsed time with a bound.

Measured on 2026-10-06 on a 2.1 GHz Xeon (cloud VM, 4 vCPU) with prototypes of the schema of 0063 and the
writer settings of 0065, 10,000 records per transaction, several successive batches into one database:
metric records 0.12 to 0.15 s; 9,000 metric records and 1,000 log lines 0.13 to 0.20 s; log lines (the
kernel's OOM-kill line, about 150 bytes, the line the forensics release is about) 0.18 to 0.33 s with the
FTS5 index filled by the write path as chosen in 0063, against 0.50 to 0.79 s with an `AFTER INSERT`
trigger. The J3455 is slower per core (lower clock and IPC; a factor of two to three is a guess, not a
measurement), so the log-line case may still come close to 1 s there. The log import (#15) writes log lines
only, so that case matters most.

## Options considered

1. **A unit test that asserts the duration** — flaky on shared CI runners and under `-race`, and it would
   not measure the DS918+ anyway.
2. **A benchmark that is run by hand and its results noted in the documentation** — no flakiness; the number
   for the reference host is produced on the reference host.
3. **Blocking the change until the DS918+ result exists** — the squad cannot run it.
4. **Closing #14 with this change and moving the criterion to a follow-up** — the criterion would be marked
   done without having been measured; rejected, see the decision on #14 below.

## Decision

Option 2.

- `BenchmarkStore_WriteBatch` in `cmd/vandoxd/internal/store/write_test.go` writes 10,000 records per
  iteration with one `WriteBatch` call (one transaction), in sub-benchmarks `metric`, `log_line` (kernel
  OOM-kill lines with varying numbers) and `mixed` (9,000 metric records and 1,000 log lines), and reports
  `records/s`. `go test ./...` does not run
  benchmarks, so nothing timing-dependent runs in CI.
- A unit test checks the functional part of the criterion: 10,000 records in one call are stored completely
  and atomically.
- `docs/BENCHMARKS.md` describes how to build the benchmark as a static test binary, run it on the DS918+
  with its temporary directory on the data volume, and holds the results table: the development host's
  numbers from this change, and the DS918+ row marked *pending*.
- The DS918+ measurement is split into a follow-up issue, and the change for #14 does **not** close #14: its
  pull request refers to #14 without a closing keyword, and #14 stays open with this one criterion unmet
  until the DS918+ result is noted in `docs/BENCHMARKS.md`. The pull request that notes it closes the
  follow-up issue and #14.
- If a sub-benchmark exceeds one second on the DS918+, the follow-up decides on a remedy in a new record;
  the next candidate is indexing FTS5 after the commit (0063, option c), which changes the user-visible
  behavior "a log line is searchable when its batch is committed" and therefore needs the Product Manager.
  The requirement is not weakened silently.

## Consequences

- The pull request for #14 states that the DS918+ measurement has not been made, gives the development
  host's numbers as what they are (not a result for the reference host), and links the follow-up issue.
- Later performance criteria on the reference host (e.g. #22, #26) add their benchmarks to
  `docs/BENCHMARKS.md` the same way.
