using Vandox.Core.Model;

namespace Vandox.Storage;

/// <summary>
/// Runs one removal step: it walks the stages in order from the cursor, removes expired rows of the stage it is in, and stops
/// at the bound of rows removed or examined, or after the first stage that removed something, so a step never touches more than
/// one batch's worth of rows of one kind.
/// </summary>
internal sealed class RetentionRemover
{
    #region Constants

    private const string GapRecords = RecordKind.Gap;

    private const string GapRows = "gap_rows";

    private const string FiveMinuteBuckets = "rollups_5m";

    private const string HourlyBuckets = "rollups_1h";

    private const string TemplateHours = "template_hours";

    private const string Incidents = "incidents";

    private const string Holds = "holds";

    private const int MaxWindows = 1000;

    private const long MergePages = -64;

    private const string UpperParameter = "$upper";

    private const string LimitParameter = "$limit";

    private const long HourNs = 3_600_000_000_000;

    #endregion // Constants

    #region Fields

    private static readonly string[] _stages = [
                                                   RecordKind.Metric,
                                                   RecordKind.LogLine,
                                                   RecordKind.ProcessSnapshot,
                                                   RecordKind.ConnectionSnapshot,
                                                   RecordKind.MariaDbStatus,
                                                   RecordKind.KernelEvent,
                                                   RecordKind.ServiceState,
                                                   RecordKind.BackupRun,
                                                   GapRecords,
                                                   GapRows,
                                                   FiveMinuteBuckets,
                                                   HourlyBuckets,
                                                   TemplateHours,
                                                   Incidents,
                                                   Holds
                                               ];

    private readonly RetentionSession _session;
    private readonly RetentionPolicy _policy;
    private readonly DateTimeOffset _now;
    private readonly CancellationToken _cancellationToken;
    private readonly long _highestId;
    private readonly long _scanned;
    private readonly long _watermark;
    private readonly long? _eventsHorizon;
    private readonly Dictionary<string, int> _counts = [with(StringComparer.Ordinal)];

