namespace Vandox.Core.Reports;

/// <summary>
/// The listener comparison counts of one agent.
/// </summary>
public sealed class ListenerTally
{
    #region Properties

    /// <summary>
    /// Gets the agent ID.
    /// </summary>
    public string AgentId { get; init; } = string.Empty;

    /// <summary>
    /// Gets the number of connection snapshots.
    /// </summary>
    public long Snapshots { get; init; }

    /// <summary>
    /// Gets the capture time of the first snapshot.
    /// </summary>
    public DateTimeOffset FirstAt { get; init; }

    /// <summary>
    /// Gets the capture time of the last snapshot.
    /// </summary>
    public DateTimeOffset LastAt { get; init; }

    /// <summary>
    /// Gets the number of agent starts.
    /// </summary>
    public long Starts { get; init; }

    /// <summary>
    /// Gets the number of starts compared on all ports.
    /// </summary>
    public long ComparedAll { get; init; }

    /// <summary>
    /// Gets the number of starts compared on ports below 1024 only.
    /// </summary>
    public long ComparedPrivileged { get; init; }

    /// <summary>
    /// Gets the number of starts not compared.
    /// </summary>
    public long NotCompared { get; init; }

    #endregion // Properties
}