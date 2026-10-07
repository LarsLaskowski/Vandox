# 0082: The storage writer sets cached parameters and calls SQLite synchronously after the first NAS measurement

- **Status:** Accepted
- **Date:** 2026-10-07
- **Source:** Issue #14 and #129 (storage write criterion), Product Manager decision after the first DS918+ measurement
- **Supersedes:** —

## Context

[0077](0077-storage-on-microsoft-data-sqlite-same-schema-and-rules.md) kept the criterion of #14: 10,000 records in one transaction
in under one second on the reference host, a DS918+. The first measurement there (`tools/Vandox.StorageBenchmark`, .NET 10.0.10,
Linux 4.4, SSD volume `/volume2`; the HDD volume is slower and was not measured) failed it: the first batch on a fresh database took 1480 ms, the next four 890 to 1119 ms
(6,755 to 11,241 records/s). The development host (4 vCPU Xeon) needed about 500 ms for the first and 310 to 390 ms for the later
batches, so the NAS is about three times slower. A transaction has one `fsync`, so the time is CPU-bound, and the writer spent CPU
where nothing was needed: per record 17 parameters were set by a name lookup, a sorted copy of the labels was serialized
through a general-purpose serializer, and every statement went through an asynchronous call that Microsoft.Data.Sqlite runs
synchronously anyway.

## Options considered

1. **Make the writer cheaper and measure again** — create the parameters once and set them by reference, call
   `ExecuteScalar`/`ExecuteNonQuery`, serialize the labels with a reused `Utf8JsonWriter`. Same SQL, same schema, same stored text.
2. **Relax the criterion** — for example the median of the later batches, or a higher limit. The data is not worse than needed
   for the agent's real load (hundreds of records per minute), but the criterion would be weakened before the cheap fix was tried.
3. **Only record the result** — accept that the criterion is not met.

## Decision

Option 1. `BatchWriter` holds its `SqliteParameter` objects and sets their values by index, calls the commands synchronously
(`Write` replaces `WriteAsync`; cancellation is checked per record), and serializes labels with a reused writer
(`BatchWriter.SerializeLabels`; a test pins that the text equals the former `JsonSerializer` output of a `SortedDictionary` in
ordinal order, so stored rows do not change). The criterion of #14 stays as it is; the benchmark tool gets `--warmup` to show how much
of the first batch is start-up cost.

The first measurement after this change on the DS918+ (writer without the page cache below) gave 1043 ms for the first batch and
797 to 1004 ms for the others (median 925 ms): 30 % better, still just over the limit. The writer connection (only that one; the readers keep
the default) therefore also gets `PRAGMA cache_size=-16384`, up to 16 MiB of pages instead of 2 MiB, so a large transaction does not spill pages
before the commit; on the development host this made the fastest batch about 18 % faster, the medians were within the noise. The benchmark tool
now also prints how long a 4 KiB write takes to reach the disk on the volume, to show whether waiting for the disk plays a part.

Tried and not adopted: runtime switches (`TieredPGO` on or off, `ReadyToRun` on the dev host) changed nothing measurable.

## Consequences

- On the development host the first batch dropped from about 500 ms to about 290 ms and the median from 310–390 ms to 270–320 ms; on
  the NAS from 1480 to 1043 ms. The NAS has to be measured again with the page cache (`docs/BENCHMARKS.md`); if it still fails, the
  next candidates are `ReadyToRun` in the image, a look at the schema's four index entries per metric record, and then a recorded
  change of the criterion.
- The page cache costs up to 16 MiB of memory for the single writer connection, which fits the 512 MiB limit of the container.
- The writer no longer awaits per statement, so a very large batch holds the writer thread for its whole duration (it did before
  as well, because SQLite is synchronous); cancellation is still honored between records.
- The first NAS result is kept in `docs/BENCHMARKS.md` as the baseline.
- Result with the page cache on the DS918+: first batch 1001 ms (the limit is 1000 ms), later batches 711 to 783 ms (median 783 ms), a
  4 KiB write needs about 10 ms to reach the disk on the SSD volume, so the writer is CPU-bound there; the HDD volume is slower and has not been measured, so the result is the best case. `ReadyToRun` showed no difference on the development
  host. The first batch is about 250 ms slower than the others (fresh file, start-up); the criterion names exactly that batch, so
  whether 1001 ms against 1000 ms counts as met, or the criterion is restated (for example the median of five batches), needs a
  decision record of its own once the Product Manager has decided.
