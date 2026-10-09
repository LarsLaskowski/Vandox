# 0083: The storage writer is cheap, and its write criterion is the median of five batches on the volume that holds the database

- **Status:** Accepted
- **Date:** 2026-10-07
- **Area:** Storage
- **Source:** Issue #14 and #129, Product Manager decision after the DS918+ measurements
- **Supersedes:** —

## Context

Issue #14 requires that writing 10,000 records in one transaction takes under one second on the reference host, a DS918+
([0077](0077-storage-on-microsoft-data-sqlite-same-schema-and-rules.md)). It does not say which batch counts or which volume. The first
measurement there failed it (first batch 1480 ms, the next four 890 to 1119 ms; about three times slower than the development host).
A transaction has one `fsync`, so the time was CPU-bound, and the writer spent CPU where nothing was needed: parameters set by name
lookup, labels serialized through a general-purpose serializer, an asynchronous call per statement that runs synchronously anyway.
After the writer was made cheaper, the SSD volume gave a first batch of 1001 ms (1 ms over the limit, fresh file and start-up) and a
median of 783 ms; the HDD volume gave a median of 1171 ms, because every commit waits for the disk (about 122 ms per flush against
10 ms). The agent sends hundreds of records per minute, so 10,000 per transaction is a stress value.

## Options considered

1. **Make the writer cheaper and measure again** (done first) — same SQL, same schema, same stored text.
2. **Relax the criterion at once** — rejected before the cheap fix was tried.
3. **Optimize further to pass on the first batch** — the next lever is the schema (four index entries per metric record), which needs a migration and costs query time, for a 1 ms gap.
4. **Restate the criterion as the median of five batches on the data volume** (chosen) — it describes steady write capacity, which is what the stress value stands for, and the first batch is reported next to it.
5. **Change `synchronous` to `NORMAL`** — removes the wait for the disk on the HDD but gives up that a committed batch survives a power failure (0077).

Runtime switches (`TieredPGO`, `ReadyToRun`) changed nothing measurable.

## Decision

The writer keeps its parameters and calls SQLite synchronously, and the writer connection gets a 16 MiB page cache so a large transaction
does not spill pages before the commit. The criterion is restated as option 4; `synchronous=FULL` stays. The database may be on any
volume, the SSD is recommended. The criterion and the measured results are in the [Storage](../areas/storage.md) area (*Write throughput*).

## Consequences

- Issues #14 and #129 are met on the recommended volume; the HDD result is documented, not hidden.
- The writer holds its thread for the whole of a very large batch (as SQLite is synchronous); cancellation is honored between records.
- The page cache costs up to 16 MiB for the single writer connection, which fits the container's memory limit.
- The benchmark tool stays in the repository to repeat the measurement; a later change of the schema, of `synchronous` or of the writer is measured against the same criterion.
- The deployment files and the README recommend the SSD for `/data` without naming volumes.
