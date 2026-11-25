namespace Vandox.Storage;

/// <summary>
/// The merged gap spans of one collector in a range: sorted by start, overlapping and touching spans merged, at most <see cref="MaxSpans"/>.
/// </summary>
internal sealed class GapSpanSet
{
    #region Constants

    /// <summary>
    /// The most spans kept.
    /// </summary>
    internal const int MaxSpans = 1000;

    #endregion // Constants

    #region Fields

    private readonly List<(DateTimeOffset From, DateTimeOffset To)> _spans = [];

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets the merged spans by start.
    /// </summary>
    internal IReadOnlyList<(DateTimeOffset From, DateTimeOffset To)> Spans => _spans;

    /// <summary>
    /// Gets the time up to which the spans are complete, <c>null</c> when no span was dropped.
    /// </summary>
    internal DateTimeOffset? CompleteUntil { get; private set; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Adds a span, merging it with the spans it overlaps or touches. Past <see cref="MaxSpans"/> spans the latest-starting one is dropped.
    /// </summary>
    /// <param name="from">The start of the span</param>
    /// <param name="to">The end of the span</param>
    internal void Add(DateTimeOffset from, DateTimeOffset to)
    {
        if (to <= from || (CompleteUntil is { } bound && from >= bound))
        {
            return;
        }

        var first = FirstReaching(from);
        var last = first;
        var mergedFrom = from;
        var mergedTo = to;

        while (last < _spans.Count && _spans[last].From <= to)
        {
            mergedFrom = _spans[last].From < mergedFrom ? _spans[last].From : mergedFrom;
            mergedTo = _spans[last].To > mergedTo ? _spans[last].To : mergedTo;
            last++;
        }

        _spans.RemoveRange(first, last - first);
        _spans.Insert(first, (mergedFrom, mergedTo));

        if (_spans.Count > MaxSpans)
        {
            var dropped = _spans[^1];

            _spans.RemoveAt(_spans.Count - 1);
            CompleteUntil = SeriesTime.Earlier(CompleteUntil, dropped.From);
        }
    }

    /// <summary>
    /// Finds the first span that ends at or after a time.
    /// </summary>
    /// <param name="time">The time</param>
    /// <returns>The index of the span, the number of spans when none ends that late</returns>
    private int FirstReaching(DateTimeOffset time)
    {
        var low = 0;
        var high = _spans.Count;

        while (low < high)
        {
            var middle = low + ((high - low) / 2);

            if (_spans[middle].To >= time)
            {
                high = middle;
            }
            else
            {
                low = middle + 1;
            }
        }

        return low;
    }

    #endregion // Methods
}