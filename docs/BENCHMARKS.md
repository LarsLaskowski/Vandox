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
| DS918+ (reference host, SSD volume; Linux 4.4.302+, .NET 10.0.10, 4 CPUs) | Celeron J3455 | 2026-10-12 | 1,000,000 | 195.8 s (3.3 min) | 5,107 | 1,864 | 2,741 | 153,923 | **met** (limit 10 min) |

Normalizing the 1,000,000 messages alone, without the database, took 1.4 s on the development host and 4.1 s on the reference host. The
usual-level query ran on the same database (range of 744 hours ending after the last line). The statement used to sum the baseline per candidate;
it now sums it with a window function per template (record 0095, option 13) and returns the same rows. The development host ran both, the
reference host ran the first measurement with the old statement and a second one with the new statement (write time 195.5 s, 5,116 lines per
second, so the same as in the first run):

| Query | Candidates | Development host, per candidate | Development host, window | Reference host, per candidate | Reference host, window |
| ----- | ---------: | ------------------------------: | -----------------------: | ----------------------------: | ---------------------: |
| new templates (last 15 days, limit 10,000), 1,197 results | — | 8 ms | 9 ms | 27 ms | 35 ms |
| usual level, default parameters, 0 results | 12,960 | 572 ms | 497 ms | 1,464 ms | 1,424 ms |
| usual level, widest parameters, 10,000 results | 153,923 | 3,913 ms | 710 ms | **11,979 ms** | **1,963 ms** |
| hostile case, widest parameters, 10,000 results (below) | 100,000 | 203 ms | 306 ms | 571 ms | 898 ms |
| concentrated case, widest parameters, 10,000 results (below) | 100,743 | 275 ms | 338 ms | 970 ms | 1,009 ms |

The widest query on the reference host took 12.0 s with the baseline sum per candidate, above the 10 s line issue #208 set. The window function reads
each template's hours once instead of up to 2,160 counts per candidate and brings it to 2.0 s (6.1 times faster); the default parameters and the
concentrated case do not change. The hostile case, where every template has a single hour, is slower with the window (0.9 s against 0.6 s)
and stays below the 10 s line, so no cap on the candidates and no other query shape is needed.

The hostile case (100,000 lines, 100,000 templates, 100,000 template hours): write time 5.1 s (19,520 lines per second, median batch 509 ms) on
the development host and 20.4 s (4,903 lines per second, median batch 2,048 ms) on the reference host. The **concentrated case** puts the worst shape for the covered hours into one database:
2,160 hourly heartbeat lines of one source, then 100,000 digitless lines that differ per line, all of that source in the next hour, so one source-hour
holds 100,000 candidates with a baseline of 2,160 covered hours; the statement materializes the covered hours (`AS MATERIALIZED`), so they are
counted once for that source-hour and not once per candidate (without it SQLite flattens the statement and counts per candidate, which took
22.4 s on this shape). Writing took 5.1 s (20,071 lines per second) on the development host and 21.0 s (4,870 lines per second) on the reference host. The generated lines have no spikes (the counts per hour are even), so the default
query reports nothing; its time is the cost of the candidates. The reference host is about three to four times slower on the metric write (783 ms against 272 ms for a batch of 10,000) and 3.7 times on the
log write; the usual-level query time is not bounded by the range or the limit (detection area, *Limits of templates*), so a caller that serves a
user still narrows the range and runs it off the request path.

## Log search

