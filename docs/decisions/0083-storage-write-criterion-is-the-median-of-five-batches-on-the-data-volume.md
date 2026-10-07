# 0083: The storage write criterion is the median of five batches on the volume that holds the database

- **Status:** Accepted
- **Date:** 2026-10-07
- **Source:** Issue #14 and #129, Product Manager decision after the DS918+ measurements
- **Supersedes:** —

## Context

Issue #14 required that writing 10,000 records in one transaction takes under one second on the reference host, a DS918+ (record
[0068](0068-write-throughput-measured-by-a-benchmark-ds918-measurement-in-a-follow-up.md)). It does not say which batch counts or which
volume. After the writer change of [0082](0082-storage-writer-cached-parameters-and-synchronous-calls.md) the benchmark tool measured on the
reference host: on the SSD volume a first batch of 1001 ms and later batches of 711 to 783 ms (median of five 783 ms); on the HDD volume a first batch
of 1171 ms and a median of 1171 ms. The first batch includes start-up and the growth of a fresh file and was 1 ms over the limit, although every
other batch was far below it. The agent sends hundreds of records per minute, so 10,000 per transaction is a stress value.

## Options considered

1. **Keep the first batch as the criterion and optimize further** — the next lever is the schema (four index entries per metric record), which
   needs a migration and costs query time, for a 1 ms gap.
2. **Restate the criterion as the median of five batches on the data volume** — it describes steady write capacity, which is what the stress value
   stands for, and it reports the first batch next to it.
3. **Change `synchronous` to `NORMAL`** — would remove the wait for the disk on the HDD but gives up that a committed batch survives a power failure
   (record [0065](0065-sqlite-connections-single-writer-query-only-readers-synchronous-full.md)).

## Decision

Option 2. The criterion is: the **median of five batches** of 10,000 records, one transaction each, takes under one second on the reference host,
measured with `tools/Vandox.StorageBenchmark` on the volume that holds the database; the first batch is reported as well. `synchronous=FULL` stays.
The database may be on any volume. The SSD is recommended: there the criterion is met (783 ms). On the HDD volume it is not (1171 ms), because every
commit waits for the disk (about 122 ms per flush there, 10 ms on the SSD), but the writes are still far above the agent's real load, so the HDD is
possible and the slower writes are documented in `docs/BENCHMARKS.md`.

## Consequences

- Issues #14 and #129 are met on the recommended volume; the result on the HDD is documented, not hidden.
- The benchmark tool stays in the repository to repeat the measurement after a change of the storage layer, the schema or the host.
- A later change of the schema, of `synchronous` or of the writer is measured against the same criterion with the same tool.
- The deployment files and the README recommend the SSD for `/data` without naming volumes.
