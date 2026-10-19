namespace Vandox.Core.Reports;

/// <summary>
/// The origin of a listener finding.
/// </summary>
public enum ListenerFindingKind
{
    #region Values

    /// <summary>
    /// Found by comparing with the snapshot before an agent start.
    /// </summary>
    AgentStart,

    /// <summary>
    /// Found by the agent itself.
    /// </summary>
    Agent

    #endregion // Values
}