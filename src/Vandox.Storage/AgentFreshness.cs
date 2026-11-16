namespace Vandox.Storage;

/// <summary>
/// How fresh the agent's data is.
/// </summary>
public sealed class AgentFreshness
{
    #region Properties

    /// <summary>
    /// Gets the ID of the agent that delivered last, empty for none.
    /// </summary>
    public string AgentId { get; init; } = string.Empty;

    /// <summary>
    /// Gets when the last record was received, if known.
    /// </summary>
    public DateTimeOffset? ReceivedAt { get; init; }

    /// <summary>
    /// Gets the capture time of the newest live metric record, if one was found.
    /// </summary>
    public DateTimeOffset? LastLiveAt { get; init; }

    /// <summary>
    /// Gets the capture time of the oldest record the live scan read, if none was live.
    /// </summary>
    public DateTimeOffset? ScannedBackTo { get; init; }

    /// <summary>
    /// Gets the number of open sequence gaps.
    /// </summary>
    public int OpenSequenceGaps { get; init; }

    /// <summary>
    /// Gets the start of the earliest open sequence gap, if any.
    /// </summary>
    public DateTimeOffset? MissingStart { get; init; }

    /// <summary>
    /// Gets the end of the latest open sequence gap, if any.
    /// </summary>
    public DateTimeOffset? MissingEnd { get; init; }

    #endregion // Properties
}