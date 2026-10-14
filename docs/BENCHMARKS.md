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

Development host (4 vCPU, Intel Xeon 2.10 GHz, Ubuntu 24.04, .NET 10.0.12), 5,000,000 lines written in 264 s (18,915 lines per second), database file
1,983 MiB, 12,914 templates, after the changes of this section (scan bound 8,000 divided by the word parts, the scan tests the user's text only, the index check
under a budget of 0.7 s, the index mode reads the lines by the IDs of the probe, the template list sums in key order). Times in milliseconds: the first run, then the
slowest of the three:

| Case | Mode | Result | First run | Slowest |
| ---- | ---- | ------ | --------: | ------: |
| 1 rare word | index | 100 lines | 39 | 39 |
| 2 word in 1 % | scan | 80 lines, scan bound reached | 213 | 213 |
| 2b word in 0.33 % | scan | 27 lines, scan bound reached | 150 | 162 |
| 3 word in 33 % | scan | 100 lines | 10 | 10 |
| 4 with program and severity | scan | 100 lines | 12 | 12 |
| 4b with a source | scan | 100 lines | 10 | 11 |
| 5 two words, rare combination | index | 100 lines | 232 | 232 |
| 6 program only (4 %) | scan | 100 lines | 3 | 3 |
| 6b rare program | scan | 1 line, scan bound reached | 6 | 13 |
| 6c rare program in one source | scan | 2 lines, scan bound reached | 10 | 10 |
| 7 one-day range | scan | 100 lines | 13 | 13 |
| 8 most frequent template | scan | 100 lines | 36 | 36 |
| 8b rare template | scan | 0 lines, scan bound reached | 27 | 500 |
| 9 scan bound, no match | scan | 0 lines, scan bound reached | 287 | 313 |
| 10 context | n/a | 5 + 5 lines | 48 | 48 |
| 11 template list, whole data | n/a | 200 templates | 93 | 93 |
| 11b template list, 30 days | n/a | 200 templates | 30 | 30 |
| 12 16 words, empty intersection | index | 0 lines | 621 | 621 |
| 13 eight words together, range without them | scan | 0 lines, scan bound reached | 157 | 157 |
| 14 four-part term of common parts | index | 0 lines | 137 | 138 |
| **15 crafted 511-part term** | index check stopped | **stopped by the 10 s deadline of the page** | **10,116** | **10,116** |

**Verdict on the development host: criterion not met, because of case 15.** Every other case is under 2,000 ms, and all but case 12 (621 ms on the first run) are under the 350 ms target (case 8b is 500 ms in one run of three). Case 15 does not end within the 10 seconds the page
allows: its index check is stopped at the budget, but the scan page after it is not (notes). Cases 12 and 14 were not stopped in these runs (the warm index check
ended within 0.7 s), so the scan after a stopped check was not measured for them in this table; a run in which the check of case 12 was stopped (the first
run of case 12 of an earlier run of this build, with a cold cache) took **234 seconds**. Reference host, run 2 (DS918+, SSD volume): **not yet measured.**

| Host | Date | Slowest case | Verdict |
| ---- | ---- | ------------ | ------- |
| DS918+ (reference host, SSD volume), run 1 | 2026-10-14 | case 12, 2,229 ms | **not met** (cases 9 and 12) |
| DS918+ (reference host, SSD volume), run 2 | _to be entered after the Product Manager's run_ | _?_ | _?_ |

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
is given up and the request goes through the range as a scan (case 12 on the reference host took 2.2 s). The budget, the bound and the cap are values of the code, not
of the configuration.

### Notes

- **Case 12** probes the index for the record IDs of 16 words that each occur in 1,250,000 lines and have no line in common. FTS5 has to walk their whole
  entries to find that (about 0.45 s inside the full-text module on the development host). The progress handler runs there (gaps of 20 to 31 ms in a scratch
  measurement with terms of 4 to 511 parts, an interrupted probe ended at 0.71 s), so the check is stopped by the deadline or by the budget. Before the change
  of the index mode the query then walked the entries a second time (1,232 ms). On the development host the check ends within the budget when the cache is warm (434 to 621 ms) and
  would not on the reference host (2.2 s).
- **The scan after a stopped check is the open problem.** It is bounded by its line bound, not by the clock, and the per-line test of the terms of the cases 12 and 15 is
  very slow: measured in SQLite on the benchmark file, `MATCH` of the 16 words of case 12 with `rowid = ?` took 497 ms per line (500 lines at the bound of 8,000 divided
  by 16: about 250 s; the 234 s run above), a four-part phrase of a word every line holds 375 ms per line, twenty parts 2.3 s and sixty parts 8.1 s per line (case 15:
  15 lines at 511 parts), against 0.3 ms for one word and for the eight words that occur together (case 13). Testing the 16 words as 16 separate `EXISTS` of one
  word each (the same rows) took 0.36 ms per line. A phrase of several tokens is not made cheaper that way.
- **Cases 11 and 11b:** the statement summed 374,020 template hours through the index on the hour with a lookup per row (0.65 to 0.9 s warm, up to 1.2 s cold). It now sums over
  the table in key order (`+hour` in the range conditions) and joins the 12,914 templates afterwards: 30 to 43 ms. It reads the whole table whatever the range
  (a one-day range took 12 ms against 21 ms before, in the SQLite library on the same file), so its cost grows with the stored template hours, not with the range.
  For all range, source and program combinations that were compared (whole data, 30 days, one day; no filter, a source, a source and a program; limits 200 and
  1,000; 18 combinations) the old and the new statement returned identical rows in identical order.
- The scan modes rely on the planner reading `records(kind, source, captured_at)` or `records(kind, captured_at)` in order without a hint; the times of cases 3 to 9
  (a few milliseconds for a page that needs a few hundred lines) show that it does, also with a source (cases 4b and 6c).
