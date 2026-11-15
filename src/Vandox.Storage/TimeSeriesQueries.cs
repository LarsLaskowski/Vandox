using System.Text;
using System.Text.Json;

using Microsoft.Data.Sqlite;

namespace Vandox.Storage;

/// <summary>
/// The statements and the row logic of the time-series reads: raw values by capture time, rollup steps, gap rows and the projections of
/// service, boot, gap, kernel and backup records. Every statement is a constant; values are bound as parameters.
/// </summary>
internal static class TimeSeriesQueries
{
    #region Constants

    /// <summary>
    /// The operation named in the error of a time-series read.
    /// </summary>
    internal const string Context = "store: reading time series";

    /// <summary>
    /// The largest limit of the gap rows and of the event projections.
    /// </summary>
    internal const int MaxEventLimit = 1001;

    /// <summary>
    /// The largest limit of the service changes, and the largest number of units of a previous-state read.
    /// </summary>
    internal const int MaxServiceLimit = 2001;

    /// <summary>
    /// The largest number of label sets a step read examines.
    /// </summary>
    internal const int MaxLabelSets = 1024;

    /// <summary>
    /// The number of most recent service records a previous-state read examines.
    /// </summary>
    internal const int PreviousScanRows = 10000;

    /// <summary>
    /// Reads the values of one series in a range in order of capture time, so SQLite streams them over the series index and sorts only the labels of equal times.
    /// </summary>
    internal const string RawValues = "SELECT record_id, captured_at, labels, value FROM metrics WHERE source = $source AND name = $name AND captured_at >= $from AND captured_at < $to "
                                      + "ORDER BY captured_at, labels, record_id";

    /// <summary>
    /// Reads the earliest capture time of the metric values above the fold watermark.
    /// </summary>
    internal const string EarliestUnfolded = "SELECT min(captured_at) FROM metrics WHERE record_id > COALESCE((SELECT CAST(value AS INTEGER) FROM meta WHERE key = '" + RetentionQueries.RollupWatermarkKey + "'), 0)";

    /// <summary>
    /// Reads the first label set of a series of the 5-minute rollups.
    /// </summary>
    internal const string FirstLabelsFiveMinutes = "SELECT labels FROM metric_rollups_5m WHERE source = $source AND name = $name AND labels >= '' ORDER BY labels LIMIT 1";

    /// <summary>
    /// Reads the next label set of a series of the 5-minute rollups.
    /// </summary>
    internal const string NextLabelsFiveMinutes = "SELECT labels FROM metric_rollups_5m WHERE source = $source AND name = $name AND labels > $after ORDER BY labels LIMIT 1";

    /// <summary>
    /// Reads the first label set of a series of the hourly rollups.
    /// </summary>
    internal const string FirstLabelsHourly = "SELECT labels FROM metric_rollups_1h WHERE source = $source AND name = $name AND labels >= '' ORDER BY labels LIMIT 1";

    /// <summary>
    /// Reads the next label set of a series of the hourly rollups.
    /// </summary>
    internal const string NextLabelsHourly = "SELECT labels FROM metric_rollups_1h WHERE source = $source AND name = $name AND labels > $after ORDER BY labels LIMIT 1";

    /// <summary>
    /// Reads the 5-minute buckets of one label set in a range merged into steps: the floor of the step index, the extremes, the sum and the count of its buckets, and the last value of its latest bucket
    /// (the fold writes a bucket's last time inside the bucket, so the latest bucket holds the latest last value).
    /// </summary>
    internal const string BucketsFiveMinutes = "SELECT g.step_index, g.min_value, g.max_value, g.sum_value, g.count, r.last_value, r.last_at, r.last_record_id FROM (SELECT (bucket - ((bucket % $step) + $step) % $step) / $step AS step_index, min(min_value) AS min_value, max(max_value) AS max_value, "
                                               + "sum(sum_value) AS sum_value, sum(count) AS count, max(bucket) AS last_bucket FROM metric_rollups_5m WHERE source = $source AND name = $name AND labels = $labels AND bucket >= $from AND bucket < $to GROUP BY step_index) AS g "
                                               + "JOIN metric_rollups_5m AS r ON r.source = $source AND r.name = $name AND r.labels = $labels AND r.bucket = g.last_bucket ORDER BY g.step_index";

    /// <summary>
    /// Reads the hourly buckets of one label set in a range merged into steps, as <see cref="BucketsFiveMinutes"/> does.
    /// </summary>
    internal const string BucketsHourly = "SELECT g.step_index, g.min_value, g.max_value, g.sum_value, g.count, r.last_value, r.last_at, r.last_record_id FROM (SELECT (bucket - ((bucket % $step) + $step) % $step) / $step AS step_index, min(min_value) AS min_value, max(max_value) AS max_value, "
                                          + "sum(sum_value) AS sum_value, sum(count) AS count, max(bucket) AS last_bucket FROM metric_rollups_1h WHERE source = $source AND name = $name AND labels = $labels AND bucket >= $from AND bucket < $to GROUP BY step_index) AS g "
                                          + "JOIN metric_rollups_1h AS r ON r.source = $source AND r.name = $name AND r.labels = $labels AND r.bucket = g.last_bucket ORDER BY g.step_index";

