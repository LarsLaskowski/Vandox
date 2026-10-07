# Benchmarks

This document records how the performance criteria of Vandox are measured and what the measurements were.
Benchmarks live next to the tests of the code they measure (Go: a benchmark in the `_test.go` file; .NET: an opt-in test that runs only when `VANDOX_BENCHMARK` is set) or, when the reference host has no SDK, as a console program under `tools/`; they never assert a duration
([`UNIT_TESTS.md`](UNIT_TESTS.md)). Only results from the reference host count against a criterion; results
from the development host show the order of magnitude.

Reference host: a Synology DS918+ (Intel Celeron J3455, DSM, running `vandoxd` in Docker).

## Storage write throughput

Criterion of issue #14: writing 10,000 records in one transaction takes under one second on the reference
host. Two programs measure it the same way: five batches of 10,000 new agent metric points, one
`WriteBatchAsync` call each, into one database that keeps the earlier batches' records; each prints the time of
every batch and the records per second.

- `tools/Vandox.StorageBenchmark`, a console program that needs only the .NET runtime and is what runs on the reference host.
- `WriteThroughputOfTenThousandMetricsPerBatch` (`tests/Vandox.Storage.Tests/WriteThroughputTests.cs`), an opt-in test for
  the development host.

Background: [0063](decisions/0063-storage-schema-records-table-typed-metric-and-log-tables-json-payloads.md),
[0077](decisions/0077-storage-on-microsoft-data-sqlite-same-schema-and-rules.md) (which carries over
[0068](decisions/0068-write-throughput-measured-by-a-benchmark-ds918-measurement-in-a-follow-up.md)) and
[0082](decisions/0082-storage-writer-cached-parameters-and-synchronous-calls.md) (the writer change after the first
reference-host result). The earlier Go benchmark also measured log lines and a mixed batch; those variants are not ported yet.

### Procedure

On a machine with the .NET SDK, build the tool and copy the output folder to the reference host:

```bash
dotnet publish tools/Vandox.StorageBenchmark -c Release -o publish -p:DebugType=none
```

On the reference host (runtime only), with an existing folder on the data volume; the program creates its database in a
subfolder and removes it afterwards, and first prints how long a 4 KiB write takes to reach the disk on that volume:

```bash
dotnet publish/Vandox.StorageBenchmark.dll /volume1/<folder on the data volume> [--batches N] [--size N] [--warmup]
```

`--warmup` writes a small batch first that is not counted, which shows how much of the first batch is start-up cost; without it
the first batch is the one that counts for the criterion (a fresh database). On the development host the same measurement runs with
`dotnet run -c Release --project tools/Vandox.StorageBenchmark -- <folder>` or, as a test,

```bash
VANDOX_BENCHMARK=1 dotnet test tests/Vandox.Storage.Tests --filter WriteThroughput --logger "console;verbosity=detailed"
```

### Results

| Writer | Host | CPU | Date | First batch (ms) | Later batches (ms) |
| ------ | ---- | --- | ---- | ---------------: | -----------------: |
| before [0082](decisions/0082-storage-writer-cached-parameters-and-synchronous-calls.md) | development host (4 vCPU) | Intel Xeon 2.10 GHz | 2026-10-07 | 499–679 | 277–584 (median 309–390) |
| before 0082 | DS918+ (reference host, Linux 4.4, .NET 10.0.10, `/volume2`) | Celeron J3455 | 2026-10-07 | **1480** | 890–1119 (median 1085) |
| after 0082 | development host (4 vCPU) | Intel Xeon 2.10 GHz | 2026-10-07 | 285–329 | 242–368 (median 272–320) |
| after 0082 (cached parameters, synchronous calls) | DS918+ (reference host) | Celeron J3455 | 2026-10-07 | **1043** | 797–1004 (median 925) |
| after 0082 (and writer page cache of 16 MiB) | DS918+ (reference host) | Celeron J3455 | pending, issue #129 | pending | pending |

The development-host rows are not the reference host. With the writer before 0082 the criterion was **not met** on the DS918+
(1480 ms for the first batch), and with the first part of 0082 it was still just over the limit (1043 ms). The last row is pending: issue #129 stays open until it is filled in. If it still takes
longer than one second there, the next candidates are listed in 0082; the FTS5 remedy of record 0063 (option c) does not apply,
because this measurement writes no log lines.