    private int _removed;
    private int _examined;
    private int _windows;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="RetentionRemover"/> class.
    /// </summary>
    /// <param name="session">The session of the step</param>
    /// <param name="policy">The retention policy</param>
    /// <param name="now">The current time</param>
    /// <param name="cancellationToken">Cancels the step between rows</param>
    private RetentionRemover(RetentionSession session, RetentionPolicy policy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        _session = session;
        _policy = policy;
        _now = now;
        _cancellationToken = cancellationToken;
        _highestId = session.ReadHighestId();
        _scanned = session.ReadNumber(RetentionQueries.KillScanKey);
        _watermark = session.ReadNumber(RetentionQueries.RollupWatermarkKey);
        _eventsHorizon = policy.Events is null ? null : StorageTime.ToNanoseconds(now - policy.Events.Value);
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets a value indicating whether the step has reached a bound.
    /// </summary>
    private bool BoundReached => _removed >= StorageLimits.RemovalStepRecords || _examined >= StorageLimits.RemovalStepExamined || _windows >= MaxWindows;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Runs one removal step in the open transaction of the session.
    /// </summary>
    /// <param name="session">The session of the step</param>
    /// <param name="policy">The retention policy</param>
    /// <param name="now">The current time</param>
    /// <param name="cursor">Where the step continues, or <c>null</c> to start a pass</param>
    /// <param name="cancellationToken">Cancels the step between rows</param>
    /// <returns>The result of the step</returns>
    /// <exception cref="InvalidQueryException">The cursor names no stage</exception>
    internal static RemovalStepResult Step(RetentionSession session, RetentionPolicy policy, DateTimeOffset now, RemovalCursor? cursor, CancellationToken cancellationToken)
    {
        var first = cursor is null ? 0 : Array.IndexOf(_stages, cursor.Kind);

        RecordQueries.Require(first >= 0, "removal cursor names no stage");

        var remover = new RetentionRemover(session, policy, now, cancellationToken);
        var position = cursor is null ? new RetentionPosition() : new RetentionPosition(StorageTime.ToNanoseconds(cursor.CapturedAt), cursor.Id);

        return remover.Run(first, position);
    }

    /// <summary>
    /// Tells whether records after a position still fall into a hold that ends at a time: the position moves past a hold to its
    /// end with the largest ID, which no record has.
    /// </summary>
    /// <param name="end">The end of the hold in nanoseconds</param>
    /// <param name="position">The position</param>
    /// <returns><c>true</c> when records after the position can lie in the hold</returns>
    private static bool IsInside(long end, RetentionPosition position)
    {
        return end > position.At || (end == position.At && position.Id < long.MaxValue);
    }

    /// <summary>
    /// Runs the stages from a stage and a position.
    /// </summary>
    /// <param name="first">The index of the first stage</param>
    /// <param name="position">The position inside the first stage</param>
    /// <returns>The result of the step</returns>
    private RemovalStepResult Run(int first, RetentionPosition position)
    {
        for (var index = first; index < _stages.Length; index++)
        {
            var stage = _stages[index];
            var before = _removed;
            var finished = RunStage(stage, position);

            _counts[stage] = _counts.GetValueOrDefault(stage) + (_removed - before);

            if (stage == RecordKind.LogLine && _removed > before)
            {
                _session.Statement(RetentionQueries.MergeFullText).Set("$pages", MergePages).Execute();
            }

            if (finished == false)
            {
                return Result(new RemovalCursor
                              {
                                  Kind = stage,
                                  CapturedAt = StorageTime.FromNanoseconds(position.At),
                                  Id = position.Id
                              });
            }

            position.Reset();

            if (_removed > 0 || _examined >= StorageLimits.RemovalStepExamined)
            {
                return Result(index + 1 < _stages.Length
                                  ? new RemovalCursor
                                    {
                                        Kind = _stages[index + 1],
                                        CapturedAt = StorageTime.FromNanoseconds(position.At),
                                        Id = position.Id
                                    }
                                  : null);
            }
        }

        return Result(null);
    }

    /// <summary>
    /// Builds the result of the step.
    /// </summary>
    /// <param name="next">Where the next step continues, or <c>null</c> when nothing is left</param>
    /// <returns>The result</returns>
    private RemovalStepResult Result(RemovalCursor? next)
    {
        return new RemovalStepResult
               {
                   MetricValues = Count(RecordKind.Metric),
                   LogLines = Count(RecordKind.LogLine),
                   Snapshots = Count(RecordKind.ProcessSnapshot) + Count(RecordKind.ConnectionSnapshot) + Count(RecordKind.MariaDbStatus),
                   EventRecords = Count(RecordKind.KernelEvent) + Count(RecordKind.ServiceState) + Count(RecordKind.BackupRun) + Count(GapRecords),
                   Gaps = Count(GapRows),
                   FiveMinuteValues = Count(FiveMinuteBuckets),
                   HourlyValues = Count(HourlyBuckets),
                   TemplateHours = Count(TemplateHours),
                   RemovedIncidents = Count(Incidents),
                   Holds = Count(Holds),
                   More = next is not null,
                   Next = next
               };
    }

    /// <summary>
    /// Gets the rows a stage removed in this step.
    /// </summary>
    /// <param name="stage">The stage</param>
    /// <returns>The number of rows</returns>
    private int Count(string stage)
    {
        return _counts.GetValueOrDefault(stage);
    }

    /// <summary>
    /// Gets the period of a stage.
    /// </summary>
    /// <param name="stage">The stage</param>
    /// <returns>The period, or <c>null</c> when the stage keeps everything</returns>
    private TimeSpan? PeriodOf(string stage)
    {
        return stage switch
               {
                   RecordKind.Metric => _policy.Raw,
                   RecordKind.LogLine => _policy.Logs,
                   RecordKind.ProcessSnapshot or RecordKind.ConnectionSnapshot or RecordKind.MariaDbStatus => _policy.Snapshots,
                   RecordKind.KernelEvent or RecordKind.ServiceState or RecordKind.BackupRun => _policy.Events,
                   GapRecords or GapRows => GapPeriod(),
                   FiveMinuteBuckets => _policy.FiveMinuteRollups,
                   HourlyBuckets or TemplateHours => _policy.HourlyRollups,
                   _ => _policy.Incidents
               };
    }

    /// <summary>
    /// Gets the gap horizon: the longest of all finite periods of data and rollups, none while one is unlimited.
    /// </summary>
    /// <returns>The period, or <c>null</c> when a period is unlimited</returns>
    private TimeSpan? GapPeriod()
    {
        TimeSpan?[] periods = [_policy.Events, _policy.Raw, _policy.Logs, _policy.Snapshots, _policy.FiveMinuteRollups, _policy.HourlyRollups];

        return periods.Any(period => period is null) ? null : periods.Max();
    }

    /// <summary>
    /// Runs one stage.
    /// </summary>
    /// <param name="stage">The stage</param>
    /// <param name="position">The position, moved as the stage proceeds</param>
    /// <returns><c>true</c> when nothing of the stage is left, <c>false</c> when a bound ended the stage</returns>
    private bool RunStage(string stage, RetentionPosition position)
    {
        var period = PeriodOf(stage);

        if (period is null)
        {
            return true;
        }

        var horizon = StorageTime.ToNanoseconds(_now - period.Value);

        return stage switch
               {
                   RecordKind.Metric => PruneSampleTimes(horizon) && WalkRecords(stage, horizon, position),
                   GapRows => Walk(limit => ReadGaps(position, horizon, limit), row => RemoveGap(row, position)),
                   FiveMinuteBuckets => Walk(limit => ReadBuckets(RetentionQueries.WalkFiveMinutes, horizon, limit), row => RemoveBucket(RetentionQueries.DeleteFiveMinutes, row)),
                   HourlyBuckets => Walk(limit => ReadBuckets(RetentionQueries.WalkHourly, horizon, limit), row => RemoveBucket(RetentionQueries.DeleteHourly, row)),
                   TemplateHours => Walk(limit => ReadTemplateHours(position, horizon, limit), row => RemoveTemplateHour(row, position)),
                   Incidents => Walk(limit => ReadIncidents(position, horizon, limit), row => RemoveIncident(row, position)),
                   Holds => Walk(limit => ReadHolds(position, horizon, limit), row => RemoveHold(row, position)),
                   _ => WalkRecords(stage, horizon, position)
               };
    }

    /// <summary>
    /// Deletes each agent's sample times below the raw horizon except its newest, then raises the sample-time floor to the horizon.
    /// </summary>
    /// <param name="horizon">The raw horizon in nanoseconds</param>
    /// <returns><c>true</c> when every agent is pruned, <c>false</c> when the bound of the step ended the work</returns>
    private bool PruneSampleTimes(long horizon)
    {
        var agent = string.Empty;

        while (NextAgent(agent) is { } next)
        {
            agent = next;

            if (PruneAgent(agent, horizon) == false)
            {
                return false;
            }
        }

        if (_session.ReadNumber(RetentionQueries.SampleFloorKey) < horizon)
        {
            _session.WriteNumber(RetentionQueries.SampleFloorKey, horizon);
        }

        return true;
    }

    /// <summary>
    /// Deletes the sample times of one agent below a bound and below its newest sample time.
    /// </summary>
    /// <param name="agent">The agent ID</param>
    /// <param name="horizon">The raw horizon in nanoseconds</param>
    /// <returns><c>false</c> when the bound of the step ended the work</returns>
    private bool PruneAgent(string agent, long horizon)
    {
        using var newest = _session.Statement(RetentionQueries.NewestSampleTime).Set("$agent", agent).Query();
        var upper = newest.Read() && newest.IsDBNull(0) == false ? Math.Min(horizon, newest.GetInt64(0)) : long.MinValue;

        newest.Close();

        var times = new List<long>();

        using (var reader = _session.Statement(RetentionQueries.OldSampleTimes).Set("$agent", agent).Set("$upper", upper).Set(LimitParameter, (long)(StorageLimits.RemovalStepExamined - _examined)).Query())
        {
            while (reader.Read())
            {
                times.Add(reader.GetInt64(0));
            }
        }

        foreach (var time in times)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            _session.Statement(RetentionQueries.DeleteSampleTime).Set("$agent", agent).Set("$at", time).Execute();
            _examined++;
        }

        return BoundReached == false;
    }

