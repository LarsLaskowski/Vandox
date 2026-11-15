namespace Vandox.Backend.Components.Charts;

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
        : this(label, values, [])
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ChartSeries"/> class.
    /// </summary>
    /// <param name="label">The label of the series</param>
    /// <param name="values">The values, one per point in time; <c>null</c> is a gap</param>
    /// <param name="bridged">Per point in time, whether the point lies between samples and is bridged; empty for none</param>
    public ChartSeries(string label, IReadOnlyList<double?> values, IReadOnlyList<bool> bridged)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(bridged);

        Label = label;
        Values = values;
        Bridged = bridged;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the label of the series.
    /// </summary>
    public string Label { get; }

    /// <summary>
    /// Gets the values of the series.
    /// </summary>
    public IReadOnlyList<double?> Values { get; }

    /// <summary>
    /// Gets per point in time whether it lies between samples and is bridged; empty when no point is.
    /// </summary>
    public IReadOnlyList<bool> Bridged { get; }

    #endregion // Properties
}