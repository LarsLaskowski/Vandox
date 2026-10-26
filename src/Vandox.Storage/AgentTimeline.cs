using Microsoft.Data.Sqlite;

using Vandox.Core.Model;

namespace Vandox.Storage;

/// <summary>
/// Keeps the gaps, the clock samples and the stream state of one agent in step with the batch of a write transaction. It runs on the
/// connection and in the transaction of the batch, so a refused or rolled-back batch changes nothing. Differences of instants are taken in
/// 128-bit integers, stored bounds are saturated and number ranges are clipped in <see cref="ulong"/>, so no capture time or number can make the work throw.
/// </summary>
internal sealed class AgentTimeline : IDisposable
{
    #region Constants

    private const string CountedSort = "counted";

    private const string RunSort = "run";

    private const long NanosecondsPerSecond = 1_000_000_000L;

    private const long NanosecondsPerTick = 100;

    #endregion // Constants

    #region Fields

    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;
    private readonly string _agentId;
    private readonly Dictionary<string, TimelineStatement> _statements = new(StringComparer.Ordinal);

    private bool _known;
    private long _floor;
    private long _max;
    private long _maxCaptured;
    private long _fillAt;
    private int _openGaps;
    private long? _offset;
    private long? _measuredAt;
    private bool _clockOff;

    private DateTimeOffset _received;
    private long _receivedNs;
    private long _liveOffset;
    private ulong? _lowestOpen;
    private bool _filledOpen;
    private bool _touched;
    private int _opened;
    private int _closed;
    private int _final;
    private bool? _crossing;
    private long _runStart = -1;
    private long _runEnd = -1;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="AgentTimeline"/> class.
    /// </summary>
    /// <param name="connection">The writer connection</param>
    /// <param name="transaction">The transaction</param>
    /// <param name="agentId">The agent ID</param>
    private AgentTimeline(SqliteConnection connection, SqliteTransaction transaction, string agentId)
    {
        _connection = connection;
        _transaction = transaction;
        _agentId = agentId;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Loads the stream state of an agent.
    /// </summary>
    /// <param name="connection">The writer connection</param>
    /// <param name="transaction">The transaction</param>
    /// <param name="agentId">The agent ID</param>
    /// <returns>The timeline</returns>
    internal static AgentTimeline Load(SqliteConnection connection, SqliteTransaction transaction, string agentId)
    {
        var timeline = new AgentTimeline(connection, transaction, agentId);

        timeline.ReadStream();

        return timeline;
    }

    /// <summary>
    /// Updates the gaps and the offset for the records of a batch.
    /// </summary>
    /// <param name="batch">The batch</param>
    /// <param name="restartWait">Whether this is the first batch of the agent since the store was opened</param>
    /// <param name="cancellationToken">Cancels the work</param>
    internal void Prepare(RecordBatch batch, bool restartWait, CancellationToken cancellationToken)
    {
        _received = batch.ReceivedAt;
        _receivedNs = StorageTime.ToNanoseconds(batch.ReceivedAt);
        _liveOffset = _offset ?? 0;

        var records = batch.Records.Where(record => record.Origin == RecordOrigin.Agent).OrderBy(record => record.Seq).ToList();

        if (records.Count == 0)
        {
            return;
        }

        _touched = true;

        StartIfNew(records[0]);

        var scan = Walk(records, cancellationToken);

        ApplyFinalWait(restartWait);
        UpdateClock(scan);
    }

    /// <summary>
    /// Decides whether a record of the prepared batch is live.
    /// </summary>
    /// <param name="record">The record</param>
    /// <returns><see langword="true"/> when the record is live</returns>
    internal bool IsLive(DataRecord record)
    {
        return record.Origin == RecordOrigin.Agent && LiveRules.IsLive(record, _received, _liveOffset, _lowestOpen);
    }

    /// <summary>
    /// Records the sample times of the stored metric records and updates the time gaps.
    /// </summary>
    /// <param name="batch">The batch</param>
    /// <param name="stored">The records that were stored</param>
    /// <param name="cancellationToken">Cancels the work</param>
    internal void WriteSampleTimes(RecordBatch batch, IReadOnlyList<DataRecord> stored, CancellationToken cancellationToken)
    {
        var times = new SortedDictionary<long, (long Min, long Max)>();

        foreach (var record in stored)
        {
            if (record.Origin != RecordOrigin.Agent || record.Data is not MetricPoint)
            {
                continue;
            }

            var time = StorageTime.ToNanoseconds(record.CapturedAt);
            var seq = (long)record.Seq;

            times[time] = times.TryGetValue(time, out var range) ? (Math.Min(range.Min, seq), Math.Max(range.Max, seq)) : (seq, seq);
        }

        foreach (var (time, range) in times)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddSampleTime(time, range.Min, range.Max);
        }
    }

