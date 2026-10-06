# Benchmarks

This document records how the performance criteria of Vandox are measured and what the measurements were.
Benchmarks are Go benchmarks in the `_test.go` file of the code they measure; they never assert a duration
([`UNIT_TESTS.md`](UNIT_TESTS.md)). Only results from the reference host count against a criterion; results
from the development host show the order of magnitude.

Reference host: a Synology DS918+ (Intel Celeron J3455, DSM, running `vandoxd` in Docker).

## Storage write throughput

Criterion of issue #14: writing 10,000 records in one transaction takes under one second on the reference
host. `BenchmarkStore_WriteBatch` (`cmd/vandoxd/internal/store/write_test.go`) writes 10,000 new records per
iteration in one `WriteBatch` call into a database that keeps the earlier iterations' records, with three
sub-benchmarks: `metric` (10,000 metric points), `log_line` (10,000 kernel OOM-kill log lines, which also
fill the FTS5 index) and `mixed` (9,000 metric points and 1,000 log lines). It reports `ns/op` (one 10,000-record
batch) and `records/s`. Background: [0063](decisions/0063-storage-schema-records-table-typed-metric-and-log-tables-json-payloads.md),
[0068](decisions/0068-write-throughput-measured-by-a-benchmark-ds918-measurement-in-a-follow-up.md).

### Procedure

On the development host:

```bash
go test -run '^$' -bench BenchmarkStore_WriteBatch -benchtime 5x ./cmd/vandoxd/internal/store
```

On the reference host, build the test binary, copy it to the NAS and run it with the temporary directory on
the data volume (the benchmark creates its databases below `TMPDIR`):

```bash
CGO_ENABLED=0 GOOS=linux GOARCH=amd64 go test -c -o store.test ./cmd/vandoxd/internal/store
TMPDIR=<directory on the data volume> ./store.test -test.run '^$' -test.bench BenchmarkStore_WriteBatch -test.benchtime 5x
```

### Results

| Benchmark | Host | CPU | Date | ns/op | records/s |
| --------- | ---- | --- | ---- | ----: | --------: |
| `WriteBatch/metric` | development host (4 vCPU) | Intel Xeon 2.10 GHz | 2026-10-06 | 136,024,643 | 73,516 |
| `WriteBatch/log_line` | development host (4 vCPU) | Intel Xeon 2.10 GHz | 2026-10-06 | 221,778,539 | 45,090 |
| `WriteBatch/mixed` | development host (4 vCPU) | Intel Xeon 2.10 GHz | 2026-10-06 | 142,797,717 | 70,029 |
| `WriteBatch/*` | DS918+ (reference host) | Celeron J3455 | pending, issue #129 | pending | pending |

The development-host rows are not the reference host. The DS918+ row is pending: the 1 s criterion of #14
stays open until it is filled in (follow-up issue #129). If a sub-benchmark exceeds one second there, a
decision record chooses the remedy first; the next candidate is indexing FTS5 after the commit (record 0063,
option c).
