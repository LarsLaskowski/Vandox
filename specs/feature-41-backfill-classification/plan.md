# Plan: Recognize backfilled data from the data itself

Source: Issue #41 | [spec.md](spec.md)
Status: Draft
Tier: standard — a behavior change in the ingest write path (classification, gap and offset bookkeeping) that creates, changes or removes no security control: deduplication, token, limits and the drain stay as they are.
Security areas touched: 1 Ingest authentication (`BatchWriter` and the `SqliteStore` write transaction it shares with the deduplication, `IngestEndpoint.Acknowledge` log lines; the statement "the only rows it ever updates are `listener_comparisons` rows" changes), 12 Logging and display of external data (new `vandoxd` log lines carrying the agent ID, counts and an offset)

## Problem / root cause

See [spec.md](spec.md), *Problem*. Today `IngestEndpoint` passes no classification to the store and logs the header's `mode`
only (`src/Vandox.Backend/Ingest/IngestEndpoint.cs:835`); `BatchWriter.Write` (`src/Vandox.Storage/BatchWriter.cs:91`) inserts
records without one, and nothing detects gaps or measures an offset.

## Verified facts

| # | Claim | Evidence | Status |
| - | ----- | -------- | ------ |
| 1 | The header's `mode` never reaches the store: `RecordBatch` has no mode, the endpoint only logs it | `RecordBatch.cs` (properties AgentId, BootId, ClockOffsetNs, ReceivedAt, Records, Import); `IngestEndpoint.cs:314-321`, `:835` | confirmed |
| 2 | All records of a batch share one receive time, taken when the request arrives | `IngestEndpoint.cs:351` (`_clock.GetUtcNow()`), `:319`; Storage area, *Database file* | confirmed |
| 3 | The decoder refuses a batch whose `seq` does not strictly increase | Wire format area, *Records* (`seq` row) and `ErrSequence` (line 495) | confirmed |
| 4 | A `records` row is never updated; duplicates are `ON CONFLICT DO NOTHING` | `BatchWriter.cs:46-49`; Storage area, *Writing* | confirmed |
| 5 | Every batch write runs in one writer transaction under `_writeLock`, so two ingest slots commit one after the other | `SqliteStore.cs:460-493` | confirmed |
| 6 | The agent sends no clock offset | Agent area, *What is kept* ("No part of the agent measures an offset yet"); `internal/spool/read.go:228` only copies a spooled value; no writer of `ClockOffsetNS` in `internal/` | confirmed |
| 7 | `agent_send_lag_seconds` is measured on the agent's clock at capture; before the first acknowledgement it counts from the start | Agent area, *Send lag* (line 1785); `internal/selfstats/selfstats.go:137-144` | confirmed |
| 8 | The `self` collector runs every 60 s; `system` every 30 s (10 s under pressure), so `metric` records come at least every 60 s | Agent area, *Self-metrics*, *System metrics* table | confirmed |
| 9 | The live stream starts after the newest record (or at the oldest unacknowledged with 32 acknowledged ranges); the backfill sends oldest first and never moves past an unacknowledged record | Agent area, *Sending* (lines 1744-1747, 1769-1771) | confirmed |
| 10 | The numbers continue without a jump after a clean stop or a `kill -9` in the same boot; an unclean stop in an earlier boot jumps to the clock bound | Agent area, *Sequence numbers* (line 1596-1603) | confirmed |
| 11 | Agent gap records naming numbers (`spool_dropped`, a given-up record's `unknown`) carry numbers below their own | Agent area, *Size bound* (gaps written before the hand-over's records), *Damaged data*, *Refused batches* (gap handed to the spool after the refused record) | confirmed |
| 12 | `RecordQueries.Read` reads columns by position and every record select is built by `SelectColumns` | `RecordQueries.cs:17-21`, `:171-189`, `:219-227`; callers in `LogExplorerQueries`, `LogFinderQueries`, `LogFinder` | confirmed |
| 13 | Eight existing assertions compare a whole `WriteResult` | `SqliteStoreWriteTests.cs:47,91,153,190,250,346`, `SqliteStoreTemplateRebuildTests.cs:495`, `SqliteStoreLogExplorerTests.cs:2274`, `SqliteStoreListenerHistoryTests.cs:344` | confirmed |
| 14 | Schema version is 11; a new step is 12 | `StorageLimits.cs:18`; Storage area, *Schema* | confirmed |
| 15 | Records 0022 and 0046 are released (in tag `v0.1.0`), so 0022 is amended by a new record, not edited | `git ls-tree v0.1.0 docs/decisions/` lists both | confirmed |
| 16 | SQLite accepts `ALTER TABLE ... ADD COLUMN live INTEGER NOT NULL DEFAULT 0 CHECK (live IN (0, 1))` on a `STRICT` table | to verify: the Dev runs the migration test of AC9 in step 6 | unverified |
| 17 | The added work keeps the write-throughput criterion (median of five 10,000-record batches under 1 s on the reference host) | to verify: the Dev runs `tools/Vandox.StorageBenchmark` before and after on the development host in step 6 and records both in `docs/BENCHMARKS.md`; the reference host stays with #44 | unverified |

