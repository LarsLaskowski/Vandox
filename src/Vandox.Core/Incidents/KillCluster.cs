namespace Vandox.Core.Incidents;

/// <summary>
/// The OOM kills whose windows overlap.
/// </summary>
public sealed class KillCluster
{
    #region Properties

    /// <summary>
    /// Gets or sets the start of the window.
    /// </summary>
    public DateTimeOffset Start { get; set; }

    /// <summary>
    /// Gets or sets the end of the window.
    /// </summary>
    public DateTimeOffset End { get; set; }

    /// <summary>
    /// Gets or sets the time of the first kill.
    /// </summary>
    public DateTimeOffset FirstKillAt { get; set; }

    /// <summary>
    /// Gets or sets the time of the last kill.
    /// </summary>
    public DateTimeOffset LastKillAt { get; set; }

    /// <summary>
    /// Gets or sets the number of kills.
    /// </summary>
    public long KillCount { get; set; }

    /// <summary>
    /// Gets or sets the first kills.
    /// </summary>
    public List<IncidentKill> Kills { get; set; } = [];

    #endregion // Properties
}