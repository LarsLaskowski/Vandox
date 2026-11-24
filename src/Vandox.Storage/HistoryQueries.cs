using System.Text;

using Microsoft.Data.Sqlite;

using Vandox.Core.Model;

namespace Vandox.Storage;

/// <summary>
/// The statements of the history reads. Every statement is a constant; values are bound as parameters.
/// </summary>
internal static class HistoryQueries
{
    #region Constants

    /// <summary>
    /// The operation named in the error of a history read.
    /// </summary>
    internal const string Context = "store: reading history";

    /// <summary>
    /// The first source of <c>metrics</c> with its first name.
    /// </summary>
    internal const string MetricsFirst = "SELECT source, min(name) FROM metrics WHERE source = (SELECT min(source) FROM metrics)";

    /// <summary>
    /// The next name of a source of <c>metrics</c>.
    /// </summary>
    internal const string MetricsNextName = "SELECT min(name) FROM metrics WHERE source = $source AND name > $name";

    /// <summary>
    /// The next source of <c>metrics</c>.
    /// </summary>
    internal const string MetricsNextSource = "SELECT min(source) FROM metrics WHERE source > $source";

    /// <summary>
    /// The first source of <c>metric_rollups_1h</c> with its first name.
    /// </summary>
    internal const string RollupsFirst = "SELECT source, min(name) FROM metric_rollups_1h WHERE source = (SELECT min(source) FROM metric_rollups_1h)";

    /// <summary>
    /// The next name of a source of <c>metric_rollups_1h</c>.
    /// </summary>
    internal const string RollupsNextName = "SELECT min(name) FROM metric_rollups_1h WHERE source = $source AND name > $name";

    /// <summary>
    /// The next source of <c>metric_rollups_1h</c>.
    /// </summary>
    internal const string RollupsNextSource = "SELECT min(source) FROM metric_rollups_1h WHERE source > $source";

    /// <summary>
    /// The stamp read of the side at or before the moment.
    /// </summary>
    internal const string StampAtOrBefore = "SELECT id, captured_at FROM records WHERE kind = $kind AND captured_at <= $at AND captured_at >= $from ORDER BY captured_at DESC, id LIMIT 1";

    /// <summary>
    /// The stamp read of the side after the moment.
    /// </summary>
    internal const string StampAfter = "SELECT id, captured_at FROM records WHERE kind = $kind AND captured_at > $at AND captured_at <= $to ORDER BY captured_at, id LIMIT 1";

    /// <summary>
    /// The stamp read of the side at or before the moment, for one source.
    /// </summary>
    internal const string StampOfSourceAtOrBefore = "SELECT id, captured_at FROM records WHERE kind = $kind AND source = $source AND captured_at <= $at AND captured_at >= $from ORDER BY captured_at DESC, id LIMIT 1";

    /// <summary>
    /// The stamp read of the side after the moment, for one source.
    /// </summary>
    internal const string StampOfSourceAfter = "SELECT id, captured_at FROM records WHERE kind = $kind AND source = $source AND captured_at > $at AND captured_at <= $to ORDER BY captured_at, id LIMIT 1";

    /// <summary>
    /// The latest capture time of a kind and source in a range, start inclusive and end exclusive.
    /// </summary>
    internal const string LatestStampTime = "SELECT max(captured_at) FROM records WHERE kind = $kind AND source = $source AND captured_at >= $from AND captured_at < $to";

    /// <summary>
    /// The lowest ID of a kind and source captured at one time.
    /// </summary>
    internal const string LatestStampId = "SELECT min(id) FROM records WHERE kind = $kind AND source = $source AND captured_at = $at";

    /// <summary>
    /// The first name of a source of <c>metrics</c>.
    /// </summary>
    internal const string MetricsFirstName = "SELECT min(name) FROM metrics WHERE source = $source";

    /// <summary>
    /// The first name of a source of <c>metric_rollups_1h</c>.
    /// </summary>
    internal const string RollupsFirstName = "SELECT min(name) FROM metric_rollups_1h WHERE source = $source";

    #endregion // Constants

    #region Fields

