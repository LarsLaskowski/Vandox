# Plan: Recognize backfilled data from the data itself

Source: Issue #41 | [spec.md](spec.md)
Status: Revised (1)
Tier: standard — a behavior change in the ingest write path (classification, gap and offset bookkeeping) that creates, changes or removes no security control: deduplication, token, limits and the drain stay as they are.
Security areas touched: 1 Ingest authentication (`BatchWriter` and the `SqliteStore` write transaction it shares with the deduplication, `IngestEndpoint.Acknowledge` log lines; the statement "the only rows it ever updates are `listener_comparisons` rows" changes), 12 Logging and display of external data (new `vandoxd` log lines carrying the agent ID, counts and an offset)
Plan size: the revision answers three blocking Security findings and four challenge objections in the plan's own sections, on top of a write path with sequence gaps, time gaps, an offset estimator and a schema step.

## Problem / root cause

See [spec.md](spec.md), *Problem*. Today `IngestEndpoint` passes no classification to the store and logs the header's `mode`
only (`src/Vandox.Backend/Ingest/IngestEndpoint.cs:835`); `BatchWriter.Write` (`src/Vandox.Storage/BatchWriter.cs:91`) inserts
records without one, and nothing detects gaps or measures an offset.

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | The header's `mode` never reaches the store: `RecordBatch` has no mode, the endpoint only logs it | `RecordBatch.cs`; `IngestEndpoint.cs:314-321`, `:835` | confirmed |
| 2 | All records of a batch share one receive time, taken when the request arrives | `IngestEndpoint.cs:351`, `:319` | confirmed |
| 3 | The decoder refuses a batch whose `seq` does not strictly increase | Wire format area, *Records*, `ErrSequence` | confirmed |
| 4 | A `records` row is never updated; duplicates are `ON CONFLICT DO NOTHING` | `BatchWriter.cs:46-49`; Storage area, *Writing* | confirmed |
| 5 | Every batch write runs in one writer transaction under `_writeLock` (shared with imports and notes), so two ingest slots commit one after the other | `SqliteStore.cs:418-435`, `:460-493` | confirmed |
| 6 | The agent sends no clock offset | Agent area, *What is kept*; no writer of `ClockOffsetNS` in `internal/` but `internal/spool/read.go:228` | confirmed |
| 7 | `agent_send_lag_seconds` is now minus the newest acknowledged capture time, before the first acknowledgement minus the start; records of the same tick are not acknowledged yet, so on an agent without log traffic the lag is about one `system` interval (30 s) plus the send period | `internal/sender/sender.go:230-243`; Agent area, *Send lag*, *System metrics* (30 s), *Sending* (4 s) | confirmed |
| 8 | `metric` records come at least every 60 s (`system` 30 s, `self`, `filesystem`, `clock` 60 s) | Agent area, *Self-metrics*, *System metrics* | confirmed |
| 9 | The live stream starts after the newest record (or at the oldest unacknowledged with 32 ranges); the backfill sends oldest first and never moves past an unacknowledged record | Agent area, *Sending* (lines 1744-1747, 1769-1771) | confirmed |
| 10 | Numbers continue after a clean stop or a `kill -9` in the same boot; an unclean stop in an earlier boot jumps | Agent area, *Sequence numbers* | confirmed |
| 11 | Agent gap records naming numbers carry numbers below their own | Agent area, *Size bound*, *Damaged data*, *Refused batches* | confirmed |
| 12 | A `log_line`'s capture time is the line's own time (year-less lines up to a day after the run, zones ignored, other hosts' clocks), not an agent clock reading | Agent area, *Log shipping* (lines 1197-1211) | confirmed |
| 13 | Capture times may lie anywhere in the storable range, which spans the whole signed 64-bit nanosecond count; a capture-minus-receive difference can exceed it | `StorableTime.cs:13,18`; `RecordRules.cs:39` | confirmed |
| 14 | `Gap.FirstSeq`/`LastSeq` are `ulong` and not bounded to 2^63 − 1 | `Gap.cs:42,49`; `RecordRules` checks only `record.Seq` | confirmed |
| 15 | `RecordQueries.Read` reads columns by position; every record select is built by `SelectColumns` | `RecordQueries.cs:17-21`, `:171-227`; callers in `LogExplorerQueries`, `LogFinderQueries`, `LogFinder` | confirmed |
| 16 | 15 assertions compare a whole `WriteResult` (among them `SqliteStoreWriteTests`, `SqliteStoreTemplateTests`, `SqliteStoreListenerHistoryTests`, `SqliteStoreTemplateRebuildTests`, `SqliteStoreLogExplorerTests`); one test calls `BatchWriter.Write` directly | `grep -rn "Assert.AreEqual(new WriteResult" tests` (15 hits); `SqliteStoreWriteTests.cs:226` | confirmed |
| 17 | Schema version is 11; the new step is 12 | `StorageLimits.cs:18` | confirmed |
| 18 | Records 0022 and 0046 are released (tag `v0.1.0`), so 0022 is amended by a new record | `git ls-tree v0.1.0 docs/decisions/` | confirmed |
| 19 | `tools/Vandox.StorageBenchmark` writes contiguous numbers only | `tools/Vandox.StorageBenchmark/Program.cs:198,291` (`seq++`) | confirmed |
| 20 | The added work keeps the write-throughput criterion | to verify: the Dev runs the benchmark before and after, with the extra backfill scenario of *Approach*, on the development host in step 6 and records it in `docs/BENCHMARKS.md`; the reference host stays with #44 | unverified |

## Acceptance criteria

The criteria are those of [spec.md](spec.md), AC1 to AC10.

## Tests per criterion

`T` = `tests/Vandox.Storage.Tests/SqliteStoreTimelineTests.cs` (class `SqliteStoreTimelineTests`), a real `SqliteStore` driven
through `AgentReplay` (*Test files*).

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `LiveRulesMetricWithinNinetySecondsIsLive`, `LiveRulesMetricNinetyOneSecondsLateIsBackfilled`, `LiveRulesLogLineNineMinutesLateIsLive`, `LiveRulesKernelEventElevenMinutesLateIsBackfilled`, `LiveRulesEarlyBeyondAllowanceIsBackfilled`, `LiveRulesOpenGapBelowIsBackfilled`, `LiveRulesExtremeTimesDoNotOverflow`; `SqliteStoreTimelineClockSteppedForwardFreshRecordsBackfilled`; `IngestEndpointTimelineBackfillModeFreshRecordsStoredLive`, `IngestEndpointTimelineLiveModeOldRecordsStoredBackfilled` | `tests/Vandox.Storage.Tests/LiveRulesTests.cs`; T; `tests/Vandox.Backend.Tests/IngestEndpointTimelineTests.cs` |
| AC2 | `SqliteStoreTimelineThirteenHourOutageNeverStoresOutageRecordLive` (one sequence and one time gap opened; every record of the batches before the closing one backfilled; the closing batch's records live exactly when within 90 s of receipt; no other record captured 22:00-11:00 live; offset unchanged until both close; both `closed` with the last number; the next batch live) | T |
| AC3 | `SqliteStoreTimelineSameBootRestartDuringBackfillKeepsFirstClassification`, `SqliteStoreTimelineRebootJumpBecomesFinalAfterFiveMinutes`, `SqliteStoreTimelineWaitRestartsAfterReopen`, `SqliteStoreTimelineRefusedFirstBatchAfterReopenKeepsWaitReset` | T |
| AC4 | `SqliteStoreTimelineAgentSpoolDroppedGapFinalizesNamedNumbers`, `SqliteStoreTimelineAgentGapAtOrAboveOwnNumberFinalizesNothing` | T |
| AC5 | `SqliteStoreTimelineCleanStopHourIsFinalTimeGap`, `SqliteStoreTimelineTimeGapOpenWhileSequenceGapBetween`, `SqliteStoreTimelineTimeGapBelowFirstNumberDetected`, `SqliteStoreTimelineTimeGapBesideAgentGapStored`, `SqliteStoreTimelineTimeGapIgnoresOffsetChange` | T |
| AC6 | `ClockEstimatorBehindTwoHoursMeasuredFromCountedSample`, `ClockEstimatorAheadTwoHoursFromRunTimedRecordsBeforeCountedSample`, `ClockEstimatorLogLineNeverRaisesEstimate`, `ClockEstimatorLateSampleNeverLowersEstimate`, `ClockEstimatorBackwardStepRecoversWithinFiveMinutes`, `ClockEstimatorPreSampleBoundExpiresAfterFiveMinutes`, `ClockEstimatorExpiryFallsBackToWindowMaximumNotBackfillBatch`, `ClockEstimatorSaturatesExtremeBounds`; `SqliteStoreTimelineClockAheadNeverMakesOldRecordLive`, `SqliteStoreTimelineClockBehindLiveFromCountedSample`, `SqliteStoreTimelineQuietAgentSamplesAreCounted`, `SqliteStoreTimelineClockAheadBackfillAfterExpiryStaysBackfilled` (agent 2 h ahead, installed while the backend was off, live and backfill batches mixed so that a backfill batch is the first after an expiry) | `tests/Vandox.Storage.Tests/ClockEstimatorTests.cs`; T |
| AC7 | `SqliteStoreTimelineNoSequenceGapBelowFirstNumber`, `SqliteStoreTimelineMiddleFillSplitsGap`, `SqliteStoreTimelineFinalGapShrinksAndCloses`, `SqliteStoreTimelineGapStatesSurviveReopen` | T |
| AC8 | `SqliteStoreTimelineRefusedBatchChangesNothing`, `SqliteStoreTimelineConcurrentBatchesMatchCommitOrder`, `SqliteStoreTimelineJumpInNumberAndTimeAddsAtMostTwoRowsPerRecord`, `SqliteStoreTimelineNumbersNearMaximumWork`, `SqliteStoreTimelineAgentGapRangeAboveLongMaximumWorks`, `SqliteStoreTimelineCaptureTimesAtStorableEndsStoredBackfilled`; `AgentTimelineQueriesUseIndexSearchesOnly`; `IngestEndpointTimelineCaptureTimeAtStorableEndAnswers204` | T; `tests/Vandox.Storage.Tests/AgentTimelineQueriesTests.cs`; `tests/Vandox.Backend.Tests/IngestEndpointTimelineTests.cs` |
| AC9 | `SqliteStoreTimelineImportRecordsReadBackfilled`, `SqliteStoreOpenMigratesAgentRecordsAsBackfilledWithoutGaps` | T; `tests/Vandox.Storage.Tests/SqliteStoreOpenTests.cs` |
| AC10 | `SqliteStoreTimelineRecordsCarryClassification`, `SqliteStoreTimelineGapsAndClockReadable`; `IngestEndpointTimelineGapsChangedLoggedOncePerBatch`, `IngestEndpointTimelineClockOffLoggedOnceThenInStep` | T; `tests/Vandox.Backend.Tests/IngestEndpointTimelineTests.cs` |

## Approach

**Arithmetic (B1).** Every difference of two instants (capture minus receive, capture minus capture, receive minus `fill_at`) is
computed in `Int128` nanoseconds; no checked operation exists on this path. A bound or offset stored or carried as `long` is
saturated to `long.MinValue`..`long.MaxValue`. Sequence ranges of agent gap records are clipped in `ulong`
(`[first_seq, min(last_seq, s − 1)]`, then intersected with stored gaps, whose numbers are at most 2^63 − 1) before any
narrowing to a `long` column.

**Write path.** `SqliteStore.WriteBatchAsync` keeps its one transaction. For a batch with an agent ID it creates an
`AgentTimeline` on the same connection and transaction:

1. `Load`: the agent's `agent_streams` row (none: a first batch).
2. `Prepare(batch, restartWait)`: walks the batch's agent records in ascending `seq` (a sorted copy):
   - no stream row: `floor_seq` = the lowest number, `max_seq` = `floor_seq − 1`;
   - `s > max_seq + 1`: a sequence gap `[max_seq + 1, s − 1]`, `sequence_missing`, `open`, `from_at` = `max_captured_at`,
     `to_at` = the record's capture time; `fill_at` = receive time;
   - `s > max_seq`: `max_seq` = `s`, `max_captured_at` = its capture time; a `self` metric `agent_send_lag_seconds` with a value of
     at most 60 is a candidate sample;
   - `floor_seq <= s < max_seq`: the gap containing `s` is found by one seek (below), loses `s` (shrunk, split, or `closed` when
     empty); a fill into an `open` gap sets `fill_at`;
   - an agent `gap` record with `first_seq`/`last_seq`: the clipped range's part inside open sequence gaps becomes `final` with
     the record's cause (found by seeks from the range's start, walking only the gaps that overlap it).
   Consecutive numbers of the batch are applied as runs: one seek per gap a run touches and each changed row written in place
   (no in-memory overlay). Then the final wait: if `restartWait`,
   `fill_at` = receive time; else if the agent's open sequence gap count is above 0, the batch filled none, and receive time −
   `fill_at` ≥ 5 minutes, one `UPDATE gaps SET state = 'final', changed_at = … WHERE agent_id = … AND kind = 'sequence' AND
   state = 'open'` finalizes them, its row count added to the `Final` count. A candidate is
   **counted** when, after this, no open sequence gap starts below its number. Then the offset (`ClockEstimator`).
3. `IsLive(record)`: `LiveRules.IsLive` with the receive time, the offset and the lowest open sequence gap's first number after
   the whole of step 2 (one seek), so a batch that closes the last gap below its records is live by lateness alone. `BatchWriter.Write` gets `live` and binds `records.live`; records of origin `import` or `backend` and
   batches without an agent ID get 0.
4. After the inserts: `WriteSampleTimes` inserts each distinct capture time of the batch's stored agent `metric` records into
   `agent_sample_times` (`INSERT OR IGNORE`, one row per sample, with the lowest and highest number at that time), and for each
   new time `t` in ascending order: a not-closed time gap containing `t` (one seek: greatest `from_at <= t`, then `to_at > t`) is
   narrowed or split, `closed` when no part is over 2 minutes; otherwise, with `p` the previous stored time (one seek), a gap
   `(p, t)` is created when `t − p` is over 2 minutes, and `(t, n)` with the next time `n` when `t` is the earliest. Creation is
   idempotent through the unique index. Raw capture times, cause `no_data`; `from_seq`/`to_seq` are the bounding samples' numbers.
5. `Finish()`: writes the stream row (with the open sequence gap count kept as a counter) and returns a `TimelineChange` or
   `null` when no gap changed and no ±90 s crossing happened. `Opened`: gaps newly detected (a jump's sequence gap, a new time
   gap span); `Final`: sequence gaps made final plus time gaps detected final; `Closed`: rows moved to `closed`; a row split
   off an existing gap counts in none; `Open`: open sequence gaps after the batch.

**Time gap state at read time (B2).** A time gap's stored state is `open` or `closed`; the reader reports a not-closed time gap
as `final` when no open sequence gap overlaps `(from_seq, to_seq)` — one seek (greatest open `first_seq < to_seq`, then
`last_seq > from_seq`; open sequence gaps never overlap). So no batch re-evaluates time gaps, and a time gap's `final` never
appears in `ingest gaps changed` except at creation, where the same seek decides it. Open time gaps never affect the
classification.

**Indexes (B2).** Partial indexes `gaps_seq (agent_id, first_seq) WHERE kind = 'sequence' AND state <> 'closed'`,
`gaps_seq_open (agent_id, first_seq) WHERE kind = 'sequence' AND state = 'open'`, unique `gaps_time (agent_id, from_at)
WHERE kind = 'time' AND state <> 'closed'`, and `gaps_agent (agent_id, id)` for the reader's pages. Every lookup is "greatest key <= x, `LIMIT 1`, then check the end", or the minimum;
the SQL lives in `AgentTimelineQueries` (constants), and `AgentTimelineQueriesUseIndexSearchesOnly` asserts by `EXPLAIN QUERY
PLAN` that each statement shows `SEARCH` and never `SCAN` on `gaps` and `agent_sample_times`. The work per stored record is a
constant number of seeks, whatever the number of open gaps.

**Live rule** (`LiveRules`, pure): lateness = `receivedAt − (capturedAt − offset)` in `Int128`; live when `|lateness| <=
allowance(kind)` (`kernel_event`, `log_line`: 10 minutes; others: 90 seconds) and no open sequence gap starts below the number.

**Offset** (`ClockEstimator`, pure; record 0130). A bound is `capturedAt − receivedAt` (a lower bound of agent clock minus
backend clock), saturated. Only run-timed records give bounds — every kind except `log_line` and `kernel_event` (M1).
- Bounds are kept as rows of `agent_clock_samples`, one per agent, sort (`counted` or `run`) and 10-second bucket of receive
  time holding the bucket's largest bound (upsert with `MAX`), at most 64 rows per agent and sort (the oldest dropped), so a
  5-minute window (30 buckets) is always held whole and an expiry falls back to the true window maximum (N3).
- From the first counted sample on: the largest `counted` bound in the buckets within 5 minutes before the newest `counted`
  bucket. No other
  record raises it, so a future-stamped record cannot, and the offset stays unchanged while open gaps stop samples from counting
  (AC2).
- Before it (E7): the largest `run` bound in the buckets within 5 minutes before the newest `run` bucket, and 0 when it is
  below 0 or there is none, taken from earlier batches only: a batch's own `run` bounds are added after its records are
  classified, so a record stamped `StorableTime.Max` cannot prove itself current (AC8), while a counted sample counts for its
  own batch (AC6). The live stream sends every 4 seconds beside the backfill, so a window always holds live batches
  while the agent sends. A forward step before the first counted sample
  therefore lasts at most 5 minutes (M2); the migration seeds nothing (null), so nothing old survives the upgrade.
`measured_at` = the newest counted sample's receive time. Residual (N4): the offset can be short by the delivery delay of
the freshest counted sample, up to the agent's 30-second request timeout. Crossing |offset| = 90 s sets or clears `agent_streams.clock_off`.

**Restart set (N1).** `SqliteStore` adds an agent ID to its in-memory set of agents written since open only after the batch's
commit; a refused or rolled-back first batch leaves the reset for the next one.

**Schema step 12** (`SchemaMigrator`): `records.live` (`INTEGER NOT NULL DEFAULT 0 CHECK (live IN (0, 1))`); `agent_streams`
(`agent_id` primary key, `floor_seq`, `max_seq`, `max_captured_at`, `fill_at`, `open_gaps`,
`offset_ns`, `measured_at`, `clock_off`), seeded from existing agent records (`floor_seq` = `max_seq` = the highest stored number,
its capture and receive time, `open_gaps` 0, offsets null); `agent_clock_samples` (`agent_id`, `sort` `counted`/`run`, `bucket` (receive
time floored to 10 s), `bound_ns`, primary key `(agent_id, sort, bucket)`, without row ID, empty after the migration); `agent_sample_times` (`agent_id`, `captured_at`, `min_seq`, `max_seq`, primary key `(agent_id,
captured_at)`, without row ID, empty after the migration); `gaps` (`id` `AUTOINCREMENT`, `agent_id`, `kind`, `cause`, `state`,
`first_seq`, `last_seq`, `from_seq`, `to_seq`, `from_at`, `to_at`, `detected_at`, `changed_at`, `CHECK`s on kind, state, and
`first_seq <= last_seq`) with the indexes above.

**Readers.** `LogColumns` and `NoLog` end with `, r.live` (column 23), `Read` sets `StoredRecord.Live`. `IAgentTimelineReader`
(`GapsAsync`, `ClockAsync`) on reader connections, bound parameters, a page of 1 to 1,000 gaps by ID.

**Logs** (`IngestEndpoint.Acknowledge`, `BackendLog`): after `ingest batch stored`, when `result.Timeline` is set: info `ingest
gaps changed {AgentId} {Opened} {Closed} {Final} {Open}` (EventId 66) when a gap changed; warning `agent clock off {AgentId}
{OffsetSeconds}` (67) when the state turned off, info `agent clock in step {AgentId}` (68) when it turned back. Layout fixed here.

**Benchmark (minor 2).** Besides its current run, the Dev adds a scenario to `tools/Vandox.StorageBenchmark`: one 10,000-record
backfill batch filling an open sequence gap and its time gap, measured before and after.

## Options considered

The choices of record 0130 (write time versus read time, gap table versus gap records, the 5-minute final wait, the offset
from counted samples only versus all records, a minimum-lateness window or the header field). Besides:

- **Time gaps from an index on `records(agent_id, captured_at)`** — an index entry per metric record costs the throughput
  criterion; `agent_sample_times` adds one row per sample.
- **Time gap states kept by every batch** — per changed sequence gap a walk over the time gaps in its number range, which a
  hostile agent makes quadratic (B2); the read-time check is one seek per gap read.
- **A send-lag bound of 10 s** — on an agent without log traffic the lag is about 30 s, so no sample would ever count (M2);
  60 s admits one `system` interval plus the send period and timeout.
- **Extending `WriteResult` positionally** — would break the whole-value assertions; an init-only property defaulting to
  `null` keeps those of batches that change no gap.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Vandox.Storage | `LiveRules.cs`, `ClockEstimator.cs`, `ClockSample.cs` (new) | the live rule, the allowances, the offset estimate |
| Vandox.Storage | `AgentTimeline.cs`, `AgentTimelineQueries.cs` (new) | gap upkeep, counted samples, final wait, sample times, stream row; its SQL |
| Vandox.Storage | `TimelineChange.cs`, `AgentGap.cs`, `AgentClock.cs`, `GapQuery.cs`, `GapKind.cs`, `GapState.cs`, `TimelineLimits.cs`, `IAgentTimelineReader.cs` (new) | result, read model, constants, reader |
| Vandox.Storage | `BatchWriter.cs`, `SqliteStore.cs`, `SchemaMigrator.cs`, `StorageLimits.cs`, `RecordQueries.cs`, `StoredRecord.cs`, `WriteResult.cs` | `live`, the timeline, the restart set, step 12, readers |
| Vandox.Backend | `Ingest/IngestEndpoint.cs`, `Hosting/BackendLog.cs` | the three log lines |
| tools | `Vandox.StorageBenchmark/Program.cs` | the backfill scenario |

## Signatures (for the Dev's skeleton)

```csharp
// Vandox.Storage
public static class TimelineLimits
{
    public static readonly TimeSpan RunAllowance;        // 90 s
    public static readonly TimeSpan LogAllowance;        // 10 min
    public static readonly TimeSpan TimeGapSpan;         // 2 min
    public static readonly TimeSpan FinalWait;           // 5 min
    public static readonly TimeSpan SampleWindow;        // 5 min
    public const double MaxCountedSendLagSeconds = 60;
    public static readonly TimeSpan SampleBucket;        // 10 s
    public const int MaxClockSamples = 64;            // rows per agent and sort
}
public static class GapKind { public const string Sequence = "sequence"; public const string Time = "time"; }
public static class GapState { public const string Open = "open"; public const string Closed = "closed"; public const string Final = "final"; }
public static class LiveRules
{
    public static TimeSpan AllowanceOf(string kind);
    public static bool IsRunTimed(string kind);
    public static bool IsLive(DataRecord record, DateTimeOffset receivedAt, long offsetNs, ulong? lowestOpenGapFirstSeq);
}
public readonly record struct ClockSample(DateTimeOffset Bucket, long BoundNs); // the bucket's largest bound
public static class ClockEstimator
{
    public static long BoundOf(DateTimeOffset capturedAt, DateTimeOffset receivedAt); // Int128 inside, saturated
    public static long Estimate(IReadOnlyList<ClockSample> countedBuckets, IReadOnlyList<ClockSample> runBuckets);
    public static DateTimeOffset BucketOf(DateTimeOffset receivedAt);
}
public sealed class TimelineChange
{
    public int Opened { get; init; } public int Closed { get; init; } public int Final { get; init; } public int Open { get; init; }
    public long? OffsetNs { get; init; } public bool? ClockOff { get; init; } // null: no crossing in this batch
}
public sealed class AgentGap
{
    public long Id { get; set; } public string AgentId { get; set; } public string Kind { get; set; } public string Cause { get; set; }
    public string State { get; set; } public ulong? FirstSeq { get; set; } public ulong? LastSeq { get; set; }
    public DateTimeOffset From { get; set; } public DateTimeOffset To { get; set; }
    public DateTimeOffset DetectedAt { get; set; } public DateTimeOffset ChangedAt { get; set; }
}
public sealed class AgentClock { public string AgentId { get; set; } public long? OffsetNs { get; set; } public DateTimeOffset? MeasuredAt { get; set; } }
public sealed class GapQuery { public string AgentId { get; set; } public string State { get; set; } /* "" = any */ public long AfterId { get; set; } public int Limit { get; set; } }
public interface IAgentTimelineReader
{
    Task<IReadOnlyList<AgentGap>> GapsAsync(GapQuery query, CancellationToken cancellationToken);
    Task<AgentClock?> ClockAsync(string agentId, CancellationToken cancellationToken);
}
// changed
public readonly record struct WriteResult(int Stored, int Duplicates) { public TimelineChange? Timeline { get; init; } }
public sealed class StoredRecord { /* … */ public bool Live { get; set; } }
public sealed class SqliteStore : …, IAgentTimelineReader
internal bool BatchWriter.Write(RecordBatch batch, DataRecord record, bool live, CancellationToken cancellationToken);
internal static class AgentTimelineQueries { /* SQL constants of every gaps, agent_streams, agent_clock_samples and agent_sample_times statement */ }
internal sealed class AgentTimeline : IDisposable
{
    internal static AgentTimeline Load(SqliteConnection connection, SqliteTransaction transaction, string agentId);
    internal void Prepare(RecordBatch batch, bool restartWait, CancellationToken cancellationToken);
    internal bool IsLive(DataRecord record);
    internal void WriteSampleTimes(RecordBatch batch, IReadOnlyList<DataRecord> stored, CancellationToken cancellationToken);
    internal TimelineChange? Finish(CancellationToken cancellationToken);
}
// Vandox.Backend, Hosting/BackendLog.cs
internal static partial void IngestGapsChanged(this ILogger logger, string agentId, int opened, int closed, int final, int open);
internal static partial void AgentClockOff(this ILogger logger, string agentId, double offsetSeconds);
internal static partial void AgentClockInStep(this ILogger logger, string agentId);
```

`GapQuery` is validated (`InvalidQueryException`): agent ID by the wire rule, `State` empty or one of the three, `AfterId >= 0`,
`Limit` 1 to 1,000. Existing files the skeleton rewrites: `WriteResult.cs`, `StoredRecord.cs`, `BatchWriter.cs` (signature),
`StorageLimits.cs`; `SqliteStore.cs` gets the interface with throwing bodies.

## Test files

- `tests/Vandox.Storage.Tests/LiveRulesTests.cs`, `ClockEstimatorTests.cs`, `AgentTimelineQueriesTests.cs`,
  `SqliteStoreTimelineTests.cs`, and the helper `AgentReplay.cs`: an agent's records on the collector grid (`system` metrics every
  30 s, `self` every 60 s, optional log lines), numbers and boot IDs, receive times from the live stream (every 4 s) and the
  backfill (oldest first), and each `self` sample's `agent_send_lag_seconds` derived as the sender does (sample time minus the
  newest acknowledged capture time, before the first acknowledgement minus the start), never chosen by the test (M2).
  `SqliteStoreOpenTests.cs` gets the migration test.
