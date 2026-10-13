namespace Vandox.Core.Reports;

/// <summary>
/// The distinct UTC hours in which the system log was observed, kept as capped runs of consecutive hours.
/// </summary>
public sealed class ObservedHours
{
    #region Constants

    private const int HoursPerDay = 24;

    #endregion // Constants

    #region Fields

    private readonly int _maxRuns;
    private readonly List<(long Start, long End)> _runs = [];
    private readonly long[] _hoursOfDay = new long[HoursPerDay];
    private long _lastHour;
    private long _lastTicks;
    private bool _any;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ObservedHours"/> class with the default cap.
    /// </summary>
    public ObservedHours()
        : this(ReportLimits.MaxObservedRuns)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ObservedHours"/> class.
    /// </summary>
    /// <param name="maxRuns">The most runs kept</param>
    public ObservedHours(int maxRuns)
    {
        _maxRuns = maxRuns;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the number of distinct observed hours.
    /// </summary>
    public long Count { get; private set; }

    /// <summary>
    /// Gets the observed hours per UTC hour of day; 24 counts.
    /// </summary>
    public IReadOnlyList<long> HoursOfDay => _hoursOfDay;

    /// <summary>
    /// Gets a value indicating whether hours beyond the cap were dropped.
    /// </summary>
    public bool Truncated { get; private set; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Returns the number of the UTC hour a time falls into.
    /// </summary>
    /// <param name="at">The time</param>
    /// <returns>The hours since the Unix epoch, rounded down</returns>
    public static long HourOf(DateTimeOffset at)
    {
        return HourOfTicks(at.UtcTicks);
    }

    /// <summary>
    /// Adds the time of an observed line.
    /// </summary>
    /// <param name="capturedAt">The capture time</param>
    /// <exception cref="ArgumentException">The time is earlier than the previous one</exception>
    public void Add(DateTimeOffset capturedAt)
    {
        var ticks = capturedAt.UtcTicks;

        if (_any && ticks < _lastTicks)
        {
            throw new ArgumentException("observed hours: a time is earlier than the previous one", nameof(capturedAt));
        }

        var hour = HourOfTicks(ticks);
        var sameHour = _any && hour == _lastHour;

        _any = true;
        _lastTicks = ticks;

        if (sameHour)
        {
            return;
        }

        _lastHour = hour;
        Count++;
        _hoursOfDay[(int)(((hour % HoursPerDay) + HoursPerDay) % HoursPerDay)]++;
        Keep(hour);
    }

    /// <summary>
    /// Determines whether every hour overlapping a range is observed.
    /// </summary>
    /// <param name="from">The start of the range</param>
    /// <param name="to">The end of the range, exclusive</param>
    /// <returns><c>true</c> when every overlapping hour is observed</returns>
    /// <exception cref="ArgumentException"><paramref name="from"/> is not before <paramref name="to"/></exception>
    public bool Covers(DateTimeOffset from, DateTimeOffset to)
    {
        if (from >= to)
        {
            throw new ArgumentException("observed hours: the start of a range must be before its end", nameof(from));
        }

        var first = HourOf(from);
        var last = HourOfTicks(to.UtcTicks - 1);
        var low = 0;
        var high = _runs.Count - 1;
        var found = -1;

        while (low <= high)
        {
            var middle = low + ((high - low) / 2);

            if (_runs[middle].Start <= first)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return found >= 0 && _runs[found].End >= last;
    }

    /// <summary>
    /// Lists the end of every kept observed hour.
    /// </summary>
    /// <returns>The ends, ascending</returns>
    public IEnumerable<DateTimeOffset> Ends()
    {
        foreach (var (start, end) in _runs)
        {
            for (var hour = start; hour <= end; hour++)
            {
                yield return EndOf(hour);
            }
        }
    }

    /// <summary>
    /// Returns the number of the UTC hour a tick count falls into, rounding down.
    /// </summary>
    /// <param name="utcTicks">The ticks of the UTC instant</param>
    /// <returns>The hours since the Unix epoch</returns>
    private static long HourOfTicks(long utcTicks)
    {
        var sinceEpoch = utcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
        var hours = sinceEpoch / TimeSpan.TicksPerHour;

        return sinceEpoch % TimeSpan.TicksPerHour < 0 ? hours - 1 : hours;
    }

    /// <summary>
    /// Returns the moment an hour ends, at most the largest representable time.
    /// </summary>
    /// <param name="hour">The hours since the Unix epoch</param>
    /// <returns>The end of the hour</returns>
    private static DateTimeOffset EndOf(long hour)
    {
        var ticks = DateTimeOffset.UnixEpoch.UtcTicks + ((hour + 1) * TimeSpan.TicksPerHour);

        return ticks >= DateTimeOffset.MaxValue.UtcTicks ? DateTimeOffset.MaxValue : new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    /// <summary>
    /// Keeps an hour: it extends the last run, starts a new run while the cap allows, or is dropped.
    /// </summary>
    /// <param name="hour">The hours since the Unix epoch</param>
    private void Keep(long hour)
    {
        if (_runs.Count > 0 && _runs[^1].End == hour - 1)
        {
            _runs[^1] = (_runs[^1].Start, hour);
        }
        else if (_runs.Count < _maxRuns)
        {
            _runs.Add((hour, hour));
        }
        else
        {
            Truncated = true;
        }
    }

    #endregion // Methods
}