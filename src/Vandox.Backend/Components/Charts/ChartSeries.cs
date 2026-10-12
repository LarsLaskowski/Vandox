namespace Vandox.Backend.Components.Charts;

#pragma warning disable RH2003, S2325

/// <summary>
/// One labelled series of a time series chart; a <c>null</c> value is a gap.
/// </summary>
public sealed class ChartSeries
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ChartSeries"/> class.
    /// </summary>
    /// <param name="label">The label of the series</param>
    /// <param name="values">The values, one per point in time; <c>null</c> is a gap</param>
    public ChartSeries(string label, IReadOnlyList<double?> values)
    {
        throw new NotImplementedException();
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the label of the series.
    /// </summary>
    public string Label => throw new NotImplementedException();

    /// <summary>
    /// Gets the values of the series.
    /// </summary>
    public IReadOnlyList<double?> Values => throw new NotImplementedException();

    #endregion // Properties
}