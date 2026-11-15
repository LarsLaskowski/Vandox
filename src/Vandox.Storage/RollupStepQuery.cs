namespace Vandox.Storage;

/// <summary>
/// A read of the rollup steps of one series.
/// </summary>
public sealed class RollupStepQuery
{
    #region Properties

    /// <summary>
    /// Gets the rollup tier.
    /// </summary>
    public RollupTier Tier { get; init; }

    /// <summary>
    /// Gets the source of the series.
    /// </summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>
    /// Gets the name of the series.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Gets the start of the range.
    /// </summary>
    public DateTimeOffset From { get; init; }

    /// <summary>
    /// Gets the end of the range.
    /// </summary>
    public DateTimeOffset To { get; init; }

    /// <summary>
    /// Gets the step the buckets are folded to.
    /// </summary>
    public TimeSpan Step { get; init; }

    /// <summary>
    /// Gets the maximum number of lines.
    /// </summary>
    public int MaxLines { get; init; }

    #endregion // Properties
}