namespace Vandox.Storage;

/// <summary>
/// The time of a range whose state is unknown: the gap spans and the time from the point up to which the gaps are complete.
/// </summary>
internal sealed class UnknownSpans
{
    #region Fields

    private readonly IReadOnlyList<(DateTimeOffset From, DateTimeOffset To)> _spans;

    private readonly DateTimeOffset _end;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="UnknownSpans"/> class.
    /// </summary>
    /// <param name="spans">The merged gap spans by start</param>
    /// <param name="knownUntil">The time up to which the gap spans are complete, <c>null</c> when they are complete</param>
    /// <param name="end">The end of the range</param>
    internal UnknownSpans(IReadOnlyList<(DateTimeOffset From, DateTimeOffset To)> spans, DateTimeOffset? knownUntil, DateTimeOffset end)
    {
        _spans = spans;
        KnownUntil = knownUntil;
        _end = end;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the time up to which the gap spans are complete, <c>null</c> when they are complete.
    /// </summary>
    internal DateTimeOffset? KnownUntil { get; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Returns the parts of a span that are not unknown.
    /// </summary>
    /// <param name="from">The start of the span</param>
    /// <param name="to">The end of the span</param>
    /// <returns>The known parts by start</returns>
    internal IReadOnlyList<(DateTimeOffset From, DateTimeOffset To)> KnownParts(DateTimeOffset from, DateTimeOffset to)
    {
        var parts = new List<(DateTimeOffset From, DateTimeOffset To)>();
        var limit = Limit(to);
        var cursor = from;

        for (var index = FirstEndingAfter(from); index < _spans.Count && cursor < limit; index++)
        {
            var span = _spans[index];

            if (span.From >= limit)
            {
                break;
            }

            if (span.From > cursor)
            {
                parts.Add((cursor, span.From));
            }

            cursor = span.To > cursor ? span.To : cursor;
        }

        if (cursor < limit)
        {
            parts.Add((cursor, limit));
        }

        return parts;
    }

    /// <summary>
    /// Returns the length of the parts of a span that are not unknown.
    /// </summary>
    /// <param name="from">The start of the span</param>
    /// <param name="to">The end of the span</param>
    /// <returns>The known time</returns>
    internal TimeSpan KnownTime(DateTimeOffset from, DateTimeOffset to)
    {
        var known = TimeSpan.Zero;
        var limit = Limit(to);
        var cursor = from;

        for (var index = FirstEndingAfter(from); index < _spans.Count && cursor < limit; index++)
        {
            var span = _spans[index];

            if (span.From >= limit)
            {
                break;
            }

            known += span.From > cursor ? span.From - cursor : TimeSpan.Zero;
            cursor = span.To > cursor ? span.To : cursor;
        }

        return cursor < limit ? known + (limit - cursor) : known;
    }

    /// <summary>
    /// Returns the end of the known time of a span: its end, the end of the range and the time up to which the gaps are complete, whichever is earliest.
    /// </summary>
    /// <param name="to">The end of the span</param>
    /// <returns>The limit</returns>
    private DateTimeOffset Limit(DateTimeOffset to)
    {
        var limit = to < _end ? to : _end;

        return KnownUntil is { } complete && complete < limit ? complete : limit;
    }

    /// <summary>
    /// Finds the first span that ends after a time.
    /// </summary>
    /// <param name="time">The time</param>
    /// <returns>The index of the span, the number of spans when none ends that late</returns>
    private int FirstEndingAfter(DateTimeOffset time)
    {
        var low = 0;
        var high = _spans.Count;

        while (low < high)
        {
            var middle = low + ((high - low) / 2);

            if (_spans[middle].To > time)
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