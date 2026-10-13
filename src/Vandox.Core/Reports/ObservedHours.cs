namespace Vandox.Core.Reports;

#pragma warning disable RH2003, S2325

/// <summary>
/// The distinct UTC hours in which the system log was observed, kept as capped runs of consecutive hours.
/// </summary>
public sealed class ObservedHours
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ObservedHours"/> class with the default cap.
    /// </summary>
    public ObservedHours()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ObservedHours"/> class.
    /// </summary>
    /// <param name="maxRuns">The most runs kept</param>
    public ObservedHours(int maxRuns)
    {
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the number of distinct observed hours.
    /// </summary>
    public long Count => throw new NotImplementedException();

    /// <summary>
    /// Gets the observed hours per UTC hour of day; 24 counts.
    /// </summary>
    public IReadOnlyList<long> HoursOfDay => throw new NotImplementedException();

    /// <summary>
    /// Gets a value indicating whether hours beyond the cap were dropped.
    /// </summary>
    public bool Truncated => throw new NotImplementedException();

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Returns the number of the UTC hour a time falls into.
    /// </summary>
    /// <param name="at">The time</param>
    /// <returns>The hours since the Unix epoch, rounded down</returns>
    public static long HourOf(DateTimeOffset at)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Adds the time of an observed line.
    /// </summary>
    /// <param name="capturedAt">The capture time</param>
    /// <exception cref="ArgumentException">The time is earlier than the previous one</exception>
    public void Add(DateTimeOffset capturedAt)
    {
        throw new NotImplementedException();
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
        throw new NotImplementedException();
    }

    /// <summary>
    /// Lists the end of every kept observed hour.
    /// </summary>
    /// <returns>The ends, ascending</returns>
    public IEnumerable<DateTimeOffset> Ends()
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}