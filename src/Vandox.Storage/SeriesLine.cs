namespace Vandox.Storage;

/// <summary>
/// One line of a time-series result.
/// </summary>
public sealed class SeriesLine
{
    #region Properties

    /// <summary>
    /// Gets the source.
    /// </summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>
    /// Gets the name.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Gets the label set.
    /// </summary>
    public string Labels { get; init; } = string.Empty;

    /// <summary>
    /// Gets the points, one per axis time.
    /// </summary>
    public IReadOnlyList<SeriesPoint> Points { get; init; } = [];

    #endregion // Properties
}