    /// <summary>
    /// The read of the record of a stamp, built from the record columns.
    /// </summary>
    internal static readonly string SnapshotRecord = RecordQueries.SelectColumns(false, false) + " FROM records r WHERE r.id = $id AND r.kind = $kind";

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Checks a metric list read.
    /// </summary>
    /// <param name="limit">The limit</param>
    /// <exception cref="InvalidQueryException">The limit is out of range</exception>
    internal static void ValidateLimit(int limit)
    {
        RecordQueries.Require(limit >= 1 && limit <= StorageLimits.MaxStoredMetrics, $"limit must be 1 to {StorageLimits.MaxStoredMetrics}");
    }

    /// <summary>
    /// Checks a snapshot kind.
    /// </summary>
    /// <param name="kind">The kind</param>
    /// <exception cref="InvalidQueryException">The kind is no snapshot kind</exception>
    internal static void ValidateKind(string? kind)
    {
        RecordQueries.Require(kind is RecordKind.ProcessSnapshot or RecordKind.ConnectionSnapshot, "kind must be a snapshot kind");
    }

    /// <summary>
    /// Checks a stamp read.
    /// </summary>
    /// <param name="query">The query</param>
    /// <exception cref="InvalidQueryException">The query breaks a rule</exception>
    internal static void ValidateStamp(SnapshotQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateKind(query.Kind);
        RecordQueries.Require(query.Window > TimeSpan.Zero && query.Window <= TimeSpan.FromDays(1), "window must be positive and at most one day");
        RecordQueries.Require(StorageTime.InStorableRange(query.At), "at must be within the storable range");
        RecordQueries.Require(query.Source is null || query.Source.Length > 0, "source must not be empty");
    }

    /// <summary>
    /// Checks a latest stamp read.
    /// </summary>
    /// <param name="query">The query</param>
    /// <exception cref="InvalidQueryException">The query breaks a rule</exception>
    internal static void ValidateLatest(LatestSnapshotQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateKind(query.Kind);
        RecordQueries.Require(string.IsNullOrEmpty(query.Source) == false, "source must not be empty");
        RecordQueries.Require(StorageTime.InStorableRange(query.From) && StorageTime.InStorableRange(query.To), "at must be within the storable range");
    }

    /// <summary>
    /// Reads the metrics of both tables merged in ordinal order, each once, at most the limit.
    /// </summary>
    /// <param name="connection">The open reader connection</param>
    /// <param name="limit">The most metrics to return</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the list</returns>
    internal static async Task<StoredMetricList> ReadMetricsAsync(SqliteConnection connection, int limit, CancellationToken cancellationToken)
    {
        var raw = new SkipCursor(MetricsFirst, MetricsFirstName, MetricsNextName, MetricsNextSource);
        var rollups = new SkipCursor(RollupsFirst, RollupsFirstName, RollupsNextName, RollupsNextSource);
        var metrics = new List<SeriesKey>();

        await raw.StartAsync(connection, cancellationToken).ConfigureAwait(false);
        await rollups.StartAsync(connection, cancellationToken).ConfigureAwait(false);

        while (raw.Current is not null || rollups.Current is not null)
        {
            if (metrics.Count == limit)
            {
                return new StoredMetricList
                       {
                           Metrics = metrics,
                           More = true
                       };
            }

            var order = Compare(raw.Current, rollups.Current);
            var next = order <= 0 ? raw.Current : rollups.Current;

            metrics.Add(next!);

            if (order <= 0)
            {
                await raw.AdvanceAsync(connection, cancellationToken).ConfigureAwait(false);
            }

            if (order >= 0)
            {
                await rollups.AdvanceAsync(connection, cancellationToken).ConfigureAwait(false);
            }
        }

        return new StoredMetricList
               {
                   Metrics = metrics
               };
    }

    /// <summary>
    /// Reads the stamp of the snapshot closest to a moment on one side.
    /// </summary>
    /// <param name="connection">The open reader connection</param>
    /// <param name="query">The checked query</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the stamp, or <see langword="null"/> when there is none</returns>
    internal static async Task<SnapshotStamp?> ReadStampAsync(SqliteConnection connection, SnapshotQuery query, CancellationToken cancellationToken)
    {
        var at = StorageTime.ToNanoseconds(query.At);
        var window = query.Window.Ticks * 100;
        var before = query.Side == SnapshotSide.AtOrBefore;
        var parameters = new Dictionary<string, object>
                         {
                             ["$kind"] = query.Kind,
                             ["$at"] = at
                         };

        parameters[before ? "$from" : "$to"] = before ? at - window : at + window;

        if (query.Source is not null)
        {
            parameters["$source"] = query.Source;
        }

        var statement = (before, query.Source is null) switch
                        {
                            (true, true) => StampAtOrBefore,
                            (false, true) => StampAfter,
                            (true, false) => StampOfSourceAtOrBefore,
                            _ => StampOfSourceAfter
                        };

        var rows = await ReaderQuery.ReadAsync(connection,
                                               statement,
                                               parameters,
                                               reader => new SnapshotStamp
                                                         {
                                                             Id = reader.GetInt64(0),
                                                             Kind = query.Kind,
                                                             CapturedAt = StorageTime.FromNanoseconds(reader.GetInt64(1))
                                                         },
                                               Context,
                                               cancellationToken).ConfigureAwait(false);

        return rows.Count > 0 ? rows[0] : null;
    }

