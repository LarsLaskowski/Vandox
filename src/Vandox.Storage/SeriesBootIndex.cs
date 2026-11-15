namespace Vandox.Storage;

/// <summary>
/// The boots one query's restart and backup checks need, read once newest first in at most ten pages of 1,001 (record 0215).
/// </summary>
internal sealed class SeriesBootIndex
{
    #region Fields

    private readonly long[] _times;
    private readonly DateTimeOffset? _oldest;

    #endregion // Fields

    #region Constructor

    /// <summary>
    /// Initializes a new instance of the <see cref="SeriesBootIndex"/> class.
    /// </summary>
    /// <param name="times">The capture times of the boots read, in ticks, in ascending order</param>
    /// <param name="oldest">The capture time of the oldest boot read when more boots may lie below it, or <c>null</c> when the read reached the start of the checks</param>
    private SeriesBootIndex(long[] times, DateTimeOffset? oldest)
    {
        _times = times;
        _oldest = oldest;
    }

    #endregion // Constructor

    #region Methods

    /// <summary>
    /// Reads the boots of the intervals the checks ask about: newest first from the query time, at most ten pages of 1,001, down to the earliest interval start.
    /// </summary>
    /// <param name="reader">The store reads</param>
    /// <param name="starts">The first capture times of the intervals the checks ask about</param>
    /// <param name="end">The time below which the read starts: the query time and one tick</param>
    /// <param name="cancellationToken">Cancels the reads</param>
    /// <returns>A task that returns the index; it holds no boot and decides every check when there is no interval</returns>
    internal static async Task<SeriesBootIndex> ReadAsync(ITimeSeriesReader reader, IReadOnlyList<DateTimeOffset> starts, DateTimeOffset end, CancellationToken cancellationToken)
    {
        if (starts.Count == 0)
        {
            return new SeriesBootIndex([], null);
        }

        var from = starts.Min();
        var cursor = end;
        var before = 0L;
        var times = new List<long>();

        for (var page = 0; page < EventPages.MaxPages; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = await reader.BootTimesAsync(new TimeRangeQuery
                                                    {
                                                        From = from,
                                                        To = cursor,
                                                        Limit = EventPages.PageSize,
                                                        BeforeId = before
                                                    },
                                                    cancellationToken).ConfigureAwait(false);

            times.AddRange(batch.Select(boot => boot.CapturedAt.UtcTicks));

            if (batch.Count < EventPages.PageSize)
            {
                return new SeriesBootIndex([.. times.Order()], null);
            }

            cursor = batch[^1].CapturedAt;
            before = batch[^1].Id;
        }

        return new SeriesBootIndex([.. times.Order()], new DateTimeOffset(times[^1], TimeSpan.Zero));
    }

    /// <summary>
    /// Tells how the boot read answers whether a boot lies in an interval.
    /// </summary>
    /// <param name="from">The first capture time of the interval</param>
    /// <param name="to">The end of the interval, exclusive</param>
    /// <returns><c>true</c> when a boot lies in it, <c>false</c> when none does, <c>null</c> when the read did not reach down to the start of the interval and holds no boot in it</returns>
    internal bool? Contains(DateTimeOffset from, DateTimeOffset to)
    {
        var low = 0;
        var high = _times.Length;

        while (low < high)
        {
            var middle = low + ((high - low) / 2);

            if (_times[middle] < from.UtcTicks)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        if (low < _times.Length && _times[low] < to.UtcTicks)
        {
            return true;
        }

        return _oldest is not null && from < _oldest ? null : false;
    }

    #endregion // Methods
}