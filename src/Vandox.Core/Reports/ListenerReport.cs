namespace Vandox.Core.Reports;

/// <summary>
/// The listener section of the forensics report.
/// </summary>
public sealed class ListenerReport
{
    #region Constants

    /// <summary>
    /// The largest number of agents listed.
    /// </summary>
    public const int MaxAgents = 64;

    /// <summary>
    /// The largest number of findings listed per kind.
    /// </summary>
    public const int MaxFindings = 20;

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets the counts per agent.
    /// </summary>
    public IReadOnlyList<ListenerTally> Agents { get; init; } = [];

    /// <summary>
    /// Gets the number of agents not listed.
    /// </summary>
    public long AgentsOmitted { get; init; }

    /// <summary>
    /// Gets the findings after agent starts.
    /// </summary>
    public IReadOnlyList<ListenerFinding> StartFindings { get; init; } = [];

    /// <summary>
    /// Gets the total number of findings after agent starts.
    /// </summary>
    public long StartFindingsTotal { get; init; }

    /// <summary>
    /// Gets the findings of the agents.
    /// </summary>
    public IReadOnlyList<ListenerFinding> AgentFindings { get; init; } = [];

    /// <summary>
    /// Gets the total number of findings of the agents.
    /// </summary>
    public long AgentFindingsTotal { get; init; }

    #endregion // Properties
}