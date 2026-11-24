# Connection rollups

## Scope

What remains of the connection snapshots after `retention.snapshots`: for each source, the sockets per state, per program and per remote
address in 5-minute and hourly buckets, folded by the retention job, and the store reads that return the states and the top programs of a
source per step, its top remote addresses over a range and the sources that have them. The network view (#51) and anyone reading the
sockets of a server over months rely on it. The snapshots themselves, with their `states`, `processes` and `remotes`, are in
[Agent connections](agent-connections.md). Rollups of the metric values are in [Retention and rollups](retention.md); the tables are in
[Storage](storage.md); the reads' place among the others is [Storage reading](storage-reading.md). There is no option: the buckets follow
`retention.rollups_5m` and `retention.rollups_1h`.

## The buckets

- The job folds every `connection_snapshot` record of every origin (agent, import, backend) exactly once, in the order of its record ID,
  into the 5-minute bucket and the hourly bucket that hold its capture time, of the snapshot's source. A bucket is addressed by the
  source, the start of the bucket and, for a row, its kind (state, program or address) and name.
- **A row** holds, for one state, program or address in one bucket: `seen` (the snapshots of the bucket that name it) and the minimum,
  maximum and sum of its socket count. **A total row** holds, for the source and the bucket: `snapshots` (the snapshots folded into
  it), `incomplete`, `capped` and `trimmed` (below), the numbers of programs and addresses the bucket names, and the sum of the
  counts of all processes, named or not. No last value, percentile or process count is stored (record 0225).
- **States.** A snapshot's TCP states (`tcp` and `tcp6` added together, per state) and `UDP` (`udp` and `udp6` added together, whatever
  their state) are folded as one row per state; a state a snapshot does not list counts 0 in it. A snapshot that lists a state, program or
  address twice adds the entries up before it is folded.
- **Programs.** A program is the `command` of the snapshot's `processes`; its count in a snapshot is the sum of the `count` of every
  process of that command, so a socket held by several processes counts once per holding process. A program a snapshot does not name
  counts 0 in it. The total row sums the counts of all processes, which is what *other programs* follows from.
- **Addresses.** A remote address counts its `count` in the snapshot and 0 in a snapshot that does not list it. The bucket holds its sum
  and the highest count in one snapshot. An address is kept in the text form of the decoded snapshot, so an IPv4-mapped `::ffff:`
  address stays apart from its IPv4 address.
- **A program missing from a snapshot of its source is 0 in it.** The average of a state, program or address over a bucket is its sum
  over the bucket's `snapshots`, so the averages of all states of a bucket add up to the average of every counted socket. A bucket's
  minimum for a row is 0 when `seen` is below `snapshots`, else the stored minimum. Each snapshot weighs equally, whatever time it
  stands for (regular, full and pressure snapshots alike).
- **An incomplete snapshot** is one with `aggregates_incomplete`: the agent's lists were cut at their bounds. The total row counts them
  in `incomplete`. An imported snapshot (`legacy-monitor`) never carries the field, so imports count 0 there even when their `complete`
  is false. `complete` is false on an import for three causes: an `lsof` line was left out (unread, unmapped or too long), the cap of
  4,096 `lsof` lines was reached, or the input ended inside the snapshot's section, which is so for the last snapshot of every imported
  file ([Legacy monitoring log parser](log-parsers-legacy-monitoring.md)). The rollup does not count any of the three as incomplete;
  an imported snapshot's `processes` count the socket lines per process, the legacy parser's rule.
- **The cut per snapshot.** A snapshot that lists more than 50 programs is folded by name only for the 50 with the highest count in it,
  ties by program name in ordinal order; the others are in the total only and read as *other programs*. A snapshot that lists more than 25
  remote addresses (a full snapshot lists up to 4,096, an imported one every address) is folded only for its 25 with the highest count,
  ties by address text in ordinal order, as a regular snapshot holds them. An address cut from a snapshot reads lower there, without a
  flag and not in *further addresses*.
