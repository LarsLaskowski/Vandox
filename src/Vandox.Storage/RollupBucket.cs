namespace Vandox.Storage;

/// <summary>
/// One rollup bucket of a metric series.
/// </summary>
public sealed class RollupBucket
{
    #region Properties

    /// <summary>
    /// Gets the tier of the bucket.
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
    /// Gets the labels as stored, empty when none.
    /// </summary>
    public string Labels { get; init; } = string.Empty;

    /// <summary>
    /// Gets the start of the bucket.
    /// </summary>
    public DateTimeOffset Start { get; init; }

    /// <summary>
    /// Gets the smallest value.
    /// </summary>
    public double Minimum { get; init; }

    /// <summary>
    /// Gets the average of the values.
    /// </summary>
    public double Average { get; init; }

    /// <summary>
    /// Gets the largest value.
    /// </summary>
    public double Maximum { get; init; }

    /// <summary>
    /// Gets the last value by time.
    /// </summary>
    public double Last { get; init; }

    /// <summary>
    /// Gets the capture time of the last value.
    /// </summary>
    public DateTimeOffset LastAt { get; init; }

    /// <summary>
    /// Gets the number of values folded.
    /// </summary>
    public long Count { get; init; }

    #endregion // Properties
}