- `tests/Vandox.Backend.Tests/IngestEndpointTimelineTests.cs` (real store through `IngestRig`, logs through `IngestLogCapture`).
- Existing call sites: `SqliteStoreWriteTests.cs:226` calls `BatchWriter.Write` and is adapted by the Dev in step 4 (the new
  `live` argument, mechanically); `SqliteStoreOpenTests.SqliteStoreSchemaVersionIsEleven` becomes
  `SqliteStoreSchemaVersionIsTwelve` (Tester). The 15 whole-value `WriteResult` assertions (fact 16) stay unless their batch changes a gap
  (capture times more than 2 minutes apart, jumping numbers); the Tester checks every one in step 6 and adapts one that fails to
  compare `Stored` and `Duplicates`. `IngestEndpointTests` log expectations: the same check, Tester.
- Files no unit test can cover: none.

## Areas

- Detection, alerts and reports: *Live data and backfill* rewritten from "Planned" (rule, allowances, gap kinds and states,
  time gap `final` evaluated when read, final wait, offset from counted samples and before them, residuals E5, E15, record 0130).
- Storage: step 12 and the tables, *Writing* (the timeline in the batch transaction; rows it updates; at most three gap rows
  per record; indexed seeks only), *Reading* (`live`, gaps, clock), the open limit for #46 (retention deletes sample times, gaps
  and stream rows with their data).