Criterion ([0103](decisions/0103-logs-view-address-as-the-view-bounded-search-pages-template-lines-by-normalization.md), from issue #26):
**the first page of the logs view over 5,000,000 stored log lines answers in under 2,000 ms on the reference host**, for every case listed below,
with the database on the SSD volume. The first page is 100 lines with the page's scan bound (`LogsView.ScanRows`, 12,000 examined lines); the
store chooses between the full-text index and a time-ordered scan by a selectivity cap (`LogFinder.SelectiveMatches`, 12,000 matches). Both values
were set from the development-host run below, so that every text search stays under 500 ms there (the reference host was 3 to 4 times slower than the
development host in earlier measurements); the cases that the two values do not change are named in the notes. Only a run on the reference host counts
against the criterion.

### Procedure

Publish the tool as above and run it on the reference host, on the volume that holds the database (the SSD volume); it needs about 2 GiB of free space for
5,000,000 lines and about 5 minutes to write them on the development host (longer on the reference host):

```bash
dotnet publish/Vandox.StorageBenchmark.dll <existing folder on the data volume> --search-lines 5000000
```

`--search-lines N` (1 to 10,000,000) cannot be combined with `--batches`, `--warmup` or `--log-lines`; `--size N` is the number of lines per batch
(default 10,000). The lines are those of the log template measurement (24 message shapes, four sources, capture times in order over 30 days) with
generated words added, so that the words have known shares; the program prints the real count of lines per word and program. It then times each case
three times (the first run is the one that matters for a page opened after a pause; the later ones show the warm cache) and prints, per case, the
mode the search ran in (`index` or `scan`, found by repeating the selectivity probe), the result, the three times and whether the slowest is under
2,000 ms. The verdict line compares the slowest time of all cases with 2,000 ms, for 5,000,000 lines only.

| Case | What it asks |
| ---- | ------------ |
| 1 | a rare word (1,000 lines) |
| 2, 2b | a word in about 1 % (50,000 lines), and in about 0.33 % of the lines (16,667 lines) |
| 3 | a word in 33 % of the lines (absent in the last two days) |
| 4, 4b | case 3 with a program and a severity filter; case 3 with a source |
| 5 | two words in half of the lines each that meet in 5,000 lines |
| 6, 6b, 6c | no text, a program filter only: a program of 4 % of the lines; a program of 250 lines; the same program within one source |
| 7 | case 3 in a one-day range in the middle |
| 8, 8b | the lines of the most frequent template (105,000 lines) and of a rare one (7 lines) |
| 9 | case 3 in the last day, where the word does not occur: the search ends at the scan bound |
| 10 | the context (5 and 5) of a hit |
| 11, 11b | the template list (limit 200) of the whole data and of the 30 days |
| 12 | 16 words in a quarter of the lines each, of which no line holds all: the selectivity probe walks their whole index entries |

### Results

Development host (4 vCPU, Intel Xeon 2.10 GHz, Ubuntu 24.04, .NET 10.0.12), 5,000,000 lines written in 259 s (19,289 lines per second), database file
1,852 MiB, 12,914 templates. Times in milliseconds: the first run, then the slowest of the three:

| Case | Mode | Result | First run | Slowest |
| ---- | ---- | ------ | --------: | ------: |
| 1 rare word | index | 100 lines | 30 | 30 |
| 2 word in 1 % | scan | 100 lines | 169 | 177 |
| 2b word in 0.33 % | scan | 40 lines, scan bound reached | 168 | 168 |
| 3 word in 33 % | scan | 100 lines | 6 | 6 |
| 4 with program and severity | scan | 100 lines | 8 | 8 |
| 4b with a source | scan | 100 lines | 6 | 6 |
| 5 two words, rare combination | index | 100 lines | 173 | 173 |
| 6 program only (4 %) | scan | 100 lines | 3 | 3 |
| 6b rare program | scan | 1 line, scan bound reached | 6 | 7 |
| 6c rare program in one source | scan | 2 lines, scan bound reached | 11 | 11 |
| 7 one-day range | scan | 100 lines | 8 | 8 |
| 8 most frequent template | scan | 100 lines | 61 | 61 |
| 8b rare template | scan | 0 lines, scan bound reached | 150 | 150 |
| 9 scan bound, no match | scan | 0 lines, scan bound reached | 411 | 411 |
| 10 context | — | 5 + 5 lines | 11 | 11 |
| 11 template list, whole data | — | 200 templates | 760 | 760 |
| 11b template list, 30 days | — | 200 templates | 716 | 732 |
| 12 16 words, empty intersection | index | 0 lines | 1,232 | 1,232 |

**Verdict on the development host: every case is under 2,000 ms (slowest 1,232 ms).** Cases 11, 11b and 12 are over the 500 ms of the tuning target
and are not changed by the two constants (notes below). Reference host (DS918+, SSD volume, Celeron J3455): **not yet measured**; the Product Manager runs
the procedure above and the row is entered here.

| Host | Date | Slowest case | Verdict |
| ---- | ---- | ------------ | ------- |
| DS918+ (reference host, SSD volume) | _to be entered after the Product Manager's run_ | _?_ | _?_ |

### Tuning

The first run used a scan bound of 50,000 and a cap of 20,000. With them the scan-bound case 9 took 1,758 to 1,970 ms (about 35 microseconds per examined
line, since every line of a common word's range is looked up in that word's index entry), the rare template 8b 611 to 688 ms and the case just below the cap
(2b, 16,667 lines, then in index mode) 291 to 327 ms for its first page, about 20 microseconds per candidate. A bound and a cap of 12,000 bring the
scan-bound case to 411 ms and every text search under 500 ms. A smaller bound makes a search over a large range with few matches continue more often
(case 2b finds 40 lines per 12,000 examined); the plan's lower limit for both values is 5,000 and 10,000.

### Notes

- **Case 12** costs about 1.2 s on the first run. The selectivity probe has to walk the index entries of all 16 words (each in 1,250,000 lines) to find that no line
  holds all of them, about 0.5 s inside the full-text module, where the deadline cannot interrupt it; because the probe found 0, the index query then walks them
  again. This is the shape of risk named in record 0103; a cap on the cost needs a cheaper probe, not a larger cap. On the reference host this case is the most
  likely to exceed 2,000 ms.
- **Cases 11 and 11b** sum the template hours of the range (374,020 template hours here) through the index on the hour and look each one up in the table:
  about 0.7 s warm, 0.9 to 1.2 s cold. A rewritten statement took 0.03 s (200 rows) when the aggregate ran over the table in key order (`GROUP BY template_id` over
  `log_template_hours` with `+hour` in the conditions, then the join to `log_templates` for the 200 rows), measured with the SQLite command-line library on the same
  file; that reads the whole table whatever the range is, so it is a trade-off for a narrow range of a large table.
- The scan modes rely on the planner reading `records(kind, source, captured_at)` or `records(kind, captured_at)` in order without a hint; the times of cases 3 to 9
  (a few milliseconds for a page that needs a few hundred lines) show that it does, also with a source (cases 4b and 6c).
