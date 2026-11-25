using Microsoft.Data.Sqlite;

namespace Vandox.Storage;

/// <summary>
/// Reads the gaps of one collector in a range and merges them into spans (record 0227).
/// </summary>
internal static class CollectorGapReader
{
    #region Constants

    private const string RowsStatement = "SELECT from_at, to_at FROM gaps WHERE to_at > $from AND from_at < $to AND state <> 'closed'";

    private const string RecordsStatement = "SELECT COALESCE(CAST(json_extract(data, '$.collector') AS TEXT), ''), CAST(json_extract(data, '$.from') AS TEXT), CAST(json_extract(data, '$.to') AS TEXT) "
                                            + "FROM records WHERE kind = 'gap' AND captured_at >= $from AND captured_at < $to";

    private const string FromParameter = "$from";

    private const string ToParameter = "$to";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Streams the gap rows and the gap records that concern a collector and overlap the range.
    /// </summary>
    /// <param name="connection">The reader connection</param>
    /// <param name="collector">The collector</param>
    /// <param name="from">The start of the range</param>
    /// <param name="to">The end of the range</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the merged spans and whether a kept record names exactly the collector</returns>
    internal static async Task<(GapSpanSet Gaps, bool Named)> ReadAsync(SqliteConnection connection, string collector, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        var gaps = new GapSpanSet();

        await ReadRowsAsync(connection, gaps, from, to, cancellationToken).ConfigureAwait(false);

        var named = await ReadRecordsAsync(connection, gaps, collector, from, to, cancellationToken).ConfigureAwait(false);

        return (gaps, named);
    }

    /// <summary>
    /// Adds a span clipped to the range, when it overlaps it.
    /// </summary>
    /// <param name="gaps">The spans</param>
    /// <param name="spanFrom">The start of the span</param>
    /// <param name="spanTo">The end of the span</param>
    /// <param name="from">The start of the range</param>
    /// <param name="to">The end of the range</param>
    private static void AddClipped(GapSpanSet gaps, DateTimeOffset spanFrom, DateTimeOffset spanTo, DateTimeOffset from, DateTimeOffset to)
    {
        gaps.Add(spanFrom > from ? spanFrom : from, spanTo < to ? spanTo : to);
    }

    /// <summary>
    /// Reads the span of the current gap record when it parses and overlaps the range.
    /// </summary>
    /// <param name="reader">The reader</param>
    /// <param name="from">The start of the range</param>
    /// <param name="to">The end of the range</param>
    /// <param name="spanFrom">The start of the span</param>
    /// <param name="spanTo">The end of the span</param>
    /// <returns><see langword="true"/> when the span parses and overlaps the range</returns>
    private static bool TryReadSpan(SqliteDataReader reader, DateTimeOffset from, DateTimeOffset to, out DateTimeOffset spanFrom, out DateTimeOffset spanTo)
    {
        spanTo = default;

        if (SeriesTime.TryParse(reader.IsDBNull(1) ? null : reader.GetString(1), out spanFrom) == false || SeriesTime.TryParse(reader.IsDBNull(2) ? null : reader.GetString(2), out spanTo) == false)
        {
            return false;
        }

        return spanTo > from && spanFrom < to && spanTo >= spanFrom;
    }

    /// <summary>
    /// Tells whether a gap of a recorded collector concerns the collector asked for, remembering the answer.
    /// </summary>
    /// <param name="known">The answers so far by recorded collector</param>
    /// <param name="recorded">The collector of the gap record</param>
    /// <param name="collector">The collector asked for</param>
    /// <returns><see langword="true"/> when the gap concerns the collector</returns>
    private static bool Concerns(Dictionary<string, bool> known, string recorded, string collector)
    {
        if (known.TryGetValue(recorded, out var concerned) == false)
        {
            concerned = new SeriesGap
                        {
                            Collector = recorded
                        }.ConcernsCollector(collector);
            known[recorded] = concerned;
        }

        return concerned;
    }

    /// <summary>
    /// Streams the sequence and time gap rows that are not closed and overlap the range; they concern every collector.
    /// </summary>
    /// <param name="connection">The reader connection</param>
    /// <param name="gaps">The spans; the new ones are added</param>
    /// <param name="from">The start of the range</param>
    /// <param name="to">The end of the range</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that completes when the rows are read</returns>
    private static async Task ReadRowsAsync(SqliteConnection connection, GapSpanSet gaps, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();

        command.CommandText = RowsStatement;
        command.Parameters.AddWithValue(FromParameter, StorageTime.ToNanoseconds(from));
        command.Parameters.AddWithValue(ToParameter, StorageTime.ToNanoseconds(to));

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        await using var readerDisposal = reader.ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            AddClipped(gaps, StorageTime.FromNanoseconds(reader.GetInt64(0)), StorageTime.FromNanoseconds(reader.GetInt64(1)), from, to);
        }
    }

    /// <summary>
    /// Streams the gap records captured from an hour before the range to a week after it and keeps those that concern the collector and overlap the range.
    /// </summary>
    /// <param name="connection">The reader connection</param>
    /// <param name="gaps">The spans; the new ones are added</param>
    /// <param name="collector">The collector</param>
    /// <param name="from">The start of the range</param>
    /// <param name="to">The end of the range</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns whether a kept record names exactly the collector</returns>
    private static async Task<bool> ReadRecordsAsync(SqliteConnection connection, GapSpanSet gaps, string collector, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        var named = false;
        var concerns = new Dictionary<string, bool>(StringComparer.Ordinal);

        using var command = connection.CreateCommand();

        command.CommandText = RecordsStatement;
        command.Parameters.AddWithValue(FromParameter, StorageTime.ToNanoseconds(SeriesTime.Add(from, TimeSpan.FromHours(-1))));
        command.Parameters.AddWithValue(ToParameter, StorageTime.ToNanoseconds(SeriesTime.Add(to, TimeSpan.FromDays(7))));

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        await using var readerDisposal = reader.ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var recorded = reader.GetString(0);

            if (TryReadSpan(reader, from, to, out var spanFrom, out var spanTo) == false || Concerns(concerns, recorded, collector) == false)
            {
                continue;
            }

            named |= recorded == collector;

            AddClipped(gaps, spanFrom, spanTo, from, to);
        }

        return named;
    }

    #endregion // Methods
}