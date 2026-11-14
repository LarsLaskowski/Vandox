namespace Vandox.Storage;

/// <summary>
/// One line of a series: the points of one label set.
/// </summary>
public sealed class MetricLine
{
    #region Properties

    /// <summary>
    /// Gets the label set.
    /// </summary>
    public string Labels { get; init; } = string.Empty;

    /// <summary>
    /// Gets the points, oldest first.
    /// </summary>
    public IReadOnlyList<LinePoint> Points { get; init; } = [];

    #endregion // Properties
}