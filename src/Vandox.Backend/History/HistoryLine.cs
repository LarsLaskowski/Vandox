namespace Vandox.Backend.History;

/// <summary>
/// A line of the history chart.
/// </summary>
public sealed class HistoryLine
{
    #region Properties

    /// <summary>
    /// Gets the escaped label.
    /// </summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>
    /// Gets the index of the metric, which gives the vertical scale.
    /// </summary>
    public int Scale { get; init; }

    /// <summary>
    /// Gets the color index.
    /// </summary>
    public int Color { get; init; }

    /// <summary>
    /// Gets a value indicating whether the line is dashed.
    /// </summary>
    public bool Dashed { get; init; }

    /// <summary>
    /// Gets the values; <see langword="null"/> for a gap.
    /// </summary>
    public IReadOnlyList<double?> Values { get; init; } = [];

    /// <summary>
    /// Gets the flags of the points drawn through a missing sample.
    /// </summary>
    public IReadOnlyList<bool> Bridged { get; init; } = [];

    /// <summary>
    /// Gets the minimums of a step; empty at raw resolution.
    /// </summary>
    public IReadOnlyList<double?> Minimums { get; init; } = [];

    /// <summary>
    /// Gets the maximums of a step; empty at raw resolution.
    /// </summary>
    public IReadOnlyList<double?> Maximums { get; init; } = [];

    #endregion // Properties
}