    /// <summary>
    /// Reads the first agent after an agent ID.
    /// </summary>
    /// <param name="after">The agent ID</param>
    /// <returns>The agent ID, or <c>null</c> when there is none</returns>
    private string? NextAgent(string after)
    {
        using var reader = _session.Statement(RetentionQueries.NextAgent).Set("$after", after).Query();

        return reader.Read() ? reader.GetString(0) : null;
    }

    /// <summary>
    /// Walks the rows of a stage that has no holds: reads a page, processes each row, and ends when a page is short.
    /// </summary>
    /// <typeparam name="TRow">The type of a row</typeparam>
    /// <param name="read">Reads a page of at most the given size from the position</param>
    /// <param name="process">Processes a row and tells whether it was removed</param>
    /// <returns><c>true</c> when nothing is left, <c>false</c> when a bound ended the walk</returns>
    private bool Walk<TRow>(Func<int, List<TRow>> read, Func<TRow, bool> process)
    {
        while (true)
        {
            if (BoundReached)
            {
                return false;
            }

            var limit = StorageLimits.RemovalStepExamined - _examined;
            var rows = read(limit);

            foreach (var row in rows)
            {
                if (_removed >= StorageLimits.RemovalStepRecords)
                {
                    return false;
                }

                _cancellationToken.ThrowIfCancellationRequested();
                _examined++;

                if (process(row))
                {
                    _removed++;
                }
            }

            if (rows.Count < limit)
            {
                return true;
            }
        }
    }