- **The cap per bucket.** A bucket names at most 500 programs: the first 500 names it meets in fold order stay named, a further name is in
  the total only. A 5-minute bucket names at most 500 addresses and an hourly bucket at most 1,500 (60 regular snapshots of 25 new
  addresses each), the first met in fold order; a further address is not stored. The fold order is the record ID, then within a
  snapshot the ordinal order of program names, and for addresses the count descending, ties by address text in ordinal order. A bucket
  counts its **capped snapshots**: the folded snapshots that lost at least one address to the cap. At the regular cadence of one
  snapshot a minute no hourly bucket reaches the cap. Above it (memory pressure, extra runs, a backfill or an import at a denser
  cadence) it can be reached: an address first met after it reads lower there, and a read at a step below one hour can differ from a
  read at a longer step; the capped count flags the bucket, and *further addresses* is a lower bound there.
- **The trim.** A bucket that ended at least 10 minutes before a fold step and holds more than 50 (5-minute) or 100 (hourly)
  addresses is cut to its 50 or 100 with the highest sum, ties by address text in ordinal order; the bucket counts the addresses
  trimmed in `trimmed`. A kept address keeps its exact sum and highest count; a trimmed address reads 0 in that bucket and is not in
  *further addresses*. States and programs are never trimmed. A step trims at most 20 buckets of a tier, the oldest first, and the job
  repeats steps while buckets are left.
  - **The caught-up gate.** A step trims only when its connection part folded every stored snapshot above the watermark: it read fewer
    than 500 snapshots and did not spend its byte budget (below). While a backlog is folded (the first pass after an upgrade or after
    downtime) no bucket is trimmed, so a bucket that spans several steps is whole before it is cut and the kept addresses equal those of
    the whole bucket folded at once. During a catch-up the ended buckets stay untrimmed until it ends.
  - A late snapshot merged into a trimmed bucket can add addresses, with the values of that snapshot alone; the next step trims the
    bucket again.
- **Names** (a `command` of up to 1,024 bytes, any text) are stored and returned as received, bound as parameters, never interpreted; a
  NUL, a control character or a right-to-left override stays in the name. A caller that shows one must escape it.
- **Sums are clamped** to the range of a double (plus or minus `double.MaxValue`) at every addition, in the fold, in the merge into a
  stored bucket and in every sum a read computes, so forged counts near 2^32 added many times never make a fold step fail and a read
  never returns Infinity or NaN.
- **A late snapshot** (an agent backfill, an import) whose bucket exists is merged into it, whether the bucket's snapshots are still
  stored or removed. A bucket past its tier's period is not created.
- **The fold step** reads at most 500 connection snapshots above the watermark `connection_rollup_after_id` by record ID and stops after
  the row that takes the stored data bytes read (UTF-8, before decoding) past 2 MiB; the first row is always taken. It runs in the
  transaction of the metric and program fold step ([Retention and rollups](retention.md), *The job*) and holds at most one step's data
  in memory. A database of an earlier version folds its stored connection snapshots first, in steps, before any connection snapshot is
  removed and before any bucket is trimmed.
- **A snapshot that does not decode** is skipped: NULL data, data that is not valid JSON or not valid UTF-8, a payload that is not a
  connection snapshot, or one the wire validation refuses. The skipped snapshot is counted in the log line (event 79), the watermark
  passes it, and the removal takes it with its period, so it never stops folding or removal. No command, address or user of it is
  logged.
- **Failure and busy** are those of the metric fold: the step rolls back as a whole, the job logs its warning or error line and the
  next tick retries it.

## Reading

Every read runs on a reader connection under the caller's cancellation ([Storage](storage.md), *Database file and connections*).

- **States and programs** of one source for a range and a step. The step is one of the time-series query's steps (5, 10, 15, 30
  minutes, 1, 2, 3, 6, 12 hours, then whole days up to 36,500). A step below one hour reads the 5-minute buckets, every other step the
  hourly ones. Steps lie on the grid of multiples of the step since the Unix epoch (UTC); the first step is the one that holds `from`, so
  a `from` off a boundary starts at the boundary before it. A range with `from` at or after `to` returns the empty result.
