namespace Vandox.Storage;

/// <summary>
/// The gaps that concern one line, sorted by start, with the running maximum of their ends, so an overlap is found by a binary search.
/// </summary>
internal sealed class GapIndex
{
    #region Fields

    private readonly long[] _starts;

    private readonly long[] _runningEnds;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="GapIndex"/> class.
    /// </summary>
    /// <param name="gaps">The gaps in order of their start</param>
    internal GapIndex(IReadOnlyList<SeriesGap> gaps)
    {
        _starts = gaps.Select(gap => gap.From.UtcTicks).ToArray();
        _runningEnds = new long[gaps.Count];

        var highest = long.MinValue;

        for (var index = 0; index < gaps.Count; index++)
        {
            highest = Math.Max(highest, gaps[index].To.UtcTicks);
            _runningEnds[index] = highest;
        }
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Finds the start of the earliest gap that overlaps the open interval of two times.
    /// </summary>
    /// <param name="after">The start of the interval</param>
    /// <param name="before">The end of the interval</param>
    /// <returns>The start of the gap in ticks, or <c>null</c> when no gap overlaps</returns>
    internal long? FirstOverlapStart(long after, long before)
    {
        var low = 0;
        var high = _runningEnds.Length;

        while (low < high)
        {
            var middle = (low + high) / 2;

            if (_runningEnds[middle] > after)
            {
                high = middle;
            }
            else
            {
                low = middle + 1;
            }
        }

        return low < _starts.Length && _starts[low] < before ? _starts[low] : null;
    }

    /// <summary>
    /// Tells whether a time lies in a gap, its start included and its end excluded.
    /// </summary>
    /// <param name="time">The time in ticks</param>
    /// <returns><c>true</c> when a gap contains the time</returns>
    internal bool Contains(long time)
    {
        var low = 0;
        var high = _starts.Length;

        while (low < high)
        {
            var middle = (low + high) / 2;

            if (_starts[middle] <= time)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low > 0 && _runningEnds[low - 1] > time;
    }

    #endregion // Methods
}