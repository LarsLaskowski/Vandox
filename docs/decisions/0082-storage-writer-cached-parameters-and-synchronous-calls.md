# 0082: The storage writer sets cached parameters and calls SQLite synchronously after the first NAS measurement

- **Status:** Accepted
- **Date:** 2026-10-07
- **Source:** Issue #14 and #129 (storage write criterion), Product Manager decision after the first DS918+ measurement
- **Supersedes:** —

## Context

[0077](0077-storage-on-microsoft-data-sqlite-same-schema-and-rules.md) kept the criterion of #14: 10,000 records in one transaction
in under one second on the reference host, a DS918+. The first measurement there (`tools/Vandox.StorageBenchmark`, .NET 10.0.10,
Linux 4.4, data volume `/volume2`) failed it: the first batch on a fresh database took 1480 ms, the next four 890 to 1119 ms
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

Tried and not adopted: runtime switches (`TieredPGO` on or off, `ReadyToRun` on the dev host) changed nothing measurable; a larger
SQLite page cache (`cache_size` 16 MiB) made the fastest batch about 18 % faster on the dev host but the medians were within the noise and it
costs memory per connection in a 512 MiB container, so it is a candidate only if the NAS measurement asks for it.

## Consequences

- On the development host the first batch dropped from about 500 ms to about 290 ms and the median from 310–390 ms to 270–320 ms.
  Whether that is enough on the NAS is open: scaled by the factor of three it is around the limit, so the NAS has to be measured
  again (`docs/BENCHMARKS.md`); if it still fails, the next candidates are the page cache, `ReadyToRun` in the image, and then a
  recorded change of the criterion.
- The writer no longer awaits per statement, so a very large batch holds the writer thread for its whole duration (it did before
  as well, because SQLite is synchronous); cancellation is still honored between records.
- The first NAS result is kept in `docs/BENCHMARKS.md` as the baseline.