    /// <summary>
    /// Walks the removable records of one kind in capture order, in the ranges between the holds.
    /// </summary>
    /// <param name="kind">The record kind</param>
    /// <param name="horizon">The capture time before which a record has expired</param>
    /// <param name="position">The position, moved as the walk proceeds</param>
    /// <returns><c>true</c> when nothing is left, <c>false</c> when a bound ended the walk</returns>
    private bool WalkRecords(string kind, long horizon, RetentionPosition position)
    {
        while (BoundReached == false)
        {
            _windows++;

            if (HoldAt(position.At) is { } hold && IsInside(hold.End, position))
            {
                position.Set(hold.End, long.MaxValue);

                continue;
            }

            var next = NextHoldStart(position.At);
            var upper = next is { } start && start < horizon ? start : horizon;
            var limit = StorageLimits.RemovalStepExamined - _examined;
            var rows = ReadRecords(kind, position, upper, limit);

            if (ProcessRecords(kind, rows, position) == false)
            {
                return false;
            }

            if (rows.Count < limit)
            {
                if (upper == horizon)
                {
                    return true;
                }

                position.Set(upper, 0);
            }
        }

        return false;
    }

    /// <summary>
    /// Removes the expired records of a page that nothing keeps.
    /// </summary>
    /// <param name="kind">The record kind</param>
    /// <param name="rows">The page</param>
    /// <param name="position">The position, moved to each record</param>
    /// <returns><c>false</c> when the bound of removed records ended the page early</returns>
    private bool ProcessRecords(string kind, List<RetentionRecordRow> rows, RetentionPosition position)
    {
        foreach (var row in rows)
        {
            if (_removed >= StorageLimits.RemovalStepRecords)
            {
                return false;
            }

            _cancellationToken.ThrowIfCancellationRequested();
            _examined++;
            position.Set(row.At, row.Id);

            if (IsRemovable(kind, row))
            {
                RemoveRecord(kind, row);
                _removed++;
            }
        }

        return true;
    }

