namespace Vandox.Backend.Components.Charts;

/// <summary>
/// The data of a time series chart: strictly ascending points in time and one or more series over them.
/// </summary>
public sealed class ChartData
{
    #region Fields

    private readonly DateTimeOffset[] _times;

    private readonly ChartSeries[] _series;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ChartData"/> class.
    /// </summary>
    /// <param name="times">The points in time, strictly ascending</param>
    /// <param name="series">The series, each with one value per point in time</param>
    public ChartData(IReadOnlyList<DateTimeOffset> times, IReadOnlyList<ChartSeries> series)
        : this(times, series, false)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ChartData"/> class.
    /// </summary>
    /// <param name="times">The points in time, strictly ascending</param>
    /// <param name="series">The series, each with one value per point in time</param>
    /// <param name="stacked">Whether the series are drawn stacked; a stacked series has a value at every point</param>
    public ChartData(IReadOnlyList<DateTimeOffset> times, IReadOnlyList<ChartSeries> series, bool stacked)
    {
        ArgumentNullException.ThrowIfNull(times);
        ArgumentNullException.ThrowIfNull(series);

        _times = [.. times];
        _series = [.. series];
        Stacked = stacked;

        Validate(_times, _series, stacked);
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the points in time.
    /// </summary>
    public IReadOnlyList<DateTimeOffset> Times => _times;

    /// <summary>
    /// Gets the series.
    /// </summary>
    public IReadOnlyList<ChartSeries> Series => _series;

    /// <summary>
    /// Gets a value indicating whether the series are drawn stacked.
    /// </summary>
    public bool Stacked { get; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Converts the data to the columns the chart module expects: Unix seconds first, then one column per series.
    /// </summary>
    /// <returns>The columns</returns>
    internal double?[][] ToColumns()
    {
        var columns = new double?[_series.Length + 1][];

        columns[0] = _times.Select(time => (double?)(time.ToUnixTimeMilliseconds() / 1000.0)).ToArray();

        for (var index = 0; index < _series.Length; index++)
        {
            columns[index + 1] = _series[index].Values.ToArray();
        }

        return columns;
    }

    /// <summary>
    /// Checks that the data can be drawn.
    /// </summary>
    /// <param name="times">The points in time</param>
    /// <param name="series">The series</param>
    /// <param name="stacked">Whether the series are drawn stacked, so that every series needs a value at every point</param>
    /// <exception cref="ArgumentException">There is no series, a series has another length than the times, the times do not ascend strictly, a value is not a finite number or a stacked series has a gap</exception>
    private static void Validate(DateTimeOffset[] times, ChartSeries[] series, bool stacked)
    {
        if (series.Length == 0)
        {
            throw new ArgumentException("The chart needs at least one series.", nameof(series));
        }

        for (var index = 1; index < times.Length; index++)
        {
            if (times[index] <= times[index - 1])
            {
                throw new ArgumentException($"The times must ascend strictly, but the point {index} is not later than the point {index - 1}.", nameof(times));
            }
        }

        foreach (var item in series)
        {
            if (item.Values.Count != times.Length)
            {
                throw new ArgumentException($"The series '{item.Label}' has {item.Values.Count} values for {times.Length} points in time.", nameof(series));
            }

            if (item.Values.Any(value => value is double.NaN or double.PositiveInfinity or double.NegativeInfinity))
            {
                throw new ArgumentException($"The series '{item.Label}' holds a value that is not a finite number.", nameof(series));
            }

            if (stacked)
            {
                RequireValues(item);
            }
        }
    }

    /// <summary>
    /// Checks that a series to be stacked has a value at every point.
    /// </summary>
    /// <param name="item">The series</param>
    /// <exception cref="ArgumentException">The series has a gap</exception>
    private static void RequireValues(ChartSeries item)
    {
        for (var index = 0; index < item.Values.Count; index++)
        {
            if (item.Values[index] is null)
            {
                throw new ArgumentException($"The stacked series '{item.Label}' has no value at point {index}.", nameof(item));
            }
        }
    }

    #endregion // Methods
}