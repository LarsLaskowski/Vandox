namespace Vandox.Storage;

/// <summary>
/// Reads the gaps and the clock offset of agents.
/// </summary>
public interface IAgentTimelineReader
{
    #region Methods

    /// <summary>
    /// Reads a page of the gaps of one agent, ordered by ID.
    /// </summary>
    /// <param name="query">The query</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the gaps</returns>
    /// <exception cref="InvalidQueryException">The query is invalid</exception>
    Task<IReadOnlyList<AgentGap>> GapsAsync(GapQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the measured clock offset of one agent.
    /// </summary>
    /// <param name="agentId">The agent ID</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the clock, or <see langword="null"/> when the agent has none</returns>
    Task<AgentClock?> ClockAsync(string agentId, CancellationToken cancellationToken);

    #endregion // Methods
}