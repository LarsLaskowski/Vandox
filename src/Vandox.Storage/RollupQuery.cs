namespace Vandox.Storage;

/// <summary>
/// A read of rollup buckets of one series.
/// </summary>
public sealed class RollupQuery
{
    #region Properties

    /// <summary>
    /// Gets the tier to read.
    /// </summary>
    public RollupTier Tier { get; init; }

    /// <summary>
    /// Gets the source of the series.
    /// </summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>
    /// Gets the metric name.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Gets the first bucket start to read.
    /// </summary>
    public DateTimeOffset From { get; init; }

    /// <summary>
    /// Gets the end of the range, exclusive.
    /// </summary>
    public DateTimeOffset To { get; init; }

    /// <summary>
    /// Gets the most buckets to return.
    /// </summary>
    public int Limit { get; init; }

    #endregion // Properties
}