namespace Vandox.Storage;

/// <summary>
/// The MariaDB status of a range on the steps of a time axis.
/// </summary>
public sealed class MariaDbStatusResult
{
    #region Properties

    /// <summary>
    /// Gets the times of the steps.
    /// </summary>
    public IReadOnlyList<DateTimeOffset> Times { get; init; } = [];

    /// <summary>
    /// Gets the share of the known time of each step in which MariaDB was up, one point per time.
    /// </summary>
    public IReadOnlyList<SeriesPoint> Up { get; init; } = [];

    /// <summary>
    /// Gets the memory in use of each step, one point per time.
    /// </summary>
    public IReadOnlyList<SeriesPoint> Memory { get; init; } = [];

    /// <summary>
    /// Gets the connections of each step, one point per time.
    /// </summary>
    public IReadOnlyList<SeriesPoint> Connections { get; init; } = [];

    /// <summary>
    /// Gets the time MariaDB was up.
    /// </summary>
    public TimeSpan UpTime { get; init; }

    /// <summary>
    /// Gets the time MariaDB was down.
    /// </summary>
    public TimeSpan DownTime { get; init; }

    /// <summary>
    /// Gets the time MariaDB was running but not answering.
    /// </summary>
    public TimeSpan NotAnsweringTime { get; init; }

    /// <summary>
    /// Gets the time of the range that is unknown.
    /// </summary>
    public TimeSpan UnknownTime { get; init; }

    /// <summary>
    /// Gets the latest memory in use of the range, <c>null</c> when none was recorded.
    /// </summary>
    public double? LatestMemory { get; init; }

    /// <summary>
    /// Gets the capture time of the latest memory in use, <c>null</c> when none was recorded.
    /// </summary>
    public DateTimeOffset? LatestMemoryAt { get; init; }

    /// <summary>
    /// Gets the capture time of the oldest status record, <c>null</c> when none is stored.
    /// </summary>
    public DateTimeOffset? Oldest { get; init; }

    /// <summary>
    /// Gets the time up to which the gap spans are complete, <c>null</c> when they are complete.
    /// </summary>
    public DateTimeOffset? GapsCompleteUntil { get; init; }

    /// <summary>
    /// Gets the number of status records read.
    /// </summary>
    public int Records { get; init; }

    #endregion // Properties
}