- **The result** holds the step starts (`Times`), every state of the range, in the ordinal order of its name, and the 10 programs with the
  highest sum over the range, in that order (which is the highest average, since every program is divided by the same snapshots), ties by
  program name, each with one point per step (average, minimum, maximum) or a gap; the average of the sockets of all other programs per step
  as *other programs* (`Others`); the number of folded snapshots per step that were incomplete (`Incomplete`).
  - A state or program a step's snapshots do not name reads 0 in that step; a step without a folded snapshot is a gap: `null` in a line
    and in *other programs*, 0 in the incomplete count.
  - *Other programs* is the average of the total of the source less the named programs, computed from the clamped sums and kept within
    0 and `double.MaxValue`; it includes the programs cut or capped in the fold. The named programs and *other programs* add up to the
    average of the counts of all processes, unless a fold step commits between the read's queries, which share one reader connection
    without a shared transaction: then a step's named programs can differ from its total by that fold step's snapshots, and *other
    programs* stays at 0 at the least.
  - `EarliestUnfolded` is the earliest capture time of the source's connection snapshots not yet folded (none when all are), and
    `StartsBeforeBuckets` is true when the range starts before the oldest bucket the source has in the tier the step reads. A step below
    one hour over a range that starts before the 5-minute period returns what the 5-minute buckets still hold: gaps before, the flag set.
- **Remote addresses** of one source for a range and a step, which only decides the tier and the grid of the per-step figures. The
  result holds the 20 addresses with the highest sum over the range, ties by address text, each with its average (the sum over all
  snapshots of the range, an address a snapshot does not list counting 0) and its highest count in one snapshot; the number of **further
  addresses** the range's buckets hold beyond them (`Further`, at least 0); the step starts; the gaps (steps without a folded snapshot);
  the incomplete count and the capped-snapshot count per step; `EarliestUnfolded` and `StartsBeforeBuckets` as above. The addresses are
  those the buckets kept, so an address cut per snapshot, beyond a bucket's cap or trimmed counts 0 in that bucket and is not in
  *further addresses*.
- **Sources**: the sources that have a connection bucket in a range of either tier, by source in the byte order of SQLite, with the start
  of the latest bucket within the range, at most 256, with a flag when more exist.
- **Memory:** a read holds at most one line per state the range names, 10 program lines, *other programs* and 20 addresses,
  of at most 2,000 steps.
