namespace Vandox.Storage;

/// <summary>
/// Selects a page of the gaps of one agent.
/// </summary>
public sealed class GapQuery
{
    #region Properties

    /// <summary>
    /// Gets or sets the agent ID.
    /// </summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the state to select, one of the <see cref="GapState"/> constants; empty selects every state.
    /// </summary>
    public string State { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the ID after which the page starts; 0 starts at the first gap.
    /// </summary>
    public long AfterId { get; set; }

    /// <summary>
    /// Gets or sets the number of gaps of the page, 1 to 1,000.
    /// </summary>
    public int Limit { get; set; }

    #endregion // Properties
}