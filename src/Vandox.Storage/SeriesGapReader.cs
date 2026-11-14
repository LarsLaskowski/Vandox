namespace Vandox.Storage;

/// <summary>
/// Reads the gaps of a range: the gap rows of every agent and the agent's gap records, as the plan and record 0216 describe.
/// </summary>
internal static class SeriesGapReader
{
    #region Constants

    private const string RecordKind = "record";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Reads the gaps that overlap the range, at most <see cref="TimeSeriesService.MaxGaps"/>.
    /// </summary>
    /// <param name="reader">The store reads</param>
    /// <param name="range">The range</param>
    /// <param name="cancellationToken">Cancels the reads</param>
    /// <returns>A task that returns the gaps by start and the time up to which the list is complete, <c>null</c> when it is complete</returns>
    internal static async Task<(List<SeriesGap> Gaps, DateTimeOffset? CompleteUntil)> ReadAsync(ITimeSeriesReader reader, SeriesRange range, CancellationToken cancellationToken)
    {
        var rows = await reader.GapsInRangeAsync(new TimeRangeQuery
                                                 {
                                                     From = range.From,
                                                     To = range.End,
                                                     Limit = EventPages.PageSize
                                                 },
                                                 cancellationToken).ConfigureAwait(false);
        var records = await EventPages.ReadAsync(query => reader.GapRecordsAsync(query, cancellationToken),
                                                 SeriesTime.Add(range.From, TimeSpan.FromHours(-1)),
                                                 SeriesTime.Add(range.End, TimeSpan.FromDays(7)),
                                                 row => row.CapturedAt,
                                                 row => row.Id,
                                                 cancellationToken).ConfigureAwait(false);
        var items = rows.Select(row => (Gap: FromRow(row, range), Order: 0, row.Id)).ToList();

        foreach (var row in records.Rows)
        {
            var gap = FromRecord(row, rows, range);

            if (gap is not null)
            {
                items.Add((gap, 1, row.Id));
            }
        }

        var sorted = items.OrderBy(item => item.Gap.From).ThenBy(item => item.Order).ThenBy(item => item.Id).Select(item => item.Gap).ToList();
        DateTimeOffset? bound = records.RanOutAt is null ? null : SeriesTime.Add(records.RanOutAt.Value, TimeSpan.FromDays(-7));

        if (rows.Count >= EventPages.PageSize)
        {
            bound = SeriesTime.Earlier(bound, rows[EventPages.PageSize - 1].From);
        }

        if (sorted.Count > TimeSeriesService.MaxGaps)
        {
            bound = SeriesTime.Earlier(bound, sorted[TimeSeriesService.MaxGaps].From);
            sorted = sorted.Take(TimeSeriesService.MaxGaps).ToList();
        }

        return (sorted, bound);
    }

    /// <summary>
    /// Builds the gap of a gap row, its end clipped to the query time.
    /// </summary>
    /// <param name="row">The row</param>
    /// <param name="range">The range</param>
    /// <returns>The gap</returns>
    private static SeriesGap FromRow(AgentGap row, SeriesRange range)
    {
        return new SeriesGap
               {
                   AgentId = row.AgentId,
                   Kind = row.Kind,
                   Cause = row.Cause,
                   State = row.State,
                   From = row.From,
                   To = row.To < range.Now ? row.To : range.Now
               };
    }

    /// <summary>
    /// Builds the final gap of a gap record, or <c>null</c> when its span does not parse, does not overlap the range, or is already listed as a sequence gap.
    /// </summary>
    /// <param name="row">The record</param>
    /// <param name="rows">The gap rows read</param>
    /// <param name="range">The range</param>
    /// <returns>The gap, or <c>null</c></returns>
    private static SeriesGap? FromRecord(GapRecordRow row, IReadOnlyList<AgentGap> rows, SeriesRange range)
    {
        if (SeriesTime.TryParse(row.SpanFrom, out var from) == false || SeriesTime.TryParse(row.SpanTo, out var to) == false)
        {
            return null;
        }

        to = to < range.Now ? to : range.Now;

        if (to < from || to <= range.From || from >= range.End || IsListed(row, rows))
        {
            return null;
        }

        return new SeriesGap
               {
                   AgentId = row.AgentId,
                   Kind = RecordKind,
                   Cause = row.Cause,
                   State = GapState.Final,
                   Collector = row.Collector,
                   From = from,
                   To = to
               };
    }

    /// <summary>
    /// Tells whether a gap record names sequence numbers inside a sequence gap of its agent and cause.
    /// </summary>
    /// <param name="row">The record</param>
    /// <param name="rows">The gap rows</param>
    /// <returns><c>true</c> when the record is listed as a sequence gap already</returns>
    private static bool IsListed(GapRecordRow row, IReadOnlyList<AgentGap> rows)
    {
        if (row.FirstSeq is null || row.LastSeq is null)
        {
            return false;
        }

        return rows.Any(gap => gap.Kind == GapKind.Sequence
                               && gap.AgentId == row.AgentId
                               && gap.Cause == row.Cause
                               && gap.FirstSeq <= (ulong)Math.Max(row.FirstSeq.Value, 0)
                               && gap.LastSeq >= (ulong)Math.Max(row.LastSeq.Value, 0));
    }

    #endregion // Methods
}