- Backend host: *Ingest endpoint* log table row 13. Wire format: consumer duties. Agent: *Gaps*, *Write failures*.

## Documentation updates

All by the Dev once the code is green:

- `docs/areas/detection-and-reports.md`, `storage.md`, `backend-host.md`, `wire-format.md`, `agent.md` as under *Areas*; the
  sweep found: Storage *Schema* table and version sentence, Storage index list, Backend host log table row 13, Detection
  *Scope* ("the live/backfill classification ... not implemented yet").
- `docs/ARCHITECTURE.md`, *Offline behavior and backfill*: measured offset, gap states, final wait, link to 0130.
- `.squad/project.md`, security area 1: `BatchWriter`'s transaction also writes `gaps`, `agent_streams`, `agent_clock_samples`
  and `agent_sample_times` rows, never updates a `records` row, adds at most three gap rows per stored record with indexed seeks
  only; record 0130; area 12: the three log lines carry only the agent ID, counts and the offset; *Guarantees*, backfill:
  implemented by `LiveRules` and `AgentTimeline`.
- `docs/BENCHMARKS.md`, *Storage write throughput*: the before/after figures of fact 20 with the backfill scenario.

## Architecture check

*Backfilled data never raises an alert by itself* is implemented, not weakened: live only by spec AC1, decided once at write
time, every undecidable case backfilled. *No data gaps unless explicitly recorded* gains the backend's sequence and time gaps.
Deduplication and the immutability of `records` rows stay. No guarantee needs the Product Manager.

