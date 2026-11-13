namespace Vandox.Storage;

/// <summary>
/// How long each kind of data is kept; a null period is unlimited.
/// </summary>
public sealed class RetentionPolicy
{
    #region Properties

    /// <summary>
    /// Gets how long raw metric values are kept.
    /// </summary>
    public TimeSpan? Raw { get; init; }

    /// <summary>
    /// Gets how long the 5-minute rollups are kept.
    /// </summary>
    public TimeSpan? FiveMinuteRollups { get; init; }

    /// <summary>
    /// Gets how long the hourly rollups are kept.
    /// </summary>
    public TimeSpan? HourlyRollups { get; init; }

    /// <summary>
    /// Gets how long log lines are kept.
    /// </summary>
    public TimeSpan? Logs { get; init; }

    /// <summary>
    /// Gets how long snapshots are kept.
    /// </summary>
    public TimeSpan? Snapshots { get; init; }

    /// <summary>
    /// Gets how long events are kept.
    /// </summary>
    public TimeSpan? Events { get; init; }

    /// <summary>
    /// Gets how long incidents are kept.
    /// </summary>
    public TimeSpan? Incidents { get; init; }

    /// <summary>
    /// Gets how long before an OOM kill its window starts.
    /// </summary>
    public TimeSpan KillWindowBefore { get; init; }

    /// <summary>
    /// Gets how long after an OOM kill its window ends.
    /// </summary>
    public TimeSpan KillWindowAfter { get; init; }

    #endregion // Properties
}