## Acceptance criteria

The criteria are those of [spec.md](spec.md), AC1 to AC10.

## Tests per criterion

`T` = `tests/Vandox.Storage.Tests/SqliteStoreTimelineTests.cs` (class `SqliteStoreTimelineTests`), which drives a real
`SqliteStore` through `AgentReplay` (below).

| AC | Test(s) | Test file |
| -- | ------- | --------- |
| AC1 | `LiveRulesMetricWithinNinetySecondsIsLive`, `LiveRulesMetricNinetyOneSecondsLateIsBackfilled`, `LiveRulesLogLineNineMinutesLateIsLive`, `LiveRulesKernelEventElevenMinutesLateIsBackfilled`, `LiveRulesEarlyBeyondAllowanceIsBackfilled`, `LiveRulesOpenGapBelowIsBackfilled`; `IngestEndpointTimelineBackfillModeFreshRecordsStoredLive`, `IngestEndpointTimelineLiveModeOldRecordsStoredBackfilled` | `tests/Vandox.Storage.Tests/LiveRulesTests.cs`; `tests/Vandox.Backend.Tests/IngestEndpointTimelineTests.cs` |
| AC2 | `SqliteStoreTimelineThirteenHourOutageNeverStoresOutageRecordLive` (asserts: one sequence and one time gap opened; no record captured 22:00-09:00 live; every current record before the closing batch backfilled; offset unchanged until both close; both `closed` with the last number; the next batch live) | T |
| AC3 | `SqliteStoreTimelineSameBootRestartDuringBackfillKeepsFirstClassification`, `SqliteStoreTimelineRebootJumpBecomesFinalAfterFiveMinutes`, `SqliteStoreTimelineWaitRestartsAfterReopen` | T |
| AC4 | `SqliteStoreTimelineAgentSpoolDroppedGapFinalizesNamedNumbers`, `SqliteStoreTimelineAgentGapAtOrAboveOwnNumberFinalizesNothing` | T |
| AC5 | `SqliteStoreTimelineCleanStopHourIsFinalTimeGap`, `SqliteStoreTimelineTimeGapOpenWhileSequenceGapBetween`, `SqliteStoreTimelineTimeGapBelowFirstNumberDetected`, `SqliteStoreTimelineTimeGapBesideAgentGapStored`, `SqliteStoreTimelineTimeGapIgnoresOffsetChange` | T |
| AC6 | `ClockEstimatorBehindTwoHoursMeasuredFromCountedSample`, `ClockEstimatorAheadTwoHoursMeasuredBeforeCountedSample`, `ClockEstimatorLateSampleNeverLowersEstimate`, `ClockEstimatorBackwardStepRecoversWithinFiveMinutes`, `ClockEstimatorWithoutCountedSampleNeverBelowZero`; `SqliteStoreTimelineClockAheadNeverMakesOldRecordLive`, `SqliteStoreTimelineClockBehindLiveFromCountedSample` | `tests/Vandox.Storage.Tests/ClockEstimatorTests.cs`; T |
| AC7 | `SqliteStoreTimelineNoSequenceGapBelowFirstNumber`, `SqliteStoreTimelineMiddleFillSplitsGap`, `SqliteStoreTimelineFinalGapShrinksAndCloses`, `SqliteStoreTimelineGapStatesSurviveReopen` | T |
| AC8 | `SqliteStoreTimelineRefusedBatchChangesNothing`, `SqliteStoreTimelineConcurrentBatchesMatchCommitOrder`, `SqliteStoreTimelineJumpBetweenEveryRecordOpensOneGapPerRecord`, `SqliteStoreTimelineNumbersNearMaximumWork` | T |
| AC9 | `SqliteStoreTimelineImportRecordsReadBackfilled`, `SqliteStoreOpenMigratesAgentRecordsAsBackfilledWithoutGaps` | T; `tests/Vandox.Storage.Tests/SqliteStoreOpenTests.cs` |
| AC10 | `SqliteStoreTimelineRecordsCarryClassification`, `SqliteStoreTimelineGapsAndClockReadable`; `IngestEndpointTimelineGapsChangedLoggedOncePerBatch`, `IngestEndpointTimelineClockOffLoggedOnceThenInStep` | T; `tests/Vandox.Backend.Tests/IngestEndpointTimelineTests.cs` |

