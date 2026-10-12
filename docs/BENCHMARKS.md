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
[0077](decisions/0077-storage-on-microsoft-data-sqlite-same-schema-and-rules.md) (the move to Microsoft.Data.Sqlite) and
[0083](decisions/0083-storage-write-criterion-is-the-median-of-five-batches-on-the-data-volume.md) (the writer change after the first
reference-host result). The earlier Go benchmark also measured log lines and a mixed batch; those variants are not ported.

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
| after the log templates of [0095](decisions/0095-log-templates-token-rules-statistics-in-the-write-path-usual-level-from-covered-hours.md) (two runs; metric batches run no template statement) | development host (4 vCPU) | Intel Xeon 2.10 GHz | 2026-10-12 | 273–386 | 185–402 | 273–282 |

Outcome against the restated criterion on the DS918+: **met on the SSD volume** (median 783 ms, 12,800 records/s in a steady state) and **not met on the
HDD volume** (median 1171 ms, about 8,500 records/s). A 4 KiB write needs about 10 ms to reach the disk on the SSD and about 122 ms on the HDD: on the
SSD the writer is CPU-bound, on the HDD the median is about 390 ms higher (1171 against 783 ms) because each commit waits for the disk. The database can be put on
either volume; the HDD works too, with the slower writes shown here, which is far above what the agent sends (hundreds of records per minute). The
first batch is about 250 ms slower than the later ones on the SSD (fresh file, start-up); with the unchanged writer it was 1480 ms. `ReadyToRun`
made no difference on the development host. The FTS5 remedy of record 0063 (option c) does not apply, because this measurement writes no log lines.
The development-host rows are not the reference host.

## Log templates

Criterion ([0095](decisions/0095-log-templates-token-rules-statistics-in-the-write-path-usual-level-from-covered-hours.md), from issue #22):
**1,000,000 log lines are written with their templates in under 10 minutes on the reference host**, in batches of 10,000 lines, one
transaction each. Writing a log line normalizes its message into a template and counts it under the template, the template's hour and the
source's hour in the same transaction (storage area, *Log templates*), so this is the write cost on top of the log line and its full-text entry.
The same program measures the three template queries on the written database. Only a run on the reference host counts against the criterion.

### Procedure

Publish the tool as above and run it on the reference host, on the volume that holds the database (the SSD volume):

```bash
dotnet publish/Vandox.StorageBenchmark.dll <existing folder on the data volume> --log-lines 1000000 [--size N]
```

`--log-lines N` (1 to 10,000,000) cannot be combined with `--batches` or `--warmup`; `--size N` is the number of lines per batch (default 10,000,
the criterion's batch size). The lines are generated from the line index, without a random number generator, so two runs write the same lines:
24 message shapes modeled on real formats (Postfix connection, queue and delivery lines, Dovecot logins and logouts, the kernel OOM kill lines,
MariaDB aborted connections, nginx errors with client address and path, systemd and sshd lines, firewall lines) of the sources `syslog`,
`journal`, `mariadb` and `nginx`, with numbers, addresses, IDs, e-mail addresses and file names that vary with the index, and capture times in
order over 30 days (720 hours). On the development host the same measurement runs with
`dotnet run -c Release --project tools/Vandox.StorageBenchmark -- <folder> --log-lines 1000000`.

The program prints, in this order: the time to normalize the messages in memory; the write time (total, lines per second, the median, fastest and
slowest batch); the numbers of templates, template hours and source hours; the time of the new-templates query (the last 15 days, limit 10,000); the
usual-level query over the last 744 hours at the **default parameters** (baseline 168 hours, minimum baseline 24 hours, factor 3, minimum count
10) and at the **widest parameters** (baseline 2,160 hours, minimum baseline 1 hour, factor 1, minimum count 1, limit 10,000), each with its number
of **candidates** (the template hours of the range that reach the minimum count, all of which the query evaluates); the verdict for 1,000,000
lines with the default batch size; and then the **hostile case** in a second, fresh database: 100,000 lines (or all lines if fewer) whose messages
hold no digit and differ per line, so that every line is a template of its own and costs three upserts, with the write time and the widest
usual-level query on it; and finally the **concentrated case** in a third, fresh database: 2,160 hourly heartbeat lines of one source followed by as many such lines as the hostile case, all of that source in the next hour, with the write time and the widest usual-level query on it.

### Results

| Host | CPU | Date | Lines | Write, total | Lines per second | Median batch (ms) | Templates | Template hours | Verdict |
| ---- | --- | ---- | ----: | -----------: | ---------------: | ----------------: | --------: | -------------: | ------- |
| development host (4 vCPU, .NET 10.0.12) | Intel Xeon 2.10 GHz | 2026-10-12 | 1,000,000 | 52.6 s | 18,995 | 504 | 2,741 | 153,923 | not the reference host |
| DS918+ (reference host, SSD volume) | Celeron J3455 | not measured yet | 1,000,000 | not measured yet | not measured yet | not measured yet | not measured yet | not measured yet | not measured yet |

Normalizing the 1,000,000 messages alone, without the database, took 1.4 s on the development host. The usual-level query on the same database
(range of 744 hours ending after the last line):

| Query | Candidates | Time (development host) |
| ----- | ---------: | ----------------------: |
| new templates (last 15 days, limit 10,000), 1,197 results | — | 8 ms |
| usual level, default parameters, 0 results | 12,960 | 572 ms |
| usual level, widest parameters, 10,000 results | 153,923 | 3,913 ms |
| concentrated case, widest parameters, 10,000 results (below) | 100,743 | 275 ms |

The hostile case (100,000 lines, 100,000 templates, 100,000 template hours): write time 5.1 s (19,520 lines per second, median batch 509 ms), and
the widest usual-level query 203 ms with 100,000 candidates. The **concentrated case** puts the worst shape for the covered hours into one database:
2,160 hourly heartbeat lines of one source, then 100,000 digitless lines that differ per line, all of that source in the next hour, so one source-hour
holds 100,000 candidates with a baseline of 2,160 covered hours; the statement materializes the covered hours (`AS MATERIALIZED`), so they are
counted once for that source-hour and not once per candidate (without it SQLite flattens the statement and counts per candidate, which took
22.4 s on this shape). Writing took 5.1 s (20,071 lines per second), the widest query 275 ms with 100,743 candidates. The generated lines have no spikes (the counts per hour are even), so the default
query reports nothing; its time is the cost of the candidates. The first run of the reference host replaces the "not measured yet" row; the
reference host is about three to four times slower on the metric write (783 ms against 272 ms for a batch of 10,000), and the usual-level query time
is not bounded by the range or the limit (detection area, *Limits of templates*), so the figures of the reference host also decide whether a caller
needs a narrower range.
