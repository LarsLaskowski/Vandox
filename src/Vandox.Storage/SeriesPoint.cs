namespace Vandox.Storage;

/// <summary>
/// One point of a series line.
/// </summary>
public sealed class SeriesPoint
{
    #region Properties

    /// <summary>
    /// Gets the state.
    /// </summary>
    public PointState State { get; init; }

    /// <summary>
    /// Gets the minimum.
    /// </summary>
    public double Minimum { get; init; }

    /// <summary>
    /// Gets the average.
    /// </summary>
    public double Average { get; init; }

    /// <summary>
    /// Gets the maximum.
    /// </summary>
    public double Maximum { get; init; }

    /// <summary>
    /// Gets the last value.
    /// </summary>
    public double Last { get; init; }

    /// <summary>
    /// Gets the time of the last sample.
    /// </summary>
    public DateTimeOffset LastAt { get; init; }

    /// <summary>
    /// Gets the number of samples.
    /// </summary>
    public long Count { get; init; }

    #endregion // Properties
}