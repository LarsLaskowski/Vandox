# Benchmarks

This document records how the performance criteria of Vandox are measured and what the measurements were.
Benchmarks live next to the tests of the code they measure (Go: a benchmark in the `_test.go` file; .NET: an opt-in test that runs only when `VANDOX_BENCHMARK` is set); they never assert a duration
([`UNIT_TESTS.md`](UNIT_TESTS.md)). Only results from the reference host count against a criterion; results
from the development host show the order of magnitude.

Reference host: a Synology DS918+ (Intel Celeron J3455, DSM, running `vandoxd` in Docker).

## Storage write throughput

Criterion of issue #14: writing 10,000 records in one transaction takes under one second on the reference
host. The measurement is `WriteThroughputOfTenThousandMetricsPerBatch`
(`tests/Vandox.Storage.Tests/WriteThroughputTests.cs`): it writes five batches of 10,000 new agent metric
points in one `WriteBatchAsync` call each into one database that keeps the earlier batches' records, and prints
the time of each batch with the resulting records per second. Background:
[0063](decisions/0063-storage-schema-records-table-typed-metric-and-log-tables-json-payloads.md),
[0077](decisions/0077-storage-on-microsoft-data-sqlite-same-schema-and-rules.md) (which carries over
[0068](decisions/0068-write-throughput-measured-by-a-benchmark-ds918-measurement-in-a-follow-up.md)). The earlier
Go benchmark also measured log lines and a mixed batch; those variants are not ported yet.

### Procedure

On the development host:

```bash
VANDOX_BENCHMARK=1 dotnet test tests/Vandox.Storage.Tests --filter WriteThroughput --logger "console;verbosity=detailed"
```

On the reference host the same command runs with the temporary directory (`TMPDIR`) on the data volume. The
DS918+ has no .NET SDK by default; how to run the measurement there is part of issue #129.

### Results

The first of the five batches is the one that counts for the criterion (a fresh database); later ones show the
effect of a growing database and warm-up.

| Measurement | Host | CPU | Date | First batch (ms) | Steady batch (ms) |
| ----------- | ---- | --- | ---- | ---------------: | ----------------: |
| 10,000 metric points | development host (4 vCPU) | Intel Xeon 2.10 GHz | 2026-10-06 | 621 | 322 |
| 10,000 metric points | DS918+ (reference host) | Celeron J3455 | pending, issue #129 | pending | pending |

The development-host row is not the reference host. The DS918+ row is pending: the 1 s criterion of #14
stays open until it is filled in (follow-up issue #129). If it takes longer than one second there, a decision
record chooses the remedy first; the next candidate is indexing FTS5 after the commit (record 0063, option c).
