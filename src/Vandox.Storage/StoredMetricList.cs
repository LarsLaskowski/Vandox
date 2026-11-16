namespace Vandox.Storage;

/// <summary>
/// The metrics stored as raw values or hourly rollups.
/// </summary>
public sealed class StoredMetricList
{
    #region Properties

    /// <summary>
    /// Gets the metrics in ordinal order of source and name.
    /// </summary>
    public IReadOnlyList<SeriesKey> Metrics { get; init; } = [];

    /// <summary>
    /// Gets a value indicating whether further metrics exist beyond the limit.
    /// </summary>
    public bool More { get; init; }

    #endregion // Properties
}