    /// <summary>
    /// Reads the open and final gaps of every agent that overlap a range, by their start.
    /// </summary>
    internal const string GapsInRange = "SELECT gaps.id, gaps.agent_id, gaps.kind, gaps.cause, " + GapReadSql.State + ", gaps.first_seq, gaps.last_seq, gaps.from_at, gaps.to_at, gaps.detected_at, gaps.changed_at FROM gaps "
                                        + "WHERE gaps.to_at > $from AND gaps.from_at < $to AND gaps.state <> 'closed' ORDER BY gaps.from_at, gaps.id LIMIT $limit";

    /// <summary>
    /// Streams the service records of a range in order of capture time with the three members that tell a change: unit, restart counter and active enter time.
    /// </summary>
    internal const string ServiceStream = "SELECT id, CAST(json_extract(data, '$.unit') AS TEXT), COALESCE(json_extract(data, '$.restarts'), 0), CAST(json_extract(data, '$.active_enter_at') AS TEXT) FROM records "
                                          + "WHERE kind = 'service_state' AND captured_at >= $from AND captured_at < $to ORDER BY captured_at, id";

    /// <summary>
    /// Reads the service records of a list of record IDs.
    /// </summary>
    internal const string ServiceRows = "SELECT CAST(json_extract(data, '$.unit') AS TEXT), COALESCE(CAST(json_extract(data, '$.active_state') AS TEXT), ''), CAST(json_extract(data, '$.active_enter_at') AS TEXT), "
                                        + "captured_at, COALESCE(json_extract(data, '$.restarts'), 0), COALESCE(boot_id, ''), id FROM records WHERE kind = 'service_state' AND id IN (SELECT value FROM json_each($ids))";

    /// <summary>
    /// Reads the latest service record before a time of each of the units among the most recent service records.
    /// </summary>
    internal const string PreviousServiceStates = "WITH recent AS (SELECT id, captured_at, COALESCE(boot_id, '') AS boot, data FROM records WHERE kind = 'service_state' AND captured_at < $before "
                                                  + "ORDER BY captured_at DESC, id DESC LIMIT $scan), "
                                                  + "u AS (SELECT CAST(json_extract(data, '$.unit') AS TEXT) AS unit, COALESCE(CAST(json_extract(data, '$.active_state') AS TEXT), '') AS state, "
                                                  + "CAST(json_extract(data, '$.active_enter_at') AS TEXT) AS enter, captured_at, COALESCE(json_extract(data, '$.restarts'), 0) AS restarts, boot, "
                                                  + "ROW_NUMBER() OVER (PARTITION BY json_extract(data, '$.unit') ORDER BY captured_at DESC, id DESC) AS rn FROM recent "
                                                  + "WHERE json_extract(data, '$.unit') IN (SELECT value FROM json_each($units))) "
                                                  + "SELECT unit, state, enter, captured_at, restarts, boot FROM u WHERE rn = 1";

    /// <summary>
    /// Reads the boot events of a range newest first, below a capture time and ID.
    /// </summary>
    internal const string BootTimes = "SELECT id, captured_at, COALESCE(CAST(json_extract(data, '$.boot.boot_id') AS TEXT), '') FROM records "
                                      + "WHERE kind = 'kernel_event' AND json_extract(data, '$.type') = 'boot' AND captured_at >= $from AND (captured_at, id) < ($to, $before) ORDER BY captured_at DESC, id DESC LIMIT $limit";

    /// <summary>
    /// Reads the projected members of the gap records in a range.
    /// </summary>
    internal const string GapRecords = "SELECT id, COALESCE(agent_id, ''), captured_at, COALESCE(CAST(json_extract(data, '$.cause') AS TEXT), ''), COALESCE(CAST(json_extract(data, '$.collector') AS TEXT), ''), "
                                       + "COALESCE(CAST(json_extract(data, '$.from') AS TEXT), ''), COALESCE(CAST(json_extract(data, '$.to') AS TEXT), ''), json_extract(data, '$.first_seq'), json_extract(data, '$.last_seq') "
                                       + "FROM records WHERE kind = 'gap' AND (captured_at, id) > ($from, $after) AND captured_at < $to ORDER BY captured_at, id LIMIT $limit";