- **Limits and errors.** An unknown source, an empty range or no bucket is the empty result, no error. Every other broken rule is the
  store's invalid-query error `store: invalid query: <text>` before any read: `step must be one of the query's steps`, `from and to must
  be within the storable range` and `the range holds more than 2000 steps` (steps of the epoch grid that overlap the range; a year at a
  5-minute step is one such range), in that order. The source list refuses only a bound outside the storable range. A failing
  statement is `store: reading connection rollups` with SQLite's message; a cancelled call ends as cancelled. No consumer reads these
  yet; the network view (#51) adds it with its read gate.

## Disk and time

Measured with `--connections` of the storage benchmark on the development host and on the SSD volume of the reference host
(`docs/BENCHMARKS.md`, *Connection rollups*); the figures below are the reference host's.

- **Disk bound.** Per source and per day of capture time at most about 288 × 50 + 24 × 100 address rows stay once trimmed, plus up to
  1,500 address rows per open hour. The source is the agent's choice, and a bucket at or below 50 (5-minute) or 100 (hourly) addresses
  is never trimmed, so forged sources or backdated snapshots leave rows kept for `retention.rollups_5m` and `retention.rollups_1h`,
  beyond `retention.snapshots`. The free-space floor `ingest.min_free` stays the bound of last resort against forged churn, as for
  program rollups ([Retention and rollups](retention.md), *Limits and errors*).
- **A day** of one regular snapshot a minute (1,440 snapshots, 13 states, 40 processes of 15 programs, 25 remote addresses no earlier
  snapshot named) added 1,884 pages of 4,096 bytes (7,536 KiB) to the file and left 27,086 rollup rows after the trim; the fold and the
  trim took 4,290 ms: 5 folding steps and 14 steps that only trimmed. The file grows by the untrimmed buckets, which are written whole before the trim cuts them, and the freed pages are
  reused.
- **The yearly cost at the default periods**, per source under full address churn (every snapshot naming 25 new addresses): 365 days
  of `rollups_5m` are 365 × 288 × (50 + 13 + 15) = 8.2 million rows, 1,095 days of `rollups_1h` are 1,095 × 24 × (100 + 13 + 15) = 3.4
  million rows, a year of the hourly tier alone 1.1 million. A row measured about 53 bytes in its table and about 97 bytes with the
  index on `bucket` (a copy of the tables filled with 8,760 hourly buckets of 100 addresses, 13 states and 15 programs: 53.7 and 99.0
  bytes; 5-minute buckets of 50 addresses: 52.2 and 96.1), so the tables alone take roughly 600 MB and with their index roughly 1.1
  GB per source. A source with few distinct addresses keeps far fewer rows. These are estimates from the measured row size, not a
  measured year. The row size was measured on the development host.
- A fold step beside 10,000 metric values, 500 process snapshots of 80 programs and 500 regular connection snapshots took a median of
  1,203 ms (10 steps of 95 to 2,826 ms), the median ingest batch of the same run 1,942 ms. The 22 steps that only trimmed took 722 ms
  in all (32.8 ms each) and trimmed 95,800 addresses. A step of full snapshots (8 snapshots of 4,096 remote addresses and 4,096
  connections, 30 listeners, up to the 2 MiB budget) folded in 3 steps, the slowest 332 ms, 651 ms in all.
- 1,095 days of hourly buckets at the trimmed size (26,280 buckets of 100 addresses, 13 states and 15 programs, 3,363,840 rows, written
  directly by SQL, not folded): a read over the last year at a 6-hour step took a median of 1,525 ms for states and programs and 2,697 ms for
  the addresses; a read over all 1,095 days at a 1-day step 4,251 ms and 8,022 ms (medians of 5), within the views' 10-second
  deadline. The fallback of record 0225 (an hourly bucket keeps 50 addresses) is not taken.
- Trimming an untrimmed hour of 1,500 addresses took 19 ms. Schema step 17 took 47 ms on a database with 8,760 stored hourly connection
  snapshots.

## Related decisions

- [0225](../decisions/0225-connection-snapshots-are-folded-into-per-state-program-and-address-buckets-ended-buckets-trimmed.md) — why connection snapshots are folded into per-state, per-program and per-address buckets, and why ended buckets keep their top addresses.
- [0223](../decisions/0223-process-snapshots-are-folded-into-per-program-buckets-beside-a-source-total.md) — the program fold this one follows (cut, cap, total, watermark).
- [0208](../decisions/0208-metric-rollups-are-folded-from-new-rows-by-record-id-never-recomputed-from-raw.md) — the watermark rule and the merge of late values.

## Not here

- The retention periods, the removal walk and the log lines: [Retention and rollups](retention.md).
- The tables, the indexes and the migration step: [Storage](storage.md).
- The meaning of a snapshot's fields and how the agent bounds them: [Agent connections](agent-connections.md); the fields of the record: [Wire format records](wire-format-records.md).
- The network view that will read the result and its gate: planned (#51). Rollups of the `connections` list, local addresses or ports, listeners or users, distinct sockets per program, a minimum per address and percentiles do not exist.

## Implementation

`Vandox.Storage`: `ConnectionRollupFolder` (the fold and the trim, with `ConnectionBucket`, `ConnectionItemTotals`, `ConnectionRollupTier`,
`ConnectionRollupSql`), `ConnectionRollupQueries` (the reads, with `ConnectionStepTotals`, `ProgramStepGrid` and the shared `RollupSteps`),
`IConnectionRollupReader` (`ConnectionRollupsAsync`, `ConnectionAddressesAsync`, `ConnectionSourcesAsync`), implemented by `SqliteStore` with the
types `ConnectionRollupQuery`, `ConnectionRollupResult`, `ConnectionRollupLine`, `ConnectionAddressResult`, `ConnectionAddress`, `ProgramSourceQuery`
and `ProgramSourceList`. The limits are in `StorageLimits`. The measurement is `--connections` of `tools/Vandox.StorageBenchmark`.