    /// <summary>
    /// Reads a page of the removable records of a kind.
    /// </summary>
    /// <param name="kind">The record kind</param>
    /// <param name="position">The position to read after</param>
    /// <param name="upper">The capture time the page stays below</param>
    /// <param name="limit">The page size</param>
    /// <returns>The records</returns>
    private List<RetentionRecordRow> ReadRecords(string kind, RetentionPosition position, long upper, int limit)
    {
        var rows = new List<RetentionRecordRow>();

        using var reader = _session.Statement(RetentionQueries.WalkRecords).Set("$kind", kind).Set("$at", position.At).Set("$id", position.Id).Set(UpperParameter, upper).Set(LimitParameter, (long)limit).Query();

        while (reader.Read())
        {
            rows.Add(new RetentionRecordRow(reader.GetInt64(0), reader.GetInt64(1)));
        }

        return rows;
    }

    /// <summary>
    /// Tells whether an expired record is removed.
    /// </summary>
    /// <param name="kind">The record kind</param>
    /// <param name="row">The record</param>
    /// <returns><c>true</c> when the record is removed</returns>
    private bool IsRemovable(string kind, RetentionRecordRow row)
    {
        if (row.Id > _scanned || row.Id == _highestId)
        {
            return false;
        }

        if (kind == RecordKind.Metric)
        {
            return row.Id <= _watermark;
        }

        if (kind != RecordKind.ConnectionSnapshot)
        {
            return true;
        }

        if (RetentionSession.Exists(_session.Statement(RetentionQueries.IsNewestMark).Set("$id", row.Id)))
        {
            return false;
        }

        var keptFinding = _eventsHorizon is null || row.At >= _eventsHorizon;

        return keptFinding == false || RetentionSession.Exists(_session.Statement(RetentionQueries.HasFinding).Set("$id", row.Id)) == false;
    }

    /// <summary>
    /// Removes a record with its rows in the tables of its kind.
    /// </summary>
    /// <param name="kind">The record kind</param>
    /// <param name="row">The record</param>
    private void RemoveRecord(string kind, RetentionRecordRow row)
    {
        switch (kind)
        {
            case RecordKind.Metric:
                {
                    Delete(RetentionQueries.DeleteMetric, row.Id);
                }
                break;

            case RecordKind.LogLine:
                {
                    Delete(RetentionQueries.DeleteFullText, row.Id);
                    Delete(RetentionQueries.DeleteLogLine, row.Id);
                }
                break;

            case RecordKind.ConnectionSnapshot:
                {
                    Delete(RetentionQueries.DeleteMark, row.Id);
                    Delete(RetentionQueries.DeleteComparison, row.Id);
                }
                break;
        }

        Delete(RetentionQueries.DeleteRecord, row.Id);
    }

    /// <summary>
    /// Runs a statement that deletes by ID.
    /// </summary>
    /// <param name="sql">The statement</param>
    /// <param name="id">The ID</param>
    private void Delete(string sql, long id)
    {
        _session.Statement(sql).Set("$id", id).Execute();
    }

    /// <summary>
    /// Reads the hold with the greatest start at or below a position.
    /// </summary>
    /// <param name="at">The position</param>
    /// <returns>The hold, or <c>null</c> when none starts at or below it</returns>
    private (long Start, long End)? HoldAt(long at)
    {
        using var reader = _session.Statement(RetentionQueries.HoldAt).Set("$position", at).Query();

        return reader.Read() ? (reader.GetInt64(0), reader.GetInt64(1)) : null;
    }