    /// <summary>
    /// Reads the projected members of the kernel events in a range, never the message.
    /// </summary>
    internal const string KernelEvents = "SELECT id, captured_at, COALESCE(CAST(json_extract(data, '$.type') AS TEXT), ''), "
                                         + "COALESCE(CAST(COALESCE(json_extract(data, '$.oom_kill.boot_id'), json_extract(data, '$.boot.boot_id')) AS TEXT), ''), "
                                         + "COALESCE(CAST(json_extract(data, '$.oom_kill.victim_command') AS TEXT), ''), json_extract(data, '$.oom_kill.kernel_time_ns'), json_extract(data, '$.oom_kill.victim_pid') "
                                         + "FROM records WHERE kind = 'kernel_event' AND (captured_at, id) > ($from, $after) AND captured_at < $to ORDER BY captured_at, id LIMIT $limit";

    /// <summary>
    /// Reads the projected members of the backup run records in a range.
    /// </summary>
    internal const string BackupRuns = "SELECT id, captured_at, COALESCE(CAST(json_extract(data, '$.phase') AS TEXT), ''), COALESCE(CAST(json_extract(data, '$.boot_id') AS TEXT), ''), "
                                       + "COALESCE(CAST(json_extract(data, '$.started_at') AS TEXT), ''), COALESCE(CAST(json_extract(data, '$.last_seen_at') AS TEXT), ''), "
                                       + "COALESCE(CAST(json_extract(data, '$.gone_by') AS TEXT), ''), COALESCE(CAST(json_extract(data, '$.command') AS TEXT), '') "
                                       + "FROM records WHERE kind = 'backup_run' AND (captured_at, id) > ($from, $after) AND captured_at < $to ORDER BY captured_at, id LIMIT $limit";

    private const string FromParameter = "$from";
    private const string ToParameter = "$to";
    private const int MaxRawTimes = 10000;
    private const int MaxLineLimit = 1024;
    private const long FiveMinutesNs = 300_000_000_000;
    private const long HourNs = 3_600_000_000_000;

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Checks a range read with a limit.
    /// </summary>
    /// <param name="query">The query</param>
    /// <param name="maxLimit">The largest limit of the read</param>
    /// <exception cref="InvalidQueryException">The query breaks a rule</exception>
    internal static void ValidateRange(TimeRangeQuery query, int maxLimit)
    {
        ArgumentNullException.ThrowIfNull(query);
        RecordQueries.Require(StorageTime.InStorableRange(query.From) && StorageTime.InStorableRange(query.To), "from and to must be within the storable range");
        RecordQueries.Require(query.From < query.To, "from must be before to");
        RecordQueries.Require(query.Limit >= 1 && query.Limit <= maxLimit, $"limit must be 1 to {maxLimit}");
        RecordQueries.Require(query.AfterId >= 0, "after id must not be negative");
        RecordQueries.Require(query.BeforeId == 0, "before id is only for boot times");
    }

    /// <summary>
    /// Checks a boot read, which pages newest first: its range may end where it starts, once a page ended at the start.
    /// </summary>
    /// <param name="query">The query</param>
    /// <exception cref="InvalidQueryException">The query breaks a rule</exception>
    internal static void ValidateBoots(TimeRangeQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        RecordQueries.Require(StorageTime.InStorableRange(query.From) && StorageTime.InStorableRange(query.To), "from and to must be within the storable range");
        RecordQueries.Require(query.From <= query.To, "from must not be after to");
        RecordQueries.Require(query.Limit >= 1 && query.Limit <= MaxEventLimit, $"limit must be 1 to {MaxEventLimit}");
        RecordQueries.Require(query.AfterId == 0, "after id is not used by boot times");
        RecordQueries.Require(query.BeforeId >= 0, "before id must not be negative");
    }

    /// <summary>
    /// Checks a raw read.
    /// </summary>
    /// <param name="query">The query</param>
    /// <exception cref="InvalidQueryException">The query breaks a rule</exception>
    internal static void ValidateRaw(RawValueQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateSeries(query.Source, query.Name, query.From, query.To);
        RecordQueries.Require(query.MaxTimes >= 1 && query.MaxTimes <= MaxRawTimes, $"max times must be 1 to {MaxRawTimes}");
        RecordQueries.Require(query.MaxLines >= 1 && query.MaxLines <= MaxLineLimit, $"max lines must be 1 to {MaxLineLimit}");
    }

    /// <summary>
    /// Checks a step read.
    /// </summary>
    /// <param name="query">The query</param>
    /// <exception cref="InvalidQueryException">The query breaks a rule</exception>
    internal static void ValidateSteps(RollupStepQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        RecordQueries.Require(Enum.IsDefined(query.Tier), "unknown tier");
        ValidateSeries(query.Source, query.Name, query.From, query.To);
        RecordQueries.Require(query.MaxLines >= 1 && query.MaxLines <= MaxLineLimit, $"max lines must be 1 to {MaxLineLimit}");

        var bucket = query.Tier == RollupTier.FiveMinutes ? FiveMinutesNs : HourNs;

        RecordQueries.Require(query.Step > TimeSpan.Zero && query.Step <= TimeSpan.FromDays(36500), "step must be positive and at most 36500 days");
        RecordQueries.Require(query.Step.Ticks * 100 % bucket == 0, "step must be a whole number of buckets");
    }

