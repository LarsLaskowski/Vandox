using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// A record reader that answers the first query with a full page of identical log lines and every later query with the same page again, as a store whose paging does not advance would.
/// </summary>
internal sealed class RepeatingRecordReader : IRecordReader
{
    #region Fields

    private IReadOnlyList<StoredRecord>? _page;

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets the number of queries that were asked.
    /// </summary>
    internal int Calls { get; private set; }

    #endregion // Properties

    #region IRecordReader

    /// <inheritdoc />
    public Task<IReadOnlyList<StoredRecord>> RecordsAsync(RecordQuery query, CancellationToken cancellationToken)
    {
        Calls++;
        _page ??= Enumerable.Range(1, query.Limit)
                            .Select(id => new StoredRecord
                                          {
                                              Id = id,
                                              Record = new DataRecord
                                                       {
                                                           Origin = RecordOrigin.Import,
                                                           Source = "syslog",
                                                           CapturedAt = new DateTimeOffset(2026, 10, 2, 5, 0, 0, TimeSpan.Zero),
                                                           Data = new LogLine
                                                                  {
                                                                      Log = "syslog",
                                                                      Host = "web-1",
                                                                      Program = "cron",
                                                                      Message = "job finished"
                                                                  }
                                                       }
                                          })
                            .ToList();

        return Task.FromResult(_page);
    }

    #endregion // IRecordReader
}