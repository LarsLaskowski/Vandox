namespace Vandox.Storage;

/// <summary>
/// A read of the newest values of several series.
/// </summary>
public sealed class LatestValueQuery
{
    #region Properties

    /// <summary>
    /// Gets the series to read.
    /// </summary>
    public IReadOnlyList<SeriesKey> Series { get; init; } = [];

    /// <summary>
    /// Gets the span before the newest value of a series that is read.
    /// </summary>
    public TimeSpan Window { get; init; }

    /// <summary>
    /// Gets the maximum number of label sets returned per series.
    /// </summary>
    public int MaxLabelSets { get; init; }

    /// <summary>
    /// Gets the latest capture time that counts.
    /// </summary>
    public DateTimeOffset Until { get; init; }

    #endregion // Properties
}