    /// <summary>
    /// Checks the units of a previous-state read.
    /// </summary>
    /// <param name="units">The units</param>
    /// <param name="before">The time before which the records are read</param>
    /// <exception cref="InvalidQueryException">The read breaks a rule</exception>
    internal static void ValidateUnits(IReadOnlyList<string> units, DateTimeOffset before)
    {
        ArgumentNullException.ThrowIfNull(units);
        RecordQueries.Require(StorageTime.InStorableRange(before), "before must be within the storable range");
        RecordQueries.Require(units.Count <= MaxServiceLimit, $"at most {MaxServiceLimit} units");
        RecordQueries.Require(units.All(unit => unit is not null), "units must not be null");
    }

    /// <summary>
    /// Builds the parameters of a range read.
    /// </summary>
    /// <param name="query">The query</param>
    /// <returns>The parameters</returns>
    internal static Dictionary<string, object> RangeParameters(TimeRangeQuery query)
    {
        return new Dictionary<string, object>
               {
                   [FromParameter] = StorageTime.ToNanoseconds(query.From),
                   [ToParameter] = StorageTime.ToNanoseconds(query.To),
                   ["$after"] = query.AfterId,
                   ["$limit"] = (long)query.Limit
               };
    }

    /// <summary>
    /// Builds the parameters of a boot read.
    /// </summary>
    /// <param name="query">The query</param>
    /// <returns>The parameters</returns>
    internal static Dictionary<string, object> BootParameters(TimeRangeQuery query)
    {
        return new Dictionary<string, object>
               {
                   [FromParameter] = StorageTime.ToNanoseconds(query.From),
                   [ToParameter] = StorageTime.ToNanoseconds(query.To),
                   ["$before"] = query.BeforeId,
                   ["$limit"] = (long)query.Limit
               };
    }

    /// <summary>
    /// Builds the parameters of the gap rows and the service changes, which have no paging ID.
    /// </summary>
    /// <param name="query">The query</param>
    /// <returns>The parameters</returns>
    internal static Dictionary<string, object> WindowParameters(TimeRangeQuery query)
    {
        return new Dictionary<string, object>
               {
                   [FromParameter] = StorageTime.ToNanoseconds(query.From),
                   [ToParameter] = StorageTime.ToNanoseconds(query.To),
                   ["$limit"] = (long)query.Limit
               };
    }

    /// <summary>
    /// Builds the parameters of a previous-state read; the units go in as one JSON array.
    /// </summary>
    /// <param name="units">The units</param>
    /// <param name="before">The time before which the records are read</param>
    /// <returns>The parameters</returns>
    internal static Dictionary<string, object> UnitParameters(IReadOnlyList<string> units, DateTimeOffset before)
    {
        return new Dictionary<string, object>
               {
                   ["$before"] = StorageTime.ToNanoseconds(before),
                   ["$scan"] = (long)PreviousScanRows,
                   ["$units"] = JsonSerializer.Serialize(units)
               };
    }

    /// <summary>
    /// Maps the current row of <see cref="GapsInRange"/>.
    /// </summary>
    /// <param name="reader">The reader</param>
    /// <returns>The gap</returns>
    internal static AgentGap ReadGap(SqliteDataReader reader)
    {
        return new AgentGap
               {
                   Id = reader.GetInt64(0),
                   AgentId = reader.GetString(1),
                   Kind = reader.GetString(2),
                   Cause = reader.GetString(3),
                   State = reader.GetString(4),
                   FirstSeq = reader.IsDBNull(5) ? null : (ulong)reader.GetInt64(5),
                   LastSeq = reader.IsDBNull(6) ? null : (ulong)reader.GetInt64(6),
                   From = StorageTime.FromNanoseconds(reader.GetInt64(7)),
                   To = StorageTime.FromNanoseconds(reader.GetInt64(8)),
                   DetectedAt = StorageTime.FromNanoseconds(reader.GetInt64(9)),
                   ChangedAt = StorageTime.FromNanoseconds(reader.GetInt64(10))
               };
    }

