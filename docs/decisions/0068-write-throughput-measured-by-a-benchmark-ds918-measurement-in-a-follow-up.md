# 0068: Write throughput is measured by a Go benchmark and never asserted in tests; the DS918+ measurement is a follow-up issue

- **Status:** Proposed
- **Date:** 2026-10-06
- **Source:** Issue #14
- **Supersedes:** —

## Context

Issue #14's acceptance criterion: "Writing 10,000 records in one transaction takes under one second on the
reference host, a DS918+ (measured and noted)". The DS918+ (Intel Celeron J3455, 4 cores, 1.5 GHz, burst
2.3 GHz) is the maintainer's NAS; the squad's sessions and CI run on other hardware. `docs/UNIT_TESTS.md`
forbids a real clock in tests, and 0062 forbids comparing elapsed time with a bound. Measured on 2026-10-06
on a 2.1 GHz Xeon (cloud VM): about 0.14 s for 10,000 metric records and about 0.45 s for 10,000 log lines
(FTS5 indexing about 0.3 s of it); the J3455 is expected to be several times slower, so log lines may exceed
1 s there.

## Options considered

1. **A unit test that asserts the duration** — flaky on shared CI runners and under `-race`, and it would
   not measure the DS918+ anyway.
2. **A benchmark that is run by hand and its results noted in the documentation** — no flakiness; the number
   for the reference host is produced on the reference host.
3. **Blocking the change until the DS918+ result exists** — the squad cannot run it.

## Decision

Option 2.

- `BenchmarkStore_WriteBatch` in `cmd/vandoxd/internal/store/write_test.go` writes 10,000 records per
  iteration with one `WriteBatch` call (one transaction), in sub-benchmarks `metric`, `log_line` and
  `mixed` (9,000 metric records and 1,000 log lines), and reports `records/s`. `go test ./...` does not run
  benchmarks, so nothing timing-dependent runs in CI.
- A unit test checks the functional part of the criterion: 10,000 records in one call are stored completely
  and atomically.
- `docs/BENCHMARKS.md` describes how to build the benchmark as a static test binary, run it on the DS918+
  with its temporary directory on the data volume, and holds the results table: the development host's
  numbers from this change, and the DS918+ row marked *pending*.
- The DS918+ measurement is split into a follow-up issue. If log lines miss 1 s there, that issue decides on
  a remedy (for example indexing FTS5 in a separate transaction after the commit); the requirement is not
  weakened silently.

## Consequences

- The pull request for #14 states that the DS918+ measurement is pending and links the follow-up issue.
- Later performance criteria on the reference host (e.g. #22, #26) add their benchmarks to
  `docs/BENCHMARKS.md` the same way.