## Approach

**Write path.** `SqliteStore.WriteBatchAsync` keeps its one transaction. For a batch with an agent ID it creates an
`AgentTimeline` on the same connection and transaction, in this order:

1. `Load`: the agent's row of `agent_streams` (none: a first batch).
2. `Prepare(batch, restartWait)`: walks the batch's agent records in ascending `seq` (a copy sorted, so a storage caller's order
   does not matter) and keeps the sequence gaps:
   - no stream row: `floor_seq` = the batch's lowest number, `max_seq` = `floor_seq − 1`;
   - `s > max_seq + 1`: a sequence gap `[max_seq + 1, s − 1]`, cause `sequence_missing`, state `open`, `from_at` = `max_captured_at`,
     `to_at` = the record's capture time, `detected_at` = `changed_at` = receive time; `fill_at` = receive time;
   - `s > max_seq`: `max_seq` = `s`, `max_captured_at` = its capture time; when the record is a `self` metric
     `agent_send_lag_seconds` with a value of at most 10, it is a candidate sample;
   - `floor_seq <= s < max_seq`: the open or final sequence gap containing `s` (index lookup) loses `s`: shrunk at an edge, split in
     the middle (the row keeps the lower part, a new row of the same kind, cause, state and `detected_at` the upper part),
     `closed` when empty; `to_at`/`from_at` of the parts are the record's capture time; a fill into an `open` gap sets `fill_at`;
   - `s < floor_seq` or `s` already stored: nothing;
   - an agent `gap` record with `first_seq`/`last_seq`: the part of `[first_seq, min(last_seq, s − 1)]` that lies in open sequence
     gaps becomes `final` with the record's cause (split as above).
   Then the final wait: if `restartWait`, `fill_at` = receive time; else if an open sequence gap exists, this batch filled none,
   and receive time − `fill_at` ≥ 5 minutes, every open sequence gap becomes `final` (`changed_at` = receive time). A candidate
   sample is **counted** when, after this, no open sequence gap starts below its number. Then the offset (`ClockEstimator`).
3. `IsLive(record)`: `LiveRules.IsLive` with the batch's receive time, the offset and the lowest open sequence gap's first number
   after step 2 (so a batch that closes or finalizes the last gap may be live, S2, S5). `BatchWriter.Write` takes the result as
   a new argument and binds it to `records.live`. Records of origin `import` or `backend`, and every record of a batch without an
   agent ID, are written with `live` 0.
