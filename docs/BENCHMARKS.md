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

Criterion (issue #26, decided in [0106](decisions/0106-logs-view-address-as-the-view-bounded-search-pages-template-lines-by-normalization.md)):
**every search case of the logs view answers in under 2,000 ms on the reference host** over 5,000,000 stored log lines. The cases (rare and
common words, filters, templates, context, the template list and crafted terms) are generated and timed by the benchmark tool, three runs each:

```bash
dotnet publish/Vandox.StorageBenchmark.dll <existing folder on the data volume> --search-lines 5000000
```

### Results

Reference host, **run 1** (DS918+, SSD volume `/volume20/HostData`, Linux 4.4.302+, .NET 10.0.10, 4 CPUs; scan bound and cap 12,000, before the changes of
this section; cases 1 to 12; 5,000,000 lines written in 1,087 s, 4,600 lines per second): **criterion not met.** Slowest of three runs, in milliseconds:

| Case | Mode | Result | First run | Slowest |
| ---- | ---- | ------ | --------: | ------: |
| 1 rare word | index | 100 lines | 30 | 30 |
| 2 word in 1 % | scan | 100 lines | 1,007 | 1,021 |
| 2b word in 0.33 % | scan | 40 lines, scan bound reached | 1,075 | 1,081 |
| 3 word in 33 % | scan | 100 lines | 41 | 42 |
| 4 / 4b with program and severity / with a source | scan | 100 lines | 44 / 41 | 44 / 42 |
| 5 two words, rare combination | index | 100 lines | 170 | 172 |
| 6 / 6b / 6c program only | scan | 100 / 1 / 2 lines | 9 / 26 / 35 | 9 / 26 / 35 |
| 7 one-day range | scan | 100 lines | 53 | 54 |
| 8 most frequent template | scan | 100 lines | 365 | 365 |
| 8b rare template | scan | 0 lines, scan bound reached | 877 | 889 |
| 9 scan bound, no match | scan | 0 lines, scan bound reached | **2,141** | **2,141** |
| 10 context | n/a | 5 + 5 lines | 21 | 21 |
| 11 / 11b template list (before the key-order statement) | n/a | 200 templates | 125 / 125 | 125 / 125 |
| 12 16 words, empty intersection | index | 0 lines | **2,203** | **2,229** |

Cases 9 and 12 were over 2,000 ms. Case 9 (12,000 examined lines) led to the scan bound of 8,000 (2,141 ms times 8/12 is about 1,430 ms); case 12 to the
time budget of the index check.

Development host (4 vCPU, Intel Xeon 2.10 GHz, Ubuntu 24.04, .NET 10.0.12), 5,000,000 lines written in 270 s (18,520 lines per second), database file
1,983 MiB, after the changes of this section (scan bound 8,000 divided by the word parts, one test per one-part word, the line index for terms of several parts, the page clock
of 1.5 s, the index check under a budget of 0.7 s, the index mode reads the lines by the IDs of the probe, the template list sums in key order). Times in milliseconds: the
first run, then the slowest of the three. `bound or clock` means that the page ended at the scan bound or at the page budget:

| Case | Mode | Result | First run | Slowest |
| ---- | ---- | ------ | --------: | ------: |
| 1 rare word | index | 100 lines | 41 | 41 |
| 2 word in 1 % | scan | 80 lines, bound or clock | 193 | 201 |
| 2b word in 0.33 % | scan | 27 lines, bound or clock | 145 | 177 |
| 3 word in 33 % | scan | 100 lines | 9 | 9 |
| 4 with program and severity | scan | 100 lines | 12 | 12 |
| 4b with a source | scan | 100 lines | 19 | 19 |
| 5 two words, rare combination | index | 100 lines | 139 | 139 |
| 6 program only (4 %) | scan | 100 lines | 3 | 3 |
| 6b rare program | scan | 1 line, bound or clock | 7 | 7 |
| 6c rare program in one source | scan | 2 lines, bound or clock | 11 | 11 |
| 7 one-day range | scan | 100 lines | 13 | 13 |
| 8 most frequent template | scan | 100 lines | 34 | 34 |
| 8b rare template | scan | 0 lines, bound or clock | 27 | 419 |
| 9 scan bound, no match | scan | 0 lines, bound or clock | 287 | 319 |
| 10 context | n/a | 5 + 5 lines | 18 | 18 |
| 11 template list, whole data | n/a | 200 templates | 93 | 93 |
| 11b template list, 30 days | n/a | 200 templates | 30 | 33 |
| 12 16 words, empty intersection | index | 0 lines | 752 | 752 |
| 13 eight words together, range without them | scan | 0 lines, bound or clock | 59 | 59 |
| 14 four-part term of common parts | index | 0 lines | 155 | 155 |
| 15 crafted 511-part term | scan, index check stopped | 0 lines, bound or clock | 752 | 752 |

**Verdict on the development host: every case is under 2,000 ms (slowest 752 ms), and every case but 12 and 15 is under the 350 ms target** (the 419 ms of case 8b is one run of three).
Cases 12 and 15 are over the target because the index check of case 12 takes up to its 0.7 s budget and case 15 is stopped by it. Case 12 with a **cold cache** (the file cache dropped with
`echo 3 > /proc/sys/vm/drop_caches` before each of two runs): 796 and 759 ms, in both the index check stopped by the budget and the scan page of sixteen one-word tests ended at its bound; the
third, warm run took 605 ms with the index check finishing in time. Case 15 and a 60-part term, cold: 771 and 715 ms.

| Host | Date | Slowest case | Verdict |
| ---- | ---- | ------------ | ------- |
| DS918+ (reference host, SSD volume), run 1 | 2026-10-14 | case 12, 2,229 ms | **not met** (cases 9 and 12) |
| DS918+ (reference host, SSD volume), run 2 | 2026-10-14 | case 9, 1,502 ms | **met** (every case under 2,000 ms) |

Reference host, **run 2** (DS918+, SSD volume `/volume20/HostData`, Linux 4.4.302+, .NET 10.0.10, 4 CPUs; build 38cc9a8 with scan bound 8,000, cap 12,000, probe budget 0.7 s, page
budget 1.5 s; cases 1 to 15; 5,000,000 lines written in 1,147 s, 4,358 lines per second; database 1,983 MiB). Run 1 above missed because of cases 9 (scan bound of 12,000 lines, 2,141 ms) and 12
(index check of 16 common words, 2,229 ms); this run is the one that counts against the criterion. Times in milliseconds, first run then slowest of three:

| Case | Mode | Result | First run | Slowest |
| ---- | ---- | ------ | --------: | ------: |
| 1 rare word | index | 100 lines | 28 | 28 |
| 2 word in 1 % | scan | 80 lines, bound or clock | 1,037 | 1,056 |
| 2b word in 0.33 % | scan | 27 lines, bound or clock | 938 | 938 |
| 3 word in 33 % | scan | 100 lines | 47 | 48 |
| 4 with program and severity | scan | 100 lines | 49 | 50 |
| 4b with a source | scan | 100 lines | 48 | 49 |
| 5 two words, rare combination | index | 100 lines | 169 | 175 |
| 6 program only (4 %) | scan | 100 lines | 10 | 10 |
| 6b rare program | scan | 1 line, bound or clock | 20 | 20 |
| 6c rare program in one source | scan | 2 lines, bound or clock | 27 | 28 |
| 7 one-day range | scan | 100 lines | 65 | 65 |
| 8 most frequent template | scan | 100 lines | 79 | 82 |
| 8b rare template | scan | 0 lines, bound or clock | 87 | 94 |
| 9 scan bound, no match | scan | 0 lines, bound or clock | **1,502** | **1,502** |
| 10 context | n/a | 5 + 5 lines | 21 | 21 |
| 11 template list, whole data | n/a | 200 templates | 123 | 126 |
| 11b template list, 30 days | n/a | 200 templates | 125 | 126 |
| 12 16 words, empty intersection | scan, index check stopped | 0 lines, bound or clock | 938 | 940 |
| 13 eight words together, range without them | scan | 0 lines, bound or clock | 241 | 241 |
| 14 four-part term of common parts | index | 0 lines | 623 | 644 |
| 15 crafted 511-part term | scan, index check stopped | 0 lines, bound or clock | 813 | 837 |

**Verdict on the reference host: criterion met.** Every case is under 2,000 ms; the slowest is case 9 (1,502 ms, the page clock of 1.5 s ending the scan of a common word's range where it does
not occur). Cases 12 and 15 ran as `scan, index check stopped`: the index check was given up at its 0.7 s budget and the scan page ended in 0.8 to 0.94 s in all. Case 14 stayed in index mode and took 623 ms.

### Tuning

The first development-host run used a scan bound of 50,000 and a cap of 20,000. With them the scan-bound case 9 took 1,758 to 1,970 ms (about 35 microseconds per examined
line, since every line of a common word's range is looked up in that word's index entry), the rare template 8b 611 to 688 ms and the case just below the cap
(2b, 16,667 lines, then in index mode) 291 to 327 ms for its first page, about 20 microseconds per candidate. A bound and a cap of 12,000 brought the
scan-bound case to 411 ms and every text search except case 12 under 500 ms, on a target that assumed the reference host to be 3 to 4 times slower. **Run 1 on the
reference host showed the factor to be 5** (case 9: 411 ms here, 2,141 ms there; case 12 warm: 440 against 2,220 ms), and the scan cost is linear in the bound, so the
bound is now **8,000** (the plan's range is 5,000 to 100,000) and the development-host target **350 ms**; the cap stays at 12,000. The scan bound is divided by the word
parts of the user's text, because the per-line test grows with them (in a scratch measurement 20 microseconds for one part, 99 for eight, 196 for sixteen), and the
scan tests only the user's text, not the words of a template. A smaller bound makes a search over a large range with few matches continue more often (case 2b finds
27 lines per 8,000 examined). `SearchBenchmark.ScanRows` in the benchmark tool is a copy of `LogsView.ScanRows` (the tool does not reference the backend): change both together.

**The index check budget** is 0.7 s (`LogFinder.ProbeBudget`): the Product Manager chose that every page answers in about two seconds, so a check that has not ended by then
is given up and the request goes through the range as a scan (case 12 on the reference host took 2.2 s). **The page budget** is 1.5 s (`LogFinder.PageBudget`), counted from the start of the
call and including the index check: a scan page ends after the line or chunk at which it runs out, with at least one line examined. **The chunk of the line index** is 64 lines or 262,144 characters
of messages: a chunk is inserted into an in-memory index and matched in a few milliseconds for ordinary lines (about 2.3 ms for a four-part phrase in 8,000 lines of 60 bytes, in a scratch measurement), and the limit keeps the
work and the memory of one match small. The budgets, the bound, the chunk and the cap are values of the code, not of the configuration.

### Notes

- **Case 12** probes the index for the record IDs of 16 words that each occur in 1,250,000 lines and have no line in common. FTS5 has to walk their whole
  entries to find that (about 0.45 s inside the full-text module on the development host). The progress handler runs there (gaps of 20 to 31 ms in a scratch
  measurement with terms of 4 to 511 parts, an interrupted probe ended at 0.71 s), so the check is stopped by the deadline or by the budget. Before the change
  of the index mode the query then walked the entries a second time (1,232 ms). On the development host the check ends within the budget when the cache is warm (434 to 621 ms); on the reference host it did not (2.2 s in run 1), and in run 2 it was stopped at the budget
  and the scan page answered in 938 ms.
- **The scan after a stopped check** was measured in an earlier build of this section to cost, per examined line in SQLite on the benchmark file, 497 ms for the 16 words of case 12 as one
  `MATCH` with `rowid = ?`, 375 ms for a four-part phrase of a word every line holds, 2.3 s for twenty parts and 8.1 s for sixty (case 15 did not finish in 10 s). The cause is that
  `rowid = …` does not bound the full-text work to one line: an AND of terms or a phrase walks on to the next matching row or the end of the index. The scan now tests every one-part
  word on its own (0.36 ms per line for the 16 words) and the terms of several parts in a private in-memory index of at most 64 examined lines, and its page ends at 1.5 s, so
  cases 12 and 15 end in under 1 s even when the index check is stopped (0.7 to 0.8 s on the development host; run 2 on the DS918+: 938 and 813 ms).
- **The 234-second run of case 12** was the scan of the earlier build: the index check was stopped at its budget on the cold first run, and the scan then examined 500 lines (8,000 divided by 16)
  at about 470 ms each, about 235 seconds. The benchmark of that moment passed no deadline (`CancellationToken.None`); the 10-second deadline of the page in `SearchBenchmark` was added
  right after, in the next run, which is why the same case 15 then reported "stopped by the 10 s deadline".
- **Residual:** one `MATCH` in a chunk of the line index runs to its end; crafted messages can keep a chunk busy for about 0.3 s on the development host and about 1.5 s on the DS918+ (an estimate of the
  security review), so a page can take up to about 3 s. The benchmark generates no such lines.
- **Cases 11 and 11b:** the statement summed 374,020 template hours through the index on the hour with a lookup per row (0.65 to 0.9 s warm, up to 1.2 s cold). It now sums over
  the table in key order (`+hour` in the range conditions) and joins the 12,914 templates afterwards: 30 to 43 ms. It reads the whole table whatever the range
  (a one-day range took 12 ms against 21 ms before, in the SQLite library on the same file), so its cost grows with the stored template hours, not with the range.
  For all range, source and program combinations that were compared (whole data, 30 days, one day; no filter, a source, a source and a program; limits 200 and
  1,000; 18 combinations) the old and the new statement returned identical rows in identical order.
- The scan modes rely on the planner reading `records(kind, source, captured_at)` or `records(kind, captured_at)` in order without a hint; the times of cases 3 to 9
  (a few milliseconds for a page that needs a few hundred lines) show that it does, also with a source (cases 4b and 6c).

## Process snapshot

Criterion (issue #32): a snapshot of 250 processes takes under 100 ms. `BenchmarkProcesses_Collect`
(`internal/procstats/procstats_test.go`) runs one `Collect` per iteration over the host's own `/proc` and `/etc` with memory pressure mode
on, so every process is read (`stat`, `status`, `cmdline`, `smaps_rollup`) and listed; it asserts no duration.

### Procedure

On a host with at least 250 processes (start sleeping processes in a scratch shell until `/proc` lists 250, then stop them):

```bash
for i in $(seq 1 200); do sleep 600 & done
ls /proc | grep -E '^[0-9]+$' | wc -l
go test ./internal/procstats -run '^$' -bench Collect -count 10
kill $(jobs -p)
```

### Results

| Host | Kernel | CPU | Date | Processes | Median of ten (ms) |
| ---- | ------ | --- | ---- | --------: | -----------------: |
| development host (4 vCPU, sandbox) | 6.18.44 | Intel Xeon 2.80 GHz | 2026-10-17 | 273 | **48.9** |

The ten runs took 48.0 to 55.6 ms. The development host is a sandbox: of its 273 processes the 190 sleepers started for the run
allowed reading `smaps_rollup`, while most of the sandbox's own processes (another user namespace) refused it, so the figure is for a
mix of processes with and without PSS; a host where all of them are readable costs somewhat more. The value on the server (Ubuntu 22.04, kernel 5.15, 2 GB) is **not measured**: #44 measures it. The cost of reading `smaps_rollup`
of every process every 15 seconds under memory pressure is an open point of #44 and #45 (Agent area, *Processes*).