    /// <summary>
    /// Reads the start of the first hold above a position.
    /// </summary>
    /// <param name="at">The position</param>
    /// <returns>The start, or <c>null</c> when no hold starts above it</returns>
    private long? NextHoldStart(long at)
    {
        using var reader = _session.Statement(RetentionQueries.NextHoldStart).Set("$position", at).Query();

        return reader.Read() ? reader.GetInt64(0) : null;
    }

    /// <summary>
    /// Reads a page of gap rows that ended before the gap horizon.
    /// </summary>
    /// <param name="position">The position to read after</param>
    /// <param name="horizon">The gap horizon</param>
    /// <param name="limit">The page size</param>
    /// <returns>The rows</returns>
    private List<RetentionGapRow> ReadGaps(RetentionPosition position, long horizon, int limit)
    {
        var rows = new List<RetentionGapRow>();

        using var reader = _session.Statement(RetentionQueries.WalkGaps).Set(UpperParameter, horizon).Set("$at", position.At).Set("$id", position.Id).Set(LimitParameter, (long)limit).Query();

        while (reader.Read())
        {
            rows.Add(new RetentionGapRow(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4)));
        }

        return rows;
    }

    /// <summary>
    /// Removes a gap row when it is closed (a sequence gap) or reads closed or final (a time gap) and overlaps no hold.
    /// </summary>
    /// <param name="row">The gap row</param>
    /// <param name="position">The position, moved to the row</param>
    /// <returns><c>true</c> when the row was removed</returns>
    private bool RemoveGap(RetentionGapRow row, RetentionPosition position)
    {
        position.Set(row.To, row.Id);

        var removable = row.Kind == GapKind.Sequence ? row.State == GapState.Closed : row.State is GapState.Closed or GapState.Final;

        if (removable == false || (HoldAt(row.To) is { } hold && hold.End >= row.From))
        {
            return false;
        }

        Delete(RetentionQueries.DeleteGap, row.Id);

        return true;
    }

    /// <summary>
    /// Reads the oldest buckets of a tier below the horizon.
    /// </summary>
    /// <param name="sql">The statement of the tier</param>
    /// <param name="horizon">The bucket start before which a bucket has expired</param>
    /// <param name="limit">The page size</param>
    /// <returns>The buckets</returns>
    private List<RetentionBucketRow> ReadBuckets(string sql, long horizon, int limit)
    {
        var rows = new List<RetentionBucketRow>();

        using var reader = _session.Statement(sql).Set(UpperParameter, horizon).Set(LimitParameter, (long)limit).Query();

        while (reader.Read())
        {
            rows.Add(new RetentionBucketRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3)));
        }

        return rows;
    }

    /// <summary>
    /// Removes a bucket.
    /// </summary>
    /// <param name="sql">The delete statement of the tier</param>
    /// <param name="row">The bucket</param>
    /// <returns><c>true</c> when the bucket was removed</returns>
    private bool RemoveBucket(string sql, RetentionBucketRow row)
    {
        return _session.Statement(sql).Set("$source", row.Source).Set("$name", row.Name).Set("$labels", row.Labels).Set("$bucket", row.Bucket).Execute() > 0;
    }

    /// <summary>
    /// Reads a page of template hours below the horizon.
    /// </summary>
    /// <param name="position">The position to read after</param>
    /// <param name="horizon">The hour before which statistics have expired</param>
    /// <param name="limit">The page size</param>
    /// <returns>The rows</returns>
    private List<RetentionTemplateHourRow> ReadTemplateHours(RetentionPosition position, long horizon, int limit)
    {
        var rows = new List<RetentionTemplateHourRow>();

        using var reader = _session.Statement(RetentionQueries.WalkTemplateHours).Set(UpperParameter, horizon).Set("$at", position.At).Set("$id", position.Id).Set(LimitParameter, (long)limit).Query();

        while (reader.Read())
        {
            rows.Add(new RetentionTemplateHourRow(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2)));
        }

        return rows;
    }

    /// <summary>
    /// Removes the statistics of an hour that holds no stored line of the source, and the template when it has no hour left.
    /// </summary>
    /// <param name="row">The template hour</param>
    /// <param name="position">The position, moved to the row</param>
    /// <returns><c>true</c> when the template hour was removed</returns>
    private bool RemoveTemplateHour(RetentionTemplateHourRow row, RetentionPosition position)
    {
        position.Set(row.Hour, row.TemplateId);

        if (RetentionSession.Exists(_session.Statement(RetentionQueries.HasLogLine).Set("$source", row.Source).Set("$from", row.Hour).Set("$to", row.Hour + HourNs)))
        {
            return false;
        }

        _session.Statement(RetentionQueries.DeleteTemplateHour).Set("$id", row.TemplateId).Set("$hour", row.Hour).Execute();
        _session.Statement(RetentionQueries.DeleteSourceHour).Set("$source", row.Source).Set("$hour", row.Hour).Execute();
        Delete(RetentionQueries.DeleteEmptyTemplate, row.TemplateId);

        return true;
    }

    /// <summary>
    /// Reads a page of incidents without a note that ended before the horizon.
    /// </summary>
    /// <param name="position">The position to read after</param>
    /// <param name="horizon">The time before which an incident has expired</param>
    /// <param name="limit">The page size</param>
    /// <returns>The incidents as ID and start</returns>
    private List<RetentionHoldRow> ReadIncidents(RetentionPosition position, long horizon, int limit)
    {
        var rows = new List<RetentionHoldRow>();

        using var reader = _session.Statement(RetentionQueries.WalkIncidents).Set(UpperParameter, horizon).Set("$at", position.At).Set("$id", position.Id).Set(LimitParameter, (long)limit).Query();

        while (reader.Read())
        {
            rows.Add(new RetentionHoldRow(reader.GetInt64(0), reader.GetInt64(1), 0));
        }

        return rows;
    }

    /// <summary>
    /// Removes an incident.
    /// </summary>
    /// <param name="row">The incident as ID and start</param>
    /// <param name="position">The position, moved to the row</param>
    /// <returns><c>true</c> when the incident was removed</returns>
    private bool RemoveIncident(RetentionHoldRow row, RetentionPosition position)
    {
        position.Set(row.Start, row.Id);
        Delete(RetentionQueries.DeleteIncident, row.Id);

        return true;
    }

    /// <summary>
    /// Reads a page of holds that ended before the horizon.
    /// </summary>
    /// <param name="position">The position to read after</param>
    /// <param name="horizon">The time before which a hold has expired</param>
    /// <param name="limit">The page size</param>
    /// <returns>The holds as ID, start and end</returns>
    private List<RetentionHoldRow> ReadHolds(RetentionPosition position, long horizon, int limit)
    {
        var rows = new List<RetentionHoldRow>();

        using var reader = _session.Statement(RetentionQueries.WalkHolds).Set(UpperParameter, horizon).Set("$at", position.At).Set("$id", position.Id).Set(LimitParameter, (long)limit).Query();

        while (reader.Read())
        {
            rows.Add(new RetentionHoldRow(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2)));
        }

        return rows;
    }

    /// <summary>
    /// Removes a hold unless a stored incident with a note overlaps it.
    /// </summary>
    /// <param name="row">The hold as ID, start and end</param>
    /// <param name="position">The position, moved to the row</param>
    /// <returns><c>true</c> when the hold was removed</returns>
    private bool RemoveHold(RetentionHoldRow row, RetentionPosition position)
    {
        position.Set(row.Start, row.Id);

        if (RetentionSession.Exists(_session.Statement(RetentionQueries.HasNotedIncident).Set("$start", row.Start).Set("$end", row.End)))
        {
            return false;
        }

        Delete(RetentionQueries.DeleteHold, row.Id);

        return true;
    }

    #endregion // Methods
}