    /// <summary>
    /// Reads the stamp of the latest snapshot of a kind and source in a range: the latest capture time, then the lowest ID at it.
    /// </summary>
    /// <param name="connection">The open reader connection</param>
    /// <param name="query">The checked query</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the stamp, or <see langword="null"/> when the range holds none</returns>
    internal static async Task<SnapshotStamp?> ReadLatestStampAsync(SqliteConnection connection, LatestSnapshotQuery query, CancellationToken cancellationToken)
    {
        var from = StorageTime.ToNanoseconds(query.From);
        var to = StorageTime.ToNanoseconds(query.To);

        if (from >= to)
        {
            return null;
        }

        var parameters = new Dictionary<string, object>
                         {
                             ["$kind"] = query.Kind,
                             ["$source"] = query.Source,
                             ["$from"] = from,
                             ["$to"] = to
                         };

        var times = await ReaderQuery.ReadAsync(connection,
                                                LatestStampTime,
                                                parameters,
                                                reader => reader.IsDBNull(0) ? (long?)null : reader.GetInt64(0),
                                                Context,
                                                cancellationToken).ConfigureAwait(false);

        if (times[0] is not { } latest)
        {
            return null;
        }

        parameters.Remove("$from");
        parameters.Remove("$to");
        parameters["$at"] = latest;

        var ids = await ReaderQuery.ReadAsync(connection,
                                              LatestStampId,
                                              parameters,
                                              reader => reader.IsDBNull(0) ? (long?)null : reader.GetInt64(0),
                                              Context,
                                              cancellationToken).ConfigureAwait(false);

        if (ids[0] is not { } id)
        {
            return null;
        }

        return new SnapshotStamp
               {
                   Id = id,
                   Kind = query.Kind,
                   CapturedAt = StorageTime.FromNanoseconds(latest)
               };
    }

    /// <summary>
    /// Reads the record of a stamp.
    /// </summary>
    /// <param name="connection">The open reader connection</param>
    /// <param name="stamp">The checked stamp</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the record, or <see langword="null"/> when it was removed</returns>
    internal static async Task<StoredRecord?> ReadSnapshotAsync(SqliteConnection connection, SnapshotStamp stamp, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, object>
                         {
                             ["$id"] = stamp.Id,
                             ["$kind"] = stamp.Kind
                         };

        var rows = await ReaderQuery.ReadAsync(connection, SnapshotRecord, parameters, RecordQueries.Read, Context, cancellationToken).ConfigureAwait(false);

        return rows.Count > 0 ? rows[0] : null;
    }

    /// <summary>
    /// Compares two metrics by source, then by name, as SQLite orders text (the bytes of the UTF-8 form); an absent metric comes last.
    /// </summary>
    /// <param name="left">The first metric</param>
    /// <param name="right">The second metric</param>
    /// <returns>A negative number when the first comes first, a positive number when the second does, zero when equal</returns>
    private static int Compare(SeriesKey? left, SeriesKey? right)
    {
        if (left is null || right is null)
        {
            return (left is null ? 1 : 0) - (right is null ? 1 : 0);
        }

        var source = CompareText(left.Source, right.Source);

        return source != 0 ? source : CompareText(left.Name, right.Name);
    }

    /// <summary>
    /// Compares two texts by the bytes of their UTF-8 form.
    /// </summary>
    /// <param name="left">The first text</param>
    /// <param name="right">The second text</param>
    /// <returns>The sign of the comparison</returns>
    private static int CompareText(string left, string right)
    {
        return Math.Sign(Encoding.UTF8.GetBytes(left).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(right)));
    }

    #endregion // Methods
}