    /// <summary>
    /// Writes the changed rows and the stream state.
    /// </summary>
    /// <param name="cancellationToken">Cancels the work</param>
    /// <returns>What the batch changed, or <see langword="null"/> when nothing of note</returns>
    internal TimelineChange? Finish(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_touched)
        {
            Statement(AgentTimelineQueries.SaveStream).Set(TimelineParameter.Agent, _agentId)
                                                      .Set("$floor", _floor)
                                                      .Set(TimelineParameter.Max, _max)
                                                      .Set("$captured", _maxCaptured)
                                                      .Set("$fill", _fillAt)
                                                      .Set("$open", (long)_openGaps)
                                                      .Set("$offset", _offset)
                                                      .Set("$measured", _measuredAt)
                                                      .Set("$off", _clockOff ? 1L : 0L)
                                                      .Execute();
        }

        if (_opened + _closed + _final == 0 && _crossing is null)
        {
            return null;
        }

        return new TimelineChange
               {
                   Opened = _opened,
                   Closed = _closed,
                   Final = _final,
                   Open = _openGaps,
                   OffsetNs = _offset,
                   ClockOff = _crossing
               };
    }

    /// <summary>
    /// Reads a column that may be NULL.
    /// </summary>
    /// <param name="reader">The reader</param>
    /// <param name="column">The column</param>
    /// <returns>The value, or <see langword="null"/></returns>
    private static long? ReadNullable(SqliteDataReader reader, int column)
    {
        return reader.IsDBNull(column) ? null : reader.GetInt64(column);
    }

    /// <summary>
    /// Gets a value indicating whether a span of nanoseconds is a time gap.
    /// </summary>
    /// <param name="nanoseconds">The span</param>
    /// <returns><see langword="true"/> when it is over the time gap span</returns>
    private static bool IsOverSpan(Int128 nanoseconds)
    {
        return nanoseconds > (Int128)TimelineLimits.TimeGapSpan.Ticks * NanosecondsPerTick;
    }

    /// <summary>
    /// Gets a statement of this transaction, creating it on first use.
    /// </summary>
    /// <param name="sql">The statement</param>
    /// <returns>The statement</returns>
    private TimelineStatement Statement(string sql)
    {
        if (_statements.TryGetValue(sql, out var statement))
        {
            return statement;
        }

        statement = new TimelineStatement(_connection, _transaction, sql);
        _statements[sql] = statement;

        return statement;
    }

    /// <summary>
    /// Reads the stream state of the agent, if it has one.
    /// </summary>
    private void ReadStream()
    {
        using var reader = Statement(AgentTimelineQueries.LoadStream).Set(TimelineParameter.Agent, _agentId).Query();

        if (reader.Read())
        {
            _known = true;
            _floor = reader.GetInt64(0);
            _max = reader.GetInt64(1);
            _maxCaptured = reader.GetInt64(2);
            _fillAt = reader.GetInt64(3);
            _openGaps = (int)reader.GetInt64(4);
            _offset = reader.IsDBNull(5) ? null : reader.GetInt64(5);
            _measuredAt = reader.IsDBNull(6) ? null : reader.GetInt64(6);
            _clockOff = reader.GetInt64(7) == 1;
        }
    }

    /// <summary>
    /// Starts the stream of an agent the store has not seen: numbers below its lowest number are never missing.
    /// </summary>
    /// <param name="first">The agent record with the lowest number of the batch</param>
    private void StartIfNew(DataRecord first)
    {
        if (_known)
        {
            return;
        }

        _floor = (long)first.Seq;
        _max = _floor - 1;
        _fillAt = _receivedNs;
    }

    /// <summary>
    /// Walks the agent records in ascending order: opens gaps for jumps, fills gaps with consecutive runs, applies the agent's own gap records, and collects the clock bounds.
    /// </summary>
    /// <param name="records">The agent records by number</param>
    /// <param name="cancellationToken">Cancels the work</param>
    /// <returns>The clock bounds of the records</returns>
    private ClockScan Walk(List<DataRecord> records, CancellationToken cancellationToken)
    {
        var scan = new ClockScan();

        _runStart = -1;
        _runEnd = -1;

        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var seq = (long)record.Seq;

            scan.Add(record, seq, ClockEstimator.BoundOf(record.CapturedAt, _received), seq > _max);
            Place(record, seq);

            if (record.Data is Gap { FirstSeq: not 0 } gap)
            {
                FlushRun();
                FinalizeNamed(gap, record.Seq);
            }
        }

        FlushRun();

        return scan;
    }

    /// <summary>
    /// Places a record in the stream: a number above the highest one advances it, opening a gap for a jump; a number inside it joins the run of arrived numbers.
    /// </summary>
    /// <param name="record">The record</param>
    /// <param name="seq">The number</param>
    private void Place(DataRecord record, long seq)
    {
        if (seq > _max)
        {
            FlushRun();

            if (seq > _max + 1)
            {
                OpenGap(_max + 1, seq - 1, record);
            }

            _max = seq;
            _maxCaptured = StorageTime.ToNanoseconds(record.CapturedAt);
        }
        else if (seq >= _floor && seq < _max)
        {
            ExtendRun(seq);
        }
    }

    /// <summary>
    /// Adds a number to the pending run of consecutive numbers, or starts a new run.
    /// </summary>
    /// <param name="seq">The number</param>
    private void ExtendRun(long seq)
    {
        if (_runStart >= 0 && seq == _runEnd + 1)
        {
            _runEnd = seq;

            return;
        }

        FlushRun();
        _runStart = seq;
        _runEnd = seq;
    }

    /// <summary>
    /// Fills the gaps with the pending run of consecutive numbers.
    /// </summary>
    private void FlushRun()
    {
        if (_runStart >= 0)
        {
            FillRange(_runStart, _runEnd);
        }

        _runStart = -1;
        _runEnd = -1;
    }

    /// <summary>
    /// Opens a sequence gap for the numbers between the highest stored number and a record.
    /// </summary>
    /// <param name="first">The first missing number</param>
    /// <param name="last">The last missing number</param>
    /// <param name="record">The record after the gap</param>
    private void OpenGap(long first, long last, DataRecord record)
    {
        var changed = _receivedNs;

        InsertSequenceGap(first, last, GapState.Open, GapCause.SequenceMissing, _maxCaptured, StorageTime.ToNanoseconds(record.CapturedAt), changed);
        _openGaps++;
        _opened++;
        _fillAt = changed;
    }

    /// <summary>
    /// Removes a run of arrived numbers from the gaps that contain them.
    /// </summary>
    /// <param name="first">The first number of the run</param>
    /// <param name="last">The last number of the run</param>
    private void FillRange(long first, long last)
    {
        var cursor = last;

        while (cursor >= first)
        {
            var gap = Seek(AgentTimelineQueries.FindGap, cursor);

            if (gap is null || gap.Value.Last < first)
            {
                return;
            }

            RemoveFrom(gap.Value, Math.Max(first, gap.Value.First), Math.Min(last, gap.Value.Last));
            cursor = gap.Value.First - 1;
        }
    }

    /// <summary>
    /// Removes the numbers that arrived from a gap: the gap shrinks, splits in two, or closes when none are left.
    /// </summary>
    /// <param name="gap">The gap</param>
    /// <param name="from">The first number that arrived</param>
    /// <param name="to">The last number that arrived</param>
    private void RemoveFrom(SequenceGapRow gap, long from, long to)
    {
        var isOpen = gap.State == GapState.Open;

        _filledOpen |= isOpen;

        if (from == gap.First && to == gap.Last)
        {
            UpdateSequenceGap(gap.Id, gap.First, gap.Last, GapState.Closed, gap.Cause);
            _closed++;
            _openGaps -= isOpen ? 1 : 0;
        }
        else if (from == gap.First)
        {
            UpdateSequenceGap(gap.Id, to + 1, gap.Last, gap.State, gap.Cause);
        }
        else if (to == gap.Last)
        {
            UpdateSequenceGap(gap.Id, gap.First, from - 1, gap.State, gap.Cause);
        }
        else
        {
            UpdateSequenceGap(gap.Id, gap.First, from - 1, gap.State, gap.Cause);
            InsertSequenceGap(to + 1, gap.Last, gap.State, gap.Cause, gap.FromAt, gap.ToAt, gap.DetectedAt);
            _opened++;
            _openGaps += isOpen ? 1 : 0;
        }
    }

    /// <summary>
    /// Makes the part of the open sequence gaps that an agent gap record names final with the record's cause; only numbers below the record's own number count.
    /// </summary>
    /// <param name="gap">The gap payload</param>
    /// <param name="ownSeq">The number of the gap record</param>
    private void FinalizeNamed(Gap gap, ulong ownSeq)
    {
        var high = Math.Min(gap.LastSeq, ownSeq - 1);

        if (gap.FirstSeq > high)
        {
            return;
        }

        var low = (long)gap.FirstSeq;
        var top = (long)high;
        var cursor = top;

        while (cursor >= low)
        {
            var open = Seek(AgentTimelineQueries.FindOpenGap, cursor);

            if (open is null || open.Value.Last < low)
            {
                return;
            }

            FinalizePart(open.Value, Math.Max(low, open.Value.First), Math.Min(top, open.Value.Last), gap.Cause);
            cursor = open.Value.First - 1;
        }
    }

    /// <summary>
    /// Makes a part of an open sequence gap final; the lower and the upper rest stay open.
    /// </summary>
    /// <param name="open">The gap</param>
    /// <param name="from">The first number of the part</param>
    /// <param name="to">The last number of the part</param>
    /// <param name="cause">The cause the part gets</param>
    private void FinalizePart(SequenceGapRow open, long from, long to, string cause)
    {
        _final++;

        if (from > open.First)
        {
            UpdateSequenceGap(open.Id, open.First, from - 1, GapState.Open, open.Cause);
            InsertSequenceGap(from, to, GapState.Final, cause, open.FromAt, open.ToAt, open.DetectedAt);
        }
        else
        {
            UpdateSequenceGap(open.Id, from, to, GapState.Final, cause);
            _openGaps--;
        }

        if (to < open.Last)
        {
            InsertSequenceGap(to + 1, open.Last, GapState.Open, open.Cause, open.FromAt, open.ToAt, open.DetectedAt);
            _opened++;
            _openGaps++;
        }
    }

    /// <summary>
    /// Restarts the wait after a backend start, or makes the open sequence gaps final when nothing arrived into them for the wait; then finds the lowest open gap.
    /// </summary>
    /// <param name="restartWait">Whether this is the first batch of the agent since the store was opened</param>
    private void ApplyFinalWait(bool restartWait)
    {
        _lowestOpen = SeekLowestOpen();

        if (restartWait || _filledOpen)
        {
            _fillAt = _receivedNs;
        }
        else if (_lowestOpen is not null && (Int128)_receivedNs - _fillAt >= (Int128)TimelineLimits.FinalWait.Ticks * NanosecondsPerTick)
        {
            _final += Statement(AgentTimelineQueries.FinalizeOpenGaps).Set(TimelineParameter.Agent, _agentId).Set(TimelineParameter.Changed, _receivedNs).Execute();
            _lowestOpen = null;
        }

        if (_lowestOpen is null)
        {
            _openGaps = 0;
        }
    }

    /// <summary>
    /// Gets the first number of the lowest open sequence gap.
    /// </summary>
    /// <returns>The number, or <see langword="null"/> when no sequence gap is open</returns>
    private ulong? SeekLowestOpen()
    {
        using var reader = Statement(AgentTimelineQueries.LowestOpenGap).Set(TimelineParameter.Agent, _agentId).Query();

        return reader.Read() ? (ulong)reader.GetInt64(0) : null;
    }

    /// <summary>
    /// Stores the clock bounds of the batch and measures the offset from them. A counted sample of the batch counts for the batch's own records; a bound of a run-timed
    /// record only for the batches after it, so no record can prove itself current.
    /// </summary>
    /// <param name="scan">The bounds of the batch's records</param>
    private void UpdateClock(ClockScan scan)
    {
        long? counted = null;

        foreach (var (seq, bound) in scan.Candidates)
        {
            if (_lowestOpen is null || _lowestOpen.Value >= (ulong)seq)
            {
                counted = counted is null ? bound : Math.Max(counted.Value, bound);
            }
        }

        var bucket = StorageTime.ToNanoseconds(ClockEstimator.BucketOf(_received));

        if (counted is not null)
        {
            WriteSample(CountedSort, bucket, counted.Value);
            _measuredAt = _receivedNs;
            SetOffset(ClockEstimator.Estimate(ReadSamples(CountedSort), []), true);
        }
        else if (scan.RunBound is not null && _measuredAt is null)
        {
            WriteSample(RunSort, bucket, scan.RunBound.Value);
            SetOffset(ClockEstimator.Estimate([], ReadSamples(RunSort)), false);
        }
    }

    /// <summary>
    /// Takes a new offset and notes a crossing of the allowance.
    /// </summary>
    /// <param name="estimate">The offset in nanoseconds</param>
    /// <param name="forBatch">Whether the batch's own records are classified with it</param>
    private void SetOffset(long estimate, bool forBatch)
    {
        _offset = estimate;
        _liveOffset = forBatch ? estimate : _liveOffset;

        var off = Int128.Abs(estimate) > (Int128)90 * NanosecondsPerSecond;

        if (off != _clockOff)
        {
            _clockOff = off;
            _crossing = off;
        }
    }

    /// <summary>
    /// Writes the bound of a bucket and drops the rows beyond the newest ones.
    /// </summary>
    /// <param name="sort">The sort</param>
    /// <param name="bucket">The bucket</param>
    /// <param name="bound">The bound</param>
    private void WriteSample(string sort, long bucket, long bound)
    {
        Statement(AgentTimelineQueries.UpsertClockSample).Set(TimelineParameter.Agent, _agentId).Set(TimelineParameter.Sort, sort).Set("$bucket", bucket).Set("$bound", bound).Execute();
        Statement(AgentTimelineQueries.PruneClockSamples).Set(TimelineParameter.Agent, _agentId).Set(TimelineParameter.Sort, sort).Set("$keep", (long)(TimelineLimits.MaxClockSamples - 1)).Execute();
    }

    /// <summary>
    /// Reads the clock sample rows of a sort.
    /// </summary>
    /// <param name="sort">The sort</param>
    /// <returns>The samples, oldest first</returns>
    private List<ClockSample> ReadSamples(string sort)
    {
        var samples = new List<ClockSample>();

        using var reader = Statement(AgentTimelineQueries.ReadClockSamples).Set(TimelineParameter.Agent, _agentId).Set(TimelineParameter.Sort, sort).Query();

        while (reader.Read())
        {
            samples.Add(new ClockSample(StorageTime.FromNanoseconds(reader.GetInt64(0)), reader.GetInt64(1)));
        }

        return samples;
    }

    /// <summary>
    /// Finds the sequence gap a statement selects.
    /// </summary>
    /// <param name="sql">The statement</param>
    /// <param name="seq">The number</param>
    /// <returns>The gap, or <see langword="null"/> when there is none</returns>
    private SequenceGapRow? Seek(string sql, long seq)
    {
        using var reader = Statement(sql).Set(TimelineParameter.Agent, _agentId).Set(TimelineParameter.Seq, seq).Query();

        return reader.Read()
                   ? new SequenceGapRow(reader.GetInt64(0),
                                        reader.GetInt64(1),
                                        reader.GetInt64(2),
                                        reader.GetString(3),
                                        reader.GetString(4),
                                        reader.GetInt64(5),
                                        reader.GetInt64(6),
                                        reader.GetInt64(7))
                   : null;
    }

    /// <summary>
    /// Inserts a sequence gap.
    /// </summary>
    /// <param name="first">The first missing number</param>
    /// <param name="last">The last missing number</param>
    /// <param name="state">The state</param>
    /// <param name="cause">The cause</param>
    /// <param name="fromAt">The start of the span</param>
    /// <param name="toAt">The end of the span</param>
    /// <param name="detectedAt">When the gap was detected</param>
    private void InsertSequenceGap(long first, long last, string state, string cause, long fromAt, long toAt, long detectedAt)
    {
        Statement(AgentTimelineQueries.InsertGap).Set(TimelineParameter.Agent, _agentId)
                                                 .Set("$kind", GapKind.Sequence)
                                                 .Set(TimelineParameter.Cause, cause)
                                                 .Set(TimelineParameter.State, state)
                                                 .Set(TimelineParameter.First, first)
                                                 .Set(TimelineParameter.Last, last)
                                                 .Set(TimelineParameter.FromSeq, null)
                                                 .Set(TimelineParameter.ToSeq, null)
                                                 .Set(TimelineParameter.From, fromAt)
                                                 .Set(TimelineParameter.To, toAt)
                                                 .Set(TimelineParameter.Detected, detectedAt)
                                                 .Set(TimelineParameter.Changed, _receivedNs)
                                                 .Execute();
    }

    /// <summary>
    /// Changes the number range, state and cause of a sequence gap.
    /// </summary>
    /// <param name="id">The ID</param>
    /// <param name="first">The first missing number</param>
    /// <param name="last">The last missing number</param>
    /// <param name="state">The state</param>
    /// <param name="cause">The cause</param>
    private void UpdateSequenceGap(long id, long first, long last, string state, string cause)
    {
        Statement(AgentTimelineQueries.UpdateSequenceGap).Set(TimelineParameter.Id, id)
                                                         .Set(TimelineParameter.First, first)
                                                         .Set(TimelineParameter.Last, last)
                                                         .Set(TimelineParameter.State, state)
                                                         .Set(TimelineParameter.Cause, cause)
                                                         .Set(TimelineParameter.Changed, _receivedNs)
                                                         .Execute();
    }

    /// <summary>
    /// Records one sample time and updates the time gaps around it.
    /// </summary>
    /// <param name="time">The capture time in nanoseconds</param>
    /// <param name="min">The lowest number of a metric at this time in the batch</param>
    /// <param name="max">The highest number of a metric at this time in the batch</param>
    private void AddSampleTime(long time, long min, long max)
    {
        var inserted = Statement(AgentTimelineQueries.InsertSampleTime).Set(TimelineParameter.Agent, _agentId).Set(TimelineParameter.Time, time).Set(TimelineParameter.Min, min).Set(TimelineParameter.Max, max).Execute();

        if (inserted == 0)
        {
            Statement(AgentTimelineQueries.WidenSampleTime).Set(TimelineParameter.Agent, _agentId).Set(TimelineParameter.Time, time).Set(TimelineParameter.Min, min).Set(TimelineParameter.Max, max).Execute();

            return;
        }

        var gap = SeekTimeGap(time);

        if (gap is not null && gap.Value.To > time)
        {
            SplitTimeGap(gap.Value, time, min, max);

            return;
        }

        var previous = Neighbour(AgentTimelineQueries.PreviousSampleTime, time);
        var next = Neighbour(AgentTimelineQueries.NextSampleTime, time);

        if (previous is not null && IsOverSpan((Int128)time - previous.Value.Time))
        {
            CreateTimeGap(previous.Value.Time, time, previous.Value.Seq, min, _receivedNs);
        }

        if (next is not null && IsOverSpan((Int128)next.Value.Time - time))
        {
            CreateTimeGap(time, next.Value.Time, max, next.Value.Seq, _receivedNs);
        }
    }

    /// <summary>
    /// Narrows, splits or closes the time gap that contains a new sample time.
    /// </summary>
    /// <param name="gap">The gap</param>
    /// <param name="time">The sample time</param>
    /// <param name="min">The lowest number at the time</param>
    /// <param name="max">The highest number at the time</param>
    private void SplitTimeGap(TimeGapRow gap, long time, long min, long max)
    {
        var lower = IsOverSpan((Int128)time - gap.From);
        var upper = IsOverSpan((Int128)gap.To - time);

        if (lower && upper)
        {
            UpdateTimeGap(gap.Id, gap.From, time, gap.FromSeq, min, GapState.Open);
            CreateTimeGap(time, gap.To, max, gap.ToSeq, gap.DetectedAt);
        }
        else if (lower)
        {
            UpdateTimeGap(gap.Id, gap.From, time, gap.FromSeq, min, GapState.Open);
        }
        else if (upper)
        {
            UpdateTimeGap(gap.Id, time, gap.To, max, gap.ToSeq, GapState.Open);
        }
        else
        {
            UpdateTimeGap(gap.Id, gap.From, gap.To, gap.FromSeq, gap.ToSeq, GapState.Closed);
            _closed++;
        }
    }

    /// <summary>
    /// Creates a time gap, which is stored open and reads final when no open sequence gap lies between its neighbouring records.
    /// </summary>
    /// <param name="from">The start</param>
    /// <param name="to">The end</param>
    /// <param name="fromSeq">The highest metric number at the start</param>
    /// <param name="toSeq">The lowest metric number at the end</param>
    /// <param name="detectedAt">When the gap was detected</param>
    private void CreateTimeGap(long from, long to, long? fromSeq, long? toSeq, long detectedAt)
    {
        var rows = Statement(AgentTimelineQueries.InsertGap).Set(TimelineParameter.Agent, _agentId)
                                                            .Set("$kind", GapKind.Time)
                                                            .Set(TimelineParameter.Cause, GapCause.NoData)
                                                            .Set(TimelineParameter.State, GapState.Open)
                                                            .Set(TimelineParameter.First, null)
                                                            .Set(TimelineParameter.Last, null)
                                                            .Set(TimelineParameter.FromSeq, fromSeq)
                                                            .Set(TimelineParameter.ToSeq, toSeq)
                                                            .Set(TimelineParameter.From, from)
                                                            .Set(TimelineParameter.To, to)
                                                            .Set(TimelineParameter.Detected, detectedAt)
                                                            .Set(TimelineParameter.Changed, _receivedNs)
                                                            .Execute();

        if (rows == 0)
        {
            return;
        }

        _opened++;

        var between = toSeq is null ? null : Seek(AgentTimelineQueries.FindOpenGap, toSeq.Value - 1);

        if (between is null || between.Value.Last <= fromSeq)
        {
            _final++;
        }
    }

    /// <summary>
    /// Changes the span, numbers and state of a time gap.
    /// </summary>
    /// <param name="id">The ID</param>
    /// <param name="from">The start</param>
    /// <param name="to">The end</param>
    /// <param name="fromSeq">The highest metric number at the start</param>
    /// <param name="toSeq">The lowest metric number at the end</param>
    /// <param name="state">The state</param>
    private void UpdateTimeGap(long id, long from, long to, long? fromSeq, long? toSeq, string state)
    {
        Statement(AgentTimelineQueries.UpdateTimeGap).Set(TimelineParameter.Id, id)
                                                     .Set(TimelineParameter.From, from)
                                                     .Set(TimelineParameter.To, to)
                                                     .Set(TimelineParameter.FromSeq, fromSeq)
                                                     .Set(TimelineParameter.ToSeq, toSeq)
                                                     .Set(TimelineParameter.State, state)
                                                     .Set(TimelineParameter.Changed, _receivedNs)
                                                     .Execute();
    }

    /// <summary>
    /// Finds the time gap that is not closed with the greatest start before a time.
    /// </summary>
    /// <param name="time">The time</param>
    /// <returns>The gap, or <see langword="null"/> when there is none</returns>
    private TimeGapRow? SeekTimeGap(long time)
    {
        using var reader = Statement(AgentTimelineQueries.FindTimeGap).Set(TimelineParameter.Agent, _agentId).Set(TimelineParameter.Time, time).Query();

        if (reader.Read())
        {
            return new TimeGapRow(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), ReadNullable(reader, 3), ReadNullable(reader, 4), reader.GetInt64(6));
        }

        return null;
    }

    /// <summary>
    /// Finds the sample time next to a time.
    /// </summary>
    /// <param name="sql">The statement</param>
    /// <param name="time">The time</param>
    /// <returns>The time and its metric number, or <see langword="null"/> when there is none</returns>
    private (long Time, long Seq)? Neighbour(string sql, long time)
    {
        using var reader = Statement(sql).Set(TimelineParameter.Agent, _agentId).Set(TimelineParameter.Time, time).Query();

        return reader.Read() ? (reader.GetInt64(0), reader.GetInt64(1)) : null;
    }

    #endregion // Methods

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var statement in _statements.Values)
        {
            statement.Dispose();
        }
    }

    #endregion // IDisposable
}