4. After the inserts (templates and listener comparisons as today): `WriteSampleTimes` inserts each distinct capture time of the
   batch's stored agent `metric` records into `agent_sample_times` (`INSERT OR IGNORE`, one row per sample, not per metric) with the
   lowest and highest number seen at that time, and keeps the time gaps for each new time `t` in ascending order: a time gap
   (open or final) whose span contains `t` is narrowed or split like a sequence gap, `closed` when no part is longer than 2
   minutes; otherwise, with `p` the previous stored time, a time gap `(p, t)` is created when `t − p` is over 2 minutes and,
   when `t` is the earliest time, `(t, n)` with the next time `n`; creation is idempotent (unique on agent and `from_at` for gaps
   that are not closed). Raw capture times, cause `no_data`.
5. `Finish()`: a new or narrowed time gap, and every open time gap whose numbers `(from_seq, to_seq)` overlap a sequence gap that
   changed in this batch, is `final` when no open sequence gap overlaps those numbers, else `open`. The stream row is written
   (`INSERT ... ON CONFLICT DO UPDATE`). It returns a `TimelineChange` (gaps opened, closed and made final in this batch, open gaps
   after it, the offset, and whether the clock state crossed ±90 s), or `null` when nothing of that changed.

`WriteResult` gets an init-only property `Timeline` (`TimelineChange?`, default `null`), so the eight whole-value assertions
(fact 13) keep holding for batches that change no gap. `restartWait` comes from an in-memory set of agent IDs in `SqliteStore`
(added on the agent's first batch after the store was opened; one entry per agent that writes).

**Live rule** (`LiveRules`, pure): lateness = `receivedAt − (capturedAt − offset)`; live when `|lateness| <= allowance(kind)`
(`kernel_event`, `log_line`: 10 minutes; any other kind: 90 seconds) and no open sequence gap starts below the record's number.
All arithmetic in `Int128` or checked nanoseconds, so 2^63 − 1 numbers and the storable time range cannot overflow.

**Offset** (`ClockEstimator`, pure; record 0130): a record's bound is `capturedAt − receivedAt` (a lower bound of agent clock
minus backend clock). Before the agent's first counted sample: the largest bound of all its records so far, never below 0
(`agent_streams.causal_max_ns`). From then on: the largest bound among the counted samples received within 5 minutes before the
newest counted sample (`agent_clock_samples`, one row per batch with the batch's largest counted bound, at most 64 rows kept per
agent) and among the current batch's records. `measured_at` = the newest counted sample's receive time. A crossing of
|offset| = 90 s sets or clears `agent_streams.clock_off`.

**Schema step 12** (`SchemaMigrator`): `records.live` (`INTEGER NOT NULL DEFAULT 0 CHECK (live IN (0, 1))`); `agent_streams`
(`agent_id` primary key, `floor_seq`, `max_seq`, `max_captured_at`, `fill_at`, `causal_max_ns`, `offset_ns` nullable, `measured_at`
nullable, `clock_off` 0/1), seeded from existing agent records with `floor_seq` = `max_seq` = the highest stored number, its
capture time, `fill_at` = its receive time and `causal_max_ns` = max(0, largest bound); `agent_clock_samples` (`agent_id`,
`received_at`, `bound_ns`, primary key both, without row ID); `agent_sample_times` (`agent_id`, `captured_at`, `min_seq`,
`max_seq`, primary key `(agent_id, captured_at)`, without row ID; empty after the migration, so no time gap among old data);
`gaps` (`id` `AUTOINCREMENT`, `agent_id`, `kind` `sequence`/`time`, `cause`, `state` `open`/`closed`/`final`, `first_seq` and
`last_seq` (sequence), `from_seq` and `to_seq` (time), `from_at`, `to_at`, `detected_at`, `changed_at`, `CHECK`s on kind, state and
`first_seq <= last_seq`), indexes `gaps(agent_id, kind, state, first_seq)` and a unique `gaps(agent_id, from_at)` where
`kind = 'time' AND state <> 'closed'`. Existing records read `live` 0 by the default.

