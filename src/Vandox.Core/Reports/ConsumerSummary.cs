namespace Vandox.Core.Reports;

/// <summary>
/// The memory use of one program over the whole period.
/// </summary>
public sealed class ConsumerSummary
{
    #region Properties

    /// <summary>
    /// Gets the program key.
    /// </summary>
    public string Program { get; init; } = string.Empty;

    /// <summary>
    /// Gets the number of snapshots the program is in.
    /// </summary>
    public long Snapshots { get; init; }

    /// <summary>
    /// Gets the share of snapshots the program is in.
    /// </summary>
    public double Share { get; init; }

    /// <summary>
    /// Gets the mean RSS in bytes over the snapshots the program is in, rounded down.
    /// </summary>
    public ulong MeanRssBytes { get; init; }

    /// <summary>
    /// Gets the peak RSS in bytes.
    /// </summary>
    public ulong PeakRssBytes { get; init; }

    /// <summary>
    /// Gets the first time of the peak.
    /// </summary>
    public DateTimeOffset PeakAt { get; init; }

    #endregion // Properties
}