## Security considerations

- [x] No limits on parsed input: the classification reads decoded, validated records; time differences in `Int128`, stored
  bounds saturated, gap ranges clipped in `ulong`, so no record can throw on this path (B1). A stored record adds at most three
  gap rows (two unless it is an agent `gap` record: a jump plus a three-way split of a finalized range) (B3); each costs a
  constant number of indexed seeks (B2); per agent one stream row and at most 64 clock samples.
- [x] Exceptions: a SQLite failure in the timeline is caught by `WriteBatchAsync` as `store: writing batch: <message>` and
  answered `503` without text (unchanged); no `OverflowException` path exists; `GapQuery` violations are `store: invalid query:
  <rule>`.
- The classification is no trust boundary: an agent holding the token chooses its capture times and numbers.

## Decision records

- `docs/decisions/0130-live-or-backfilled-at-write-time-gaps-as-stateful-objects-offset-from-caught-up-self-samples.md`
  (Proposed; amends 0022), revised with this plan.

## Challenge

- M1 (log-line bounds inflate the offset): accepted. Bounds come only from run-timed kinds, after the first counted sample
  only from counted samples; E4 is tested through the store (`SqliteStoreTimelineClockSteppedForwardFreshRecordsBackfilled`),
  and AC2's unchanged offset holds because no sample counts while a gap is open. Record 0130 corrected.