    /// <summary>
    /// Reads the first service record of every unit in a range and every record whose restart counter or active enter time differs from its predecessor's, in order of capture time.
    /// The records are streamed in order of capture time once, the last one of each unit is kept, and only the rows found are read in full, with their predecessors.
    /// </summary>
    /// <param name="connection">The leased reader connection</param>
    /// <param name="query">The validated query</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the rows, at most the limit</returns>
    internal static async Task<IReadOnlyList<ServiceStateRow>> ReadServiceChangesAsync(SqliteConnection connection, TimeRangeQuery query, CancellationToken cancellationToken)
    {
        var found = new List<(long Id, long PreviousId)>();
        var last = new Dictionary<string, (long Id, long Restarts, string? Enter)>(StringComparer.Ordinal);

        using (var command = connection.CreateCommand())
        {
            command.CommandText = ServiceStream;
            command.Parameters.AddWithValue(FromParameter, StorageTime.ToNanoseconds(query.From));
            command.Parameters.AddWithValue(ToParameter, StorageTime.ToNanoseconds(query.To));

            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            await using var readerDisposal = reader.ConfigureAwait(false);

            while (found.Count < query.Limit && await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.IsDBNull(1))
                {
                    continue;
                }

                var unit = reader.GetString(1);
                var id = reader.GetInt64(0);
                var restarts = reader.GetInt64(2);
                var enter = reader.IsDBNull(3) ? null : reader.GetString(3);

                if (last.TryGetValue(unit, out var before) == false)
                {
                    found.Add((id, 0));
                }
                else if (before.Restarts != restarts || string.Equals(before.Enter, enter, StringComparison.Ordinal) == false)
                {
                    found.Add((id, before.Id));
                }

                last[unit] = (id, restarts, enter);
            }
        }

        if (found.Count == 0)
        {
            return [];
        }

        var ids = found.Select(item => item.Id).Concat(found.Where(item => item.PreviousId != 0).Select(item => item.PreviousId)).Distinct().ToList();
        var rows = await ReaderQuery.ReadAsync(connection,
                                               ServiceRows,
                                               new Dictionary<string, object>
                                               {
                                                   ["$ids"] = JsonSerializer.Serialize(ids)
                                               },
                                               reader => (Id: reader.GetInt64(6), Row: ReadServiceState(reader, null)),
                                               Context,
                                               cancellationToken).ConfigureAwait(false);
        var byId = rows.ToDictionary(item => item.Id, item => item.Row);

