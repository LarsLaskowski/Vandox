namespace Vandox.Storage;

/// <summary>
/// The result of one removal step.
/// </summary>
public sealed class RemovalStepResult
{
    #region Properties

    /// <summary>
    /// Gets the number of raw metric values removed.
    /// </summary>
    public int MetricValues { get; init; }

    /// <summary>
    /// Gets the number of log lines removed.
    /// </summary>
    public int LogLines { get; init; }

    /// <summary>
    /// Gets the number of snapshot records removed.
    /// </summary>
    public int Snapshots { get; init; }

    /// <summary>
    /// Gets the number of event records removed.
    /// </summary>
    public int EventRecords { get; init; }

    /// <summary>
    /// Gets the number of gap rows removed.
    /// </summary>
    public int Gaps { get; init; }

    /// <summary>
    /// Gets the number of 5-minute buckets removed.
    /// </summary>
    public int FiveMinuteValues { get; init; }

    /// <summary>
    /// Gets the number of hourly buckets removed.
    /// </summary>
    public int HourlyValues { get; init; }

    /// <summary>
    /// Gets the number of template hours removed.
    /// </summary>
    public int TemplateHours { get; init; }

    /// <summary>
    /// Gets the number of incidents removed.
    /// </summary>
    public int RemovedIncidents { get; init; }

    /// <summary>
    /// Gets the number of holds removed.
    /// </summary>
    public int Holds { get; init; }

    /// <summary>
    /// Gets whether more data waits to be removed.
    /// </summary>
    public bool More { get; init; }

    /// <summary>
    /// Gets where the next step continues.
    /// </summary>
    public RemovalCursor? Next { get; init; }

    #endregion // Properties
}