- M2 (pre-sample maximum kept forever; samples never counted): accepted. Before the first counted sample the bound is the
  5-minute maximum of run-timed records, floored at 0, and seeded empty by the migration; spec E7 restated. The send-lag bound
  is 60 s (spec Terms and constants); `AgentReplay` derives the lag as the sender does, and AC6 adds an agent without log
  traffic (`SqliteStoreTimelineQuietAgentSamplesAreCounted`).
- Minor 1 (existing test claims): accepted; facts 16 and *Test files* corrected (15 assertions, the direct `Write` call).
- Minor 2 (benchmark misses the costly path; write once per batch): accepted; the backfill scenario, and runs of
  consecutive numbers written once per changed row.

## Security review

- B1 (overflow wedges ingest): accepted. `Int128` differences, saturated bounds, `ulong` clipping; AC8 cases at
  `StorableTime.Min`/`Max` (stored, `204`, backfilled) and a gap range above 2^63 − 1; spec E12 and AC8 extended.
- B2 (quadratic re-evaluation): accepted. Time gap `final` is evaluated when read; every lookup is an indexed seek in
  `AgentTimelineQueries`, pinned by `EXPLAIN QUERY PLAN`; the final wait is one counted `UPDATE`; open sequence gaps are counted
  in `agent_streams`, never loaded.
- B3 (one gap per record is false): accepted. The bound is three rows per record (two unless an agent `gap` record) in spec E12,
  AC8 and the NFR, here and in the `.squad/project.md` text; the AC8 test jumps metrics in number and time and asserts the row
  count.
- N1 (restart set before commit): accepted; filled after the commit, with a test.
- N2 (schema fact): accepted; the row is gone, the migration test covers it.
- N3 (one stored value cannot hold a sliding-window maximum; a backfill batch after an expiry lowers the pre-sample estimate):
  accepted, option taken: bounded bucket rows for both sorts (*Offset*), with a pure and a store test where a backfill batch is
  the first after an expiry.
- N4 (residual delay is up to the request timeout, not seconds): accepted; record 0130 and *Offset* say "up to the 30-second
  request timeout".

## Out of scope / follow-ups

As spec *Out of scope*. No follow-up issue: retention of the new rows is noted for #46 in the Storage area.
