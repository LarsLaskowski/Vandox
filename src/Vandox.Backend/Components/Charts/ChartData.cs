namespace Vandox.Backend.Components.Charts;

#pragma warning disable RH2003, S2325

/// <summary>
/// The data of a time series chart: strictly ascending points in time and one or more series over them.
/// </summary>
public sealed class ChartData
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ChartData"/> class.
    /// </summary>
    /// <param name="times">The points in time, strictly ascending</param>
    /// <param name="series">The series, each with one value per point in time</param>
    public ChartData(IReadOnlyList<DateTimeOffset> times, IReadOnlyList<ChartSeries> series)
    {
        throw new NotImplementedException();
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the points in time.
    /// </summary>
    public IReadOnlyList<DateTimeOffset> Times => throw new NotImplementedException();

    /// <summary>
    /// Gets the series.
    /// </summary>
    public IReadOnlyList<ChartSeries> Series => throw new NotImplementedException();

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Converts the data to the columns the chart module expects: Unix seconds first, then one column per series.
    /// </summary>
    /// <returns>The columns</returns>
    internal double?[][] ToColumns()
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}