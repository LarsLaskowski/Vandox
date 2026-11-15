namespace Vandox.Storage;

/// <summary>
/// Reads an event projection in pages of at most 1,001 rows keyed on capture time and ID, at most ten pages.
/// </summary>
internal static class EventPages
{
    #region Constants

    /// <summary>
    /// The rows of one page.
    /// </summary>
    internal const int PageSize = 1001;

    /// <summary>
    /// The pages of one read.
    /// </summary>
    internal const int MaxPages = 10;

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Reads the rows of a range page by page.
    /// </summary>
    /// <typeparam name="T">The row type</typeparam>
    /// <param name="read">Reads one page of a query</param>
    /// <param name="from">The start of the range</param>
    /// <param name="to">The end of the range</param>
    /// <param name="capturedAt">Gets the capture time of a row</param>
    /// <param name="id">Gets the ID of a row</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the rows, and the capture time of the last row when ten full pages were read and more may follow</returns>
    internal static async Task<(List<T> Rows, DateTimeOffset? RanOutAt)> ReadAsync<T>(Func<TimeRangeQuery, Task<IReadOnlyList<T>>> read, DateTimeOffset from, DateTimeOffset to, Func<T, DateTimeOffset> capturedAt, Func<T, long> id, CancellationToken cancellationToken)
    {
        var rows = new List<T>();
        var cursor = from;
        var after = 0L;

        for (var page = 0; page < MaxPages && cursor < to; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = await read(new TimeRangeQuery
                                   {
                                       From = cursor,
                                       To = to,
                                       Limit = PageSize,
                                       AfterId = after
                                   }).ConfigureAwait(false);

            rows.AddRange(batch);

            if (batch.Count < PageSize)
            {
                return (rows, null);
            }

            cursor = capturedAt(batch[^1]);
            after = id(batch[^1]);
        }

        return (rows, rows.Count > 0 ? capturedAt(rows[^1]) : null);
    }

    #endregion // Methods
}