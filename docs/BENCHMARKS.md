# Benchmarks

This document records how the performance criteria of Vandox are measured and what the measurements were.
Benchmarks live next to the tests of the code they measure (Go: a benchmark in the `_test.go` file; .NET: an opt-in test that runs only when `VANDOX_BENCHMARK` is set) or, when the reference host has no SDK, as a console program under `tools/`; they never assert a duration
([`UNIT_TESTS.md`](UNIT_TESTS.md)). Only results from the reference host count against a criterion; results
from the development host show the order of magnitude.

Reference host: a Synology DS918+ (Intel Celeron J3455, DSM, running `vandoxd` in Docker). It has an SSD volume and a slower HDD volume; the
tables say which one a run used. The data directory of the container can be on either volume; the SSD is recommended.

## Storage write throughput

Criterion (issue #14, restated by [0083](decisions/0083-storage-write-criterion-is-the-median-of-five-batches-on-the-data-volume.md)):
the **median of five batches** of 10,000 records, each written in one transaction, takes under one second on the reference host, on the volume
that holds the database. The first batch (fresh database, start-up included) is reported next to it. Two programs measure it the same way: five
batches of 10,000 new agent metric points, one `WriteBatchAsync` call each, into one database that keeps the earlier batches' records; each prints
the time of every batch and the records per second.

- `tools/Vandox.StorageBenchmark`, a console program that needs only the .NET runtime and is what runs on the reference host. It stays in the
  repository to repeat the measurement after a change of the storage layer, the schema or the host.
- `WriteThroughputOfTenThousandMetricsPerBatch` (`tests/Vandox.Storage.Tests/WriteThroughputTests.cs`), an opt-in test for
  the development host.

Background: [0063](decisions/0063-storage-schema-records-table-typed-metric-and-log-tables-json-payloads.md),
[0077](decisions/0077-storage-on-microsoft-data-sqlite-same-schema-and-rules.md) (which carries over
[0077](decisions/0077-storage-on-microsoft-data-sqlite-same-schema-and-rules.md)),
[0083](decisions/0083-storage-write-criterion-is-the-median-of-five-batches-on-the-data-volume.md) (the writer change after the first
reference-host result) and 0083. The earlier Go benchmark also measured log lines and a mixed batch; those variants are not ported.

### Procedure

On a machine with the .NET SDK, build the tool and copy the output folder to the reference host:

```bash
dotnet publish tools/Vandox.StorageBenchmark -c Release -o publish -p:DebugType=none
```

On the reference host (runtime only), with an existing folder on the volume that holds (or will hold) the database; the program creates its
database in a subfolder and removes it afterwards, and first prints how long a 4 KiB write takes to reach the disk on that volume:

```bash
dotnet publish/Vandox.StorageBenchmark.dll <existing folder on the data volume> [--batches N] [--size N] [--warmup]
```

`--warmup` writes a small batch first that is not counted, which shows how much of the first batch is start-up cost. On the development host the
same measurement runs with `dotnet run -c Release --project tools/Vandox.StorageBenchmark -- <folder>` or, as a test,

```bash
VANDOX_BENCHMARK=1 dotnet test tests/Vandox.Storage.Tests --filter WriteThroughput --logger "console;verbosity=detailed"
```

### Results

| Writer | Host | CPU | Date | First batch (ms) | Later batches (ms) | Median of five (ms) |
| ------ | ---- | --- | ---- | ---------------: | -----------------: | ------------------: |
| before [0083](decisions/0083-storage-write-criterion-is-the-median-of-five-batches-on-the-data-volume.md) | development host (4 vCPU) | Intel Xeon 2.10 GHz | 2026-10-07 | 499–679 | 277–584 | 309–390 |
| before 0083 | DS918+ (reference host, Linux 4.4, .NET 10.0.10, SSD volume) | Celeron J3455 | 2026-10-07 | 1480 | 890–1119 | 1085 |
| after 0083 | development host (4 vCPU) | Intel Xeon 2.10 GHz | 2026-10-07 | 285–329 | 242–368 | 272–320 |
| after 0083 (cached parameters, synchronous calls) | DS918+ (SSD volume) | Celeron J3455 | 2026-10-07 | 1043 | 797–1004 | 925 |
| after 0083 (and writer page cache of 16 MiB) | DS918+ (SSD volume) | Celeron J3455 | 2026-10-07 | 1001 | 711–783 | **783** |
| after 0083 (and writer page cache of 16 MiB) | DS918+ (HDD volume) | Celeron J3455 | 2026-10-07 | 1171 | 1079–1471 | **1171** |

Outcome against the restated criterion on the DS918+: **met on the SSD volume** (median 783 ms, 12,800 records/s in a steady state) and **not met on the
HDD volume** (median 1171 ms, about 8,500 records/s). A 4 KiB write needs about 10 ms to reach the disk on the SSD and about 122 ms on the HDD: on the
SSD the writer is CPU-bound, on the HDD the median is about 390 ms higher (1171 against 783 ms) because each commit waits for the disk. The database can be put on
either volume; the HDD works too, with the slower writes shown here, which is far above what the agent sends (hundreds of records per minute). The
first batch is about 250 ms slower than the later ones on the SSD (fresh file, start-up); with the unchanged writer it was 1480 ms. `ReadyToRun`
made no difference on the development host. The FTS5 remedy of record 0063 (option c) does not apply, because this measurement writes no log lines.
The development-host rows are not the reference host.