        return found.Where(item => byId.ContainsKey(item.Id)).Select(item => WithPrevious(byId[item.Id], item.PreviousId != 0 ? byId.GetValueOrDefault(item.PreviousId) : null)).ToList();
    }

    /// <summary>
    /// Maps the first six columns of a service row.
    /// </summary>
    /// <param name="reader">The reader</param>
    /// <param name="previous">The predecessor, or <c>null</c></param>
    /// <returns>The row</returns>
    internal static ServiceStateRow ReadServiceState(SqliteDataReader reader, ServiceStateRow? previous)
    {
        return new ServiceStateRow
               {
                   Unit = reader.GetString(0),
                   ActiveState = reader.GetString(1),
                   ActiveEnterAt = reader.IsDBNull(2) ? null : reader.GetString(2),
                   CapturedAt = StorageTime.FromNanoseconds(reader.GetInt64(3)),
                   Restarts = reader.GetInt64(4),
                   BootId = reader.GetString(5),
                   Previous = previous
               };
    }

    /// <summary>
    /// Maps the current row of <see cref="BootTimes"/>.
    /// </summary>
    /// <param name="reader">The reader</param>
    /// <returns>The boot</returns>
    internal static BootTime ReadBoot(SqliteDataReader reader)
    {
        return new BootTime
               {
                   Id = reader.GetInt64(0),
                   CapturedAt = StorageTime.FromNanoseconds(reader.GetInt64(1)),
                   BootId = reader.GetString(2)
               };
    }

    /// <summary>
    /// Maps the current row of <see cref="GapRecords"/>.
    /// </summary>
    /// <param name="reader">The reader</param>
    /// <returns>The row</returns>
    internal static GapRecordRow ReadGapRecord(SqliteDataReader reader)
    {
        return new GapRecordRow
               {
                   Id = reader.GetInt64(0),
                   AgentId = reader.GetString(1),
                   CapturedAt = StorageTime.FromNanoseconds(reader.GetInt64(2)),
                   Cause = reader.GetString(3),
                   Collector = reader.GetString(4),
                   SpanFrom = reader.GetString(5),
                   SpanTo = reader.GetString(6),
                   FirstSeq = reader.IsDBNull(7) ? null : reader.GetInt64(7),
                   LastSeq = reader.IsDBNull(8) ? null : reader.GetInt64(8)
               };
    }

    /// <summary>
    /// Maps the current row of <see cref="KernelEvents"/>.
    /// </summary>
    /// <param name="reader">The reader</param>
    /// <returns>The row</returns>
    internal static KernelEventRow ReadKernelEvent(SqliteDataReader reader)
    {
        return new KernelEventRow
               {
                   Id = reader.GetInt64(0),
                   CapturedAt = StorageTime.FromNanoseconds(reader.GetInt64(1)),
                   Type = reader.GetString(2),
                   BootId = reader.GetString(3),
                   VictimCommand = reader.GetString(4),
                   KernelTimeNs = reader.IsDBNull(5) ? null : reader.GetInt64(5),
                   VictimPid = reader.IsDBNull(6) ? null : reader.GetInt64(6)
               };
    }

    /// <summary>
    /// Maps the current row of <see cref="BackupRuns"/>.
    /// </summary>
    /// <param name="reader">The reader</param>
    /// <returns>The row</returns>
    internal static BackupRunRow ReadBackupRun(SqliteDataReader reader)
    {
        return new BackupRunRow
               {
                   Id = reader.GetInt64(0),
                   CapturedAt = StorageTime.FromNanoseconds(reader.GetInt64(1)),
                   Phase = reader.GetString(2),
                   BootId = reader.GetString(3),
                   StartedAt = reader.GetString(4),
                   LastSeenAt = reader.GetString(5),
                   GoneBy = reader.GetString(6),
                   Command = reader.GetString(7)
               };
    }

    /// <summary>
    /// Maps the single column of the earliest unfolded read.
    /// </summary>
    /// <param name="reader">The reader</param>
    /// <returns>The capture time, or <c>null</c> when every value is folded</returns>
    internal static DateTimeOffset? ReadEarliest(SqliteDataReader reader)
    {
        return reader.IsDBNull(0) ? null : StorageTime.FromNanoseconds(reader.GetInt64(0));
    }

    /// <summary>
    /// Reads the values of one series in a range into lines, merging the samples of one line at one time, and stops after one distinct time more than the limit.
    /// </summary>
    /// <param name="connection">The leased reader connection</param>
    /// <param name="query">The validated query</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the lines, in the UTF-8 byte order of their labels</returns>
    internal static async Task<LinePage> ReadRawAsync(SqliteConnection connection, RawValueQuery query, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();

        command.CommandText = RawValues;
        command.Parameters.AddWithValue("$source", query.Source);
        command.Parameters.AddWithValue("$name", query.Name);
        command.Parameters.AddWithValue(FromParameter, StorageTime.ToNanoseconds(query.From));
        command.Parameters.AddWithValue(ToParameter, StorageTime.ToNanoseconds(query.To));

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        await using var readerDisposal = reader.ConfigureAwait(false);

        var lines = new Dictionary<string, List<LinePoint>>(StringComparer.Ordinal);
        var group = new Dictionary<string, (double Min, double Max, double Sum, long Count, double Last, long LastId)>(StringComparer.Ordinal);
        var times = 0;
        var current = 0L;
        var exceeded = false;

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var at = reader.GetInt64(1);

            if (times == 0 || at != current)
            {
                FlushGroup(current, group, lines);
                times++;

                if (times > query.MaxTimes)
                {
                    exceeded = true;

                    break;
                }

                current = at;
            }

            var labels = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);

            if (labels == "{}")
            {
                labels = string.Empty;
            }

            if (Admit(labels, lines, group, query.MaxLines))
            {
                Accumulate(group, labels, reader.GetInt64(0), reader.GetDouble(3));
            }
        }

        if (exceeded == false)
        {
            FlushGroup(current, group, lines);
        }

        var ordered = lines.Where(pair => pair.Value.Count > 0).OrderBy(pair => pair.Key, Comparer<string>.Create(CompareUtf8)).ToList();

        return new LinePage
               {
                   Lines = ordered.Take(query.MaxLines).Select(pair => new MetricLine
                                                                       {
                                                                           Labels = pair.Key,
                                                                           Points = pair.Value
                                                                       }).ToList(),
                   LinesCut = ordered.Count > query.MaxLines,
                   TimesExceeded = exceeded
               };
    }

    /// <summary>
    /// Reads the rollup buckets of the label sets of a series into lines of steps.
    /// </summary>
    /// <param name="connection">The leased reader connection</param>
    /// <param name="query">The validated query</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the lines in the UTF-8 byte order of their labels</returns>
    internal static async Task<LinePage> ReadStepsAsync(SqliteConnection connection, RollupStepQuery query, CancellationToken cancellationToken)
    {
        var fiveMinutes = query.Tier == RollupTier.FiveMinutes;
        var stepNs = query.Step.Ticks * 100;
        var from = StepStart(FloorDiv(StorageTime.ToNanoseconds(query.From), stepNs), stepNs);
        var to = CeilingEnd(StorageTime.ToNanoseconds(query.To), stepNs);
        var lines = new List<MetricLine>();
        var cut = false;
        var examined = 0;
        var after = await NextLabelsAsync(connection, query, fiveMinutes ? FirstLabelsFiveMinutes : FirstLabelsHourly, null, cancellationToken).ConfigureAwait(false);

        using var buckets = connection.CreateCommand();

#pragma warning disable CA2100 // constant statements, values bound as parameters
        buckets.CommandText = fiveMinutes ? BucketsFiveMinutes : BucketsHourly;
#pragma warning restore CA2100
        buckets.Parameters.AddWithValue("$source", query.Source);
        buckets.Parameters.AddWithValue("$name", query.Name);
        buckets.Parameters.AddWithValue(FromParameter, from);
        buckets.Parameters.AddWithValue(ToParameter, to);
        buckets.Parameters.AddWithValue("$step", stepNs);

        var labelsParameter = buckets.Parameters.Add("$labels", SqliteType.Text);

        while (after is not null && examined < MaxLabelSets && cut == false)
        {
            examined++;
            labelsParameter.Value = after;

            var points = await ReadLabelSetAsync(buckets, stepNs, cancellationToken).ConfigureAwait(false);

            if (points.Count > 0)
            {
                lines.Add(new MetricLine
                          {
                              Labels = after,
                              Points = points
                          });
                cut = lines.Count > query.MaxLines;
            }

            after = await NextLabelsAsync(connection, query, fiveMinutes ? NextLabelsFiveMinutes : NextLabelsHourly, after, cancellationToken).ConfigureAwait(false);
        }

        cut = cut || (after is not null && examined >= MaxLabelSets);

        return new LinePage
               {
                   Lines = lines.Take(query.MaxLines).ToList(),
                   LinesCut = cut
               };
    }

    /// <summary>
    /// Compares two texts by their UTF-8 bytes, the order of SQLite's binary collation.
    /// </summary>
    /// <param name="left">The first text</param>
    /// <param name="right">The second text</param>
    /// <returns>A negative number, zero or a positive number</returns>
    internal static int CompareUtf8(string? left, string? right)
    {
        return Encoding.UTF8.GetBytes(left ?? string.Empty).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(right ?? string.Empty));
    }

    /// <summary>
    /// Checks the series and the range of a line read.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="name">The metric name</param>
    /// <param name="from">The start of the range</param>
    /// <param name="to">The end of the range</param>
    private static void ValidateSeries(string source, string name, DateTimeOffset from, DateTimeOffset to)
    {
        RecordQueries.Require(string.IsNullOrEmpty(source) == false && string.IsNullOrEmpty(name) == false, "source and name are required");
        RecordQueries.Require(StorageTime.InStorableRange(from) && StorageTime.InStorableRange(to), "from and to must be within the storable range");
        RecordQueries.Require(from < to, "from must be before to");
    }

    /// <summary>
    /// Returns a service row with its predecessor.
    /// </summary>
    /// <param name="row">The row</param>
    /// <param name="previous">The predecessor, or <c>null</c></param>
    /// <returns>A copy of the row that holds the predecessor</returns>
    private static ServiceStateRow WithPrevious(ServiceStateRow row, ServiceStateRow? previous)
    {
        return new ServiceStateRow
               {
                   Unit = row.Unit,
                   ActiveState = row.ActiveState,
                   ActiveEnterAt = row.ActiveEnterAt,
                   CapturedAt = row.CapturedAt,
                   Restarts = row.Restarts,
                   BootId = row.BootId,
                   Previous = previous
               };
    }

    /// <summary>
    /// Returns the start of a step, clamped to the range of a nanosecond count.
    /// </summary>
    /// <param name="index">The index of the step since the epoch</param>
    /// <param name="stepNs">The step in nanoseconds</param>
    /// <returns>The start of the step</returns>
    private static long StepStart(long index, long stepNs)
    {
        var start = (Int128)index * stepNs;

        if (start < long.MinValue)
        {
            return long.MinValue;
        }

        return start > long.MaxValue ? long.MaxValue : (long)start;
    }

    /// <summary>
    /// Divides rounding towards negative infinity.
    /// </summary>
    /// <param name="value">The dividend</param>
    /// <param name="divisor">The positive divisor</param>
    /// <returns>The quotient</returns>
    private static long FloorDiv(long value, long divisor)
    {
        var quotient = value / divisor;

        return value % divisor < 0 ? quotient - 1 : quotient;
    }

    /// <summary>
    /// Returns the end of the last step that overlaps a range ending at a time, clamped to the largest nanosecond count.
    /// </summary>
    /// <param name="to">The end of the range in nanoseconds</param>
    /// <param name="stepNs">The step in nanoseconds</param>
    /// <returns>The end of the last step</returns>
    private static long CeilingEnd(long to, long stepNs)
    {
        var steps = (Int128)FloorDiv(to - 1, stepNs) + 1;
        var end = steps * stepNs;

        return end > long.MaxValue ? long.MaxValue : (long)end;
    }

    /// <summary>
    /// Reads the label set after another one, or the first one.
    /// </summary>
    /// <param name="connection">The reader connection</param>
    /// <param name="query">The query</param>
    /// <param name="sql">The statement</param>
    /// <param name="after">The label set before, or <c>null</c> for the first</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the label set, or <c>null</c> when there is none</returns>
    private static async Task<string?> NextLabelsAsync(SqliteConnection connection, RollupStepQuery query, string sql, string? after, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, object>
                         {
                             ["$source"] = query.Source,
                             ["$name"] = query.Name
                         };

        if (after is not null)
        {
            parameters["$after"] = after;
        }

        var rows = await ReaderQuery.ReadAsync(connection, sql, parameters, reader => reader.GetString(0), Context, cancellationToken).ConfigureAwait(false);

        return rows.Count > 0 ? rows[0] : null;
    }

    /// <summary>
    /// Reads the steps of one label set.
    /// </summary>
    /// <param name="command">The bucket statement with every parameter set but the label set</param>
    /// <param name="step">The step in nanoseconds</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the points, one per step with a bucket</returns>
    private static async Task<List<LinePoint>> ReadLabelSetAsync(SqliteCommand command, long step, CancellationToken cancellationToken)
    {
        var points = new List<LinePoint>();
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        await using var readerDisposal = reader.ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var count = reader.GetInt64(4);

            points.Add(new LinePoint
                       {
                           At = StorageTime.FromNanoseconds(StepStart(reader.GetInt64(0), step)),
                           LastAt = StorageTime.FromNanoseconds(reader.GetInt64(6)),
                           Minimum = reader.GetDouble(1),
                           Average = ClampSum(reader.GetDouble(3)) / count,
                           Maximum = reader.GetDouble(2),
                           Last = reader.GetDouble(5),
                           Count = count
                       });
        }

        return points;
    }

    /// <summary>
    /// Clamps a sum to the finite range, as the fold does.
    /// </summary>
    /// <param name="sum">The sum</param>
    /// <returns>The clamped sum</returns>
    private static double ClampSum(double sum)
    {
        return Math.Min(Math.Max(sum, -double.MaxValue), double.MaxValue);
    }

    /// <summary>
    /// Decides whether the row of a label set is read: the label sets with the smallest labels are kept, one more than the limit.
    /// </summary>
    /// <param name="labels">The label set of the row</param>
    /// <param name="lines">The admitted lines</param>
    /// <param name="group">The samples of the current time</param>
    /// <param name="maxLines">The line limit</param>
    /// <returns><c>true</c> when the row belongs to an admitted line</returns>
    private static bool Admit(string labels, Dictionary<string, List<LinePoint>> lines, Dictionary<string, (double Min, double Max, double Sum, long Count, double Last, long LastId)> group, int maxLines)
    {
        if (lines.ContainsKey(labels))
        {
            return true;
        }

        if (lines.Count > maxLines)
        {
            var largest = lines.Keys.Max(Comparer<string>.Create(CompareUtf8))!;

            if (CompareUtf8(labels, largest) > 0)
            {
                return false;
            }

            lines.Remove(largest);
            group.Remove(largest);
        }

        lines[labels] = [];

        return true;
    }

    /// <summary>
    /// Adds a sample to the samples of the current time.
    /// </summary>
    /// <param name="group">The samples of the current time by label set</param>
    /// <param name="labels">The label set</param>
    /// <param name="id">The record ID</param>
    /// <param name="value">The value</param>
    private static void Accumulate(Dictionary<string, (double Min, double Max, double Sum, long Count, double Last, long LastId)> group, string labels, long id, double value)
    {
        if (group.TryGetValue(labels, out var sample) == false)
        {
            group[labels] = (value, value, value, 1, value, id);

            return;
        }

        var newer = id > sample.LastId;

        group[labels] = (Math.Min(sample.Min, value), Math.Max(sample.Max, value), ClampSum(sample.Sum + value), sample.Count + 1, newer ? value : sample.Last, newer ? id : sample.LastId);
    }

    /// <summary>
    /// Moves the samples of the current time into the points of their lines.
    /// </summary>
    /// <param name="time">The time in nanoseconds</param>
    /// <param name="group">The samples of the current time by label set; emptied</param>
    /// <param name="lines">The lines</param>
    private static void FlushGroup(long time, Dictionary<string, (double Min, double Max, double Sum, long Count, double Last, long LastId)> group, Dictionary<string, List<LinePoint>> lines)
    {
        if (group.Count == 0)
        {
            return;
        }

        var at = StorageTime.FromNanoseconds(time);

        foreach (var (labels, sample) in group)
        {
            lines[labels].Add(new LinePoint
                              {
                                  At = at,
                                  LastAt = at,
                                  Minimum = sample.Min,
                                  Average = sample.Sum / sample.Count,
                                  Maximum = sample.Max,
                                  Last = sample.Last,
                                  Count = sample.Count
                              });
        }

        group.Clear();
    }

    #endregion // Methods
}