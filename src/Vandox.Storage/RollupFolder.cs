using System.Text;

namespace Vandox.Storage;

/// <summary>
/// Folds the metric values added since the last step into the 5-minute and hourly buckets: it reads a bounded step of rows by
/// record ID, merges each into the bucket of its series, and moves the watermark.
/// </summary>
internal static class RollupFolder
{
    #region Constants

    private const long FiveMinutesNs = 300_000_000_000;

    private const long HourNs = 3_600_000_000_000;

    private const string EmptyLabels = "{}";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Runs one fold step in the open transaction of the session.
    /// </summary>
    /// <param name="session">The session of the step</param>
    /// <param name="policy">The retention policy: buckets past the period of their tier are not created</param>
    /// <param name="now">The current time</param>
    /// <param name="cancellationToken">Cancels the step between rows</param>
    /// <returns>The result of the step</returns>
    internal static RollupStepResult Fold(RetentionSession session, RetentionPolicy policy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var after = session.ReadNumber(RetentionQueries.RollupWatermarkKey);
        var fiveMinutes = new Dictionary<RollupBucketKey, RollupBucketTotals>();
        var hours = new Dictionary<RollupBucketKey, RollupBucketTotals>();
        var fiveHorizon = HorizonOf(policy.FiveMinuteRollups, now);
        var hourHorizon = HorizonOf(policy.HourlyRollups, now);
        var rows = 0;
        var lastId = after;
        var labelBytes = 0L;
        var budgetSpent = false;

        using (var reader = session.Statement(RetentionQueries.ReadMetrics).Set("$after", after).Set("$limit", (long)StorageLimits.RollupStepRows).Query())
        {
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var id = reader.GetInt64(0);
                var raw = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
                var labels = raw == EmptyLabels ? string.Empty : raw;
                var capturedAt = reader.GetInt64(4);
                var value = reader.GetDouble(5);
                var source = reader.GetString(1);
                var name = reader.GetString(2);

                Add(fiveMinutes, fiveHorizon, new RollupBucketKey(source, name, labels, Floor(capturedAt, FiveMinutesNs)), id, capturedAt, value);
                Add(hours, hourHorizon, new RollupBucketKey(source, name, labels, Floor(capturedAt, HourNs)), id, capturedAt, value);

                lastId = id;
                rows++;
                labelBytes += Encoding.UTF8.GetByteCount(raw);

                if (labelBytes > StorageLimits.RollupStepLabelBytes)
                {
                    budgetSpent = true;

                    break;
                }
            }
        }

        if (rows == 0)
        {
            return new RollupStepResult();
        }

        Write(session, RetentionQueries.UpsertFiveMinutes, fiveMinutes, cancellationToken);
        Write(session, RetentionQueries.UpsertHourly, hours, cancellationToken);
        session.WriteNumber(RetentionQueries.RollupWatermarkKey, lastId);

        return new RollupStepResult
               {
                   Values = rows,
                   Buckets = fiveMinutes.Count + hours.Count,
                   More = budgetSpent || rows >= StorageLimits.RollupStepRows
               };
    }

    /// <summary>
    /// Gets the first bucket start a tier keeps.
    /// </summary>
    /// <param name="period">The period of the tier, or <c>null</c> for unlimited</param>
    /// <param name="now">The current time</param>
    /// <returns>The nanoseconds, or the smallest number when the tier keeps everything</returns>
    private static long HorizonOf(TimeSpan? period, DateTimeOffset now)
    {
        return period is null ? long.MinValue : StorageTime.ToNanoseconds(now - period.Value);
    }

    /// <summary>
    /// Gets the start of the bucket that holds an instant.
    /// </summary>
    /// <param name="nanoseconds">The instant</param>
    /// <param name="width">The width of the bucket</param>
    /// <returns>The start of the bucket</returns>
    private static long Floor(long nanoseconds, long width)
    {
        var remainder = nanoseconds % width;

        return nanoseconds - (remainder < 0 ? remainder + width : remainder);
    }

    /// <summary>
    /// Adds a value to its bucket unless the bucket is past the period of its tier.
    /// </summary>
    /// <param name="buckets">The buckets of the tier</param>
    /// <param name="horizon">The first bucket start the tier keeps</param>
    /// <param name="key">The bucket</param>
    /// <param name="id">The record ID of the value</param>
    /// <param name="capturedAt">The capture time of the value</param>
    /// <param name="value">The value</param>
    private static void Add(Dictionary<RollupBucketKey, RollupBucketTotals> buckets, long horizon, RollupBucketKey key, long id, long capturedAt, double value)
    {
        if (key.Bucket < horizon)
        {
            return;
        }

        if (buckets.TryGetValue(key, out var totals))
        {
            totals.Add(id, capturedAt, value);
        }
        else
        {
            buckets[key] = new RollupBucketTotals(id, capturedAt, value);
        }
    }

    /// <summary>
    /// Merges the buckets of one tier into its table.
    /// </summary>
    /// <param name="session">The session of the step</param>
    /// <param name="sql">The merge statement of the tier</param>
    /// <param name="buckets">The buckets</param>
    /// <param name="cancellationToken">Cancels the write</param>
    private static void Write(RetentionSession session, string sql, Dictionary<RollupBucketKey, RollupBucketTotals> buckets, CancellationToken cancellationToken)
    {
        var statement = session.Statement(sql);

        foreach (var (key, totals) in buckets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            statement.Set("$source", key.Source)
                     .Set("$name", key.Name)
                     .Set("$labels", key.Labels)
                     .Set("$bucket", key.Bucket)
                     .Set("$min", totals.Minimum)
                     .Set("$max", totals.Maximum)
                     .Set("$sum", totals.Sum)
                     .Set("$low", -double.MaxValue)
                     .Set("$high", double.MaxValue)
                     .Set("$count", totals.Count)
                     .Set("$last", totals.Last)
                     .Set("$lastAt", totals.LastAt)
                     .Set("$lastId", totals.LastId)
                     .Execute();
        }
    }

    #endregion // Methods
}