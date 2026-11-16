namespace Vandox.Storage;

/// <summary>
/// The newest values of one series.
/// </summary>
public sealed class LatestSeries
{
    #region Properties

    /// <summary>
    /// Gets the series.
    /// </summary>
    public SeriesKey Series { get; init; } = new(string.Empty, string.Empty);

    /// <summary>
    /// Gets the newest value per label set.
    /// </summary>
    public IReadOnlyList<LatestValue> Values { get; init; } = [];

    /// <summary>
    /// Gets the number of distinct label sets in the window.
    /// </summary>
    public int LabelSets { get; init; }

    #endregion // Properties
}