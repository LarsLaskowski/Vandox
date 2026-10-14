using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.Backend.Incidents;

/// <summary>
/// Reads stored records page by page.
/// </summary>
internal static class RecordPager
{
    #region Methods

    /// <summary>
    /// Reads a range page by page, by capture time and record ID. A full page must lead to a position after the previous one.
    /// </summary>
    /// <param name="records">The reader of stored records</param>
    /// <param name="range">The kind and the range</param>
    /// <param name="limit">The page size</param>
    /// <param name="handle">Called for every record</param>
    /// <param name="cancellationToken">The cancellation token</param>
    /// <returns>A task that completes when the range is read</returns>
    /// <exception cref="StoreException">The store failed, or a page of records did not advance</exception>
    /// <exception cref="OperationCanceledException">The run was cancelled</exception>
    internal static async Task PageAsync(IRecordReader records, RecordQuery range, int limit, Func<StoredRecord, Task> handle, CancellationToken cancellationToken)
    {
        await PageAsync(records, range, limit, int.MaxValue, handle, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a range page by page like the other overload, with the source and the name of the range in every page query, and
    /// stops after a number of records.
    /// </summary>
    /// <param name="records">The reader of stored records</param>
    /// <param name="range">The kind, the source, the name and the range</param>
    /// <param name="limit">The page size</param>
    /// <param name="maxRecords">The most records handled</param>
    /// <param name="handle">Called for every record</param>
    /// <param name="cancellationToken">The cancellation token</param>
    /// <returns>A task that returns the number of records handled</returns>
    /// <exception cref="StoreException">The store failed, or a page of records did not advance</exception>
    /// <exception cref="OperationCanceledException">The run was cancelled</exception>
    internal static async Task<int> PageAsync(IRecordReader records, RecordQuery range, int limit, int maxRecords, Func<StoredRecord, Task> handle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(range);
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRecords, 1);

        var from = range.From;
        var after = 0L;
        var handled = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = await records.RecordsAsync(new RecordQuery
                                                  {
                                                      Kind = range.Kind,
                                                      Source = range.Source,
                                                      Name = range.Name,
                                                      From = from,
                                                      To = range.To,
                                                      Limit = limit,
                                                      AfterId = after
                                                  },
                                                  cancellationToken)
                                    .ConfigureAwait(false);

            foreach (var record in page)
            {
                await handle(record).ConfigureAwait(false);

                handled++;

                if (handled >= maxRecords)
                {
                    return handled;
                }
            }

            if (page.Count < limit)
            {
                return handled;
            }

            var last = page[^1];

            if (last.Record.CapturedAt < from || (last.Record.CapturedAt == from && last.Id <= after))
            {
                throw new StoreException("store: reading records: page did not advance");
            }

            from = last.Record.CapturedAt;
            after = last.Id;
        }
    }

    #endregion // Methods
}