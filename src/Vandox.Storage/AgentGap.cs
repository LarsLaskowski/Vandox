namespace Vandox.Storage;

/// <summary>
/// A gap in the data of an agent.
/// </summary>
public sealed class AgentGap
{
    #region Properties

    /// <summary>
    /// Gets or sets the ID.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Gets or sets the agent ID.
    /// </summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the kind, one of the <see cref="GapKind"/> constants.
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the cause.
    /// </summary>
    public string Cause { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the state, one of the <see cref="GapState"/> constants.
    /// </summary>
    public string State { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the first missing sequence number, if the gap names numbers.
    /// </summary>
    public ulong? FirstSeq { get; set; }

    /// <summary>
    /// Gets or sets the last missing sequence number, if the gap names numbers.
    /// </summary>
    public ulong? LastSeq { get; set; }

    /// <summary>
    /// Gets or sets the start of the gap in time.
    /// </summary>
    public DateTimeOffset From { get; set; }

    /// <summary>
    /// Gets or sets the end of the gap in time.
    /// </summary>
    public DateTimeOffset To { get; set; }

    /// <summary>
    /// Gets or sets the instant the gap was detected.
    /// </summary>
    public DateTimeOffset DetectedAt { get; set; }

    /// <summary>
    /// Gets or sets the instant the gap last changed.
    /// </summary>
    public DateTimeOffset ChangedAt { get; set; }

    #endregion // Properties
}