**Readers.** `RecordQueries`: `RecordColumns` stays, `LogColumns` and `NoLog` end with `, r.live` (column 23), `Read` sets
`StoredRecord.Live`. New `IAgentTimelineReader` on `SqliteStore` (`GapsAsync`, `ClockAsync`), reader connections only, bound
parameters, a page of 1 to 1,000 gaps by ID.

**Logs** (`IngestEndpoint.Acknowledge`, `BackendLog`): after `ingest batch stored`, when `result.Timeline` is set: info `ingest
gaps changed {AgentId} {Opened} {Closed} {Final} {Open}` (EventId 66) when a gap changed; warning `agent clock off {AgentId}
{OffsetSeconds}` (67) when the state turned off, info `agent clock in step {AgentId}` (68) when it turned back. Layout of these
lines fixed here; nothing else user-visible.

## Options considered

The four choices of record 0130 (write time versus read time, gap table versus gap records, the 5-minute final wait versus the
spool-age metric or never, and the tightest-bound offset versus a minimum-lateness window or the header field). Besides:

- **Time gaps from an index on `records(agent_id, captured_at)` for metrics** — one more index entry per metric record (most of the
  stream) costs the write-throughput criterion; `agent_sample_times` adds one row per sample instead.
- **Extending `WriteResult` positionally** — would change the eight whole-value assertions; an init-only property defaulting to
  `null` keeps them.

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| Vandox.Storage | `LiveRules.cs` (new) | the live rule and the allowances |
| Vandox.Storage | `ClockEstimator.cs`, `ClockSample.cs` (new) | the offset estimate |
| Vandox.Storage | `AgentTimeline.cs` (new) | gap upkeep, counted samples, final wait, sample times, stream row |
| Vandox.Storage | `TimelineChange.cs`, `AgentGap.cs`, `AgentClock.cs`, `GapQuery.cs`, `GapKind.cs`, `GapState.cs`, `TimelineLimits.cs`, `IAgentTimelineReader.cs` (new) | result, read model, constants, reader interface |
| Vandox.Storage | `BatchWriter.cs` | `Write` takes `live`, binds `records.live` |
| Vandox.Storage | `SqliteStore.cs` | timeline in `WriteBatchAsync`, the restart set, `IAgentTimelineReader` |
| Vandox.Storage | `SchemaMigrator.cs`, `StorageLimits.cs` | step 12, version 12 |
| Vandox.Storage | `RecordQueries.cs`, `StoredRecord.cs`, `WriteResult.cs` | `live` column and property, `Timeline` |
| Vandox.Backend | `Ingest/IngestEndpoint.cs`, `Hosting/BackendLog.cs` | the three log lines |

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
    public const double MaxCountedSendLagSeconds = 10;
    public const int MaxClockSamples = 64;
}
public static class GapKind { public const string Sequence = "sequence"; public const string Time = "time"; }
public static class GapState { public const string Open = "open"; public const string Closed = "closed"; public const string Final = "final"; }
public static class LiveRules
{
    public static TimeSpan AllowanceOf(string kind);
    public static bool IsLive(DataRecord record, DateTimeOffset receivedAt, long offsetNs, ulong? lowestOpenGapFirstSeq);
}
public readonly record struct ClockSample(DateTimeOffset ReceivedAt, long BoundNs);
public static class ClockEstimator
{
    public static long BoundOf(DateTimeOffset capturedAt, DateTimeOffset receivedAt);
    public static long Estimate(IReadOnlyList<ClockSample> countedSamples, long causalMaxNs, long batchMaxBoundNs);
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

- `tests/Vandox.Storage.Tests/LiveRulesTests.cs`, `ClockEstimatorTests.cs`, `SqliteStoreTimelineTests.cs`, and the helper
  `AgentReplay.cs` (builds an agent's records and batches: `system` metrics every 30 s, `self` samples every 60 s with a send lag,
  numbers, boot IDs, receive times, the live stream first and the backfill oldest first, as the Agent area's *Sending* describes);
  `SqliteStoreOpenTests.cs` gets the migration test.
- `tests/Vandox.Backend.Tests/IngestEndpointTimelineTests.cs` (real store through `IngestRig`, log capture through
  `IngestLogCapture`).
- Existing call sites: `BatchWriter.Write` has no test caller (only `SqliteStore`); the eight `WriteResult` assertions (fact 13)
  stay unchanged unless a batch there opens a gap — the Tester checks them in step 6 and adapts one that fails to compare
  `Stored` and `Duplicates`. `IngestEndpointTests` expectations of the log lines of a stored batch: unchanged unless their
  batches jump numbers (Tester).
- Files no unit test can cover: none.

## Areas

- Detection, alerts and reports: *Live data and backfill* rewritten from "Planned" to the rule, allowances, gap kinds and
  states, the final wait, the offset and its residuals (spec E5, E15, record 0130).
- Storage: schema step 12 and the new tables, *Writing* (the timeline in the batch transaction; rows it updates), *Reading*
  (`live`, gaps, clock), the open limit for #46 (retention deletes sample times, gaps and stream rows with their data).
- Backend host: *Ingest endpoint* log table (row 13: the three new lines).
- Wire format: consumer duties (gap detection and classification by the backend; `mode` and `clock_offset_ns` not used for it).
- Agent: *Gaps* (the backend's gaps linked); *Write failures* ("the backend's time-gap detection (#41)" now real).

## Documentation updates

All by the Dev once the code is green:

- `docs/areas/detection-and-reports.md`, `storage.md`, `backend-host.md`, `wire-format.md`, `agent.md` as under *Areas*; the
  sweep found these enumerations: Storage *Schema* table and version sentence, Storage index list, Backend host ingest log
  table row 13, Detection *Scope* ("the live/backfill classification ... not implemented yet").
- `docs/ARCHITECTURE.md`, *Offline behavior and backfill*: the measured offset, gap states and the final wait, linking 0130;
  the storage schema list of *Storage and retention* if it enumerates tables.
- `.squad/project.md`, security area 1: `BatchWriter`'s transaction now also updates `gaps`, `agent_streams`,
  `agent_clock_samples` and `agent_sample_times` rows, never a `records` row; add 0130 to its records; area 12: the three log
  lines carry only the agent ID, counts and the offset; *Guarantees*, backfill: implemented by `LiveRules` and `AgentTimeline`.
- `docs/BENCHMARKS.md`, *Storage write throughput*: the before/after figures of fact 17.

## Architecture check

The guarantee *Backfilled data never raises an alert by itself* is implemented, not weakened: a record is live only on the
rule of spec AC1, decided once at write time and never changed, with every undecidable case backfilled. *No data gaps unless
explicitly recorded* gains the backend's sequence and time gaps. Deduplication and the immutability of `records` rows stay.
No guarantee needs the Product Manager.

## Security considerations

- [x] No limits on parsed input: the classification reads decoded, validated records only; numbers and times are bounded by
  the decoder and handled without overflow (AC8). A batch opens at most one gap per stored record, and the per-agent rows are
  bounded (one stream row, at most 64 clock samples).
- [x] Exceptions: a SQLite failure in the timeline is a `SqliteException` inside the existing `WriteBatchAsync` catch, which
  becomes `store: writing batch: <message>`, answered `503` by the endpoint without text (unchanged); `GapQuery` violations are
  `store: invalid query: <rule>` with fixed rules.
- The classification is no trust boundary: an agent holding the token chooses its own capture times and numbers.

## Decision records

- `docs/decisions/0130-live-or-backfilled-at-write-time-gaps-as-stateful-objects-offset-from-caught-up-self-samples.md`
  (Proposed; amends 0022).

## Out of scope / follow-ups

As spec *Out of scope*. No follow-up issue: retention of the new rows is noted for #46 in the Storage area.
