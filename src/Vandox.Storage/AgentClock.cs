namespace Vandox.Storage;

/// <summary>
/// The measured clock offset of an agent.
/// </summary>
public sealed class AgentClock
{
    #region Properties

    /// <summary>
    /// Gets or sets the agent ID.
    /// </summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the offset of the agent clock to the backend clock in nanoseconds, if measured.
    /// </summary>
    public long? OffsetNs { get; set; }

    /// <summary>
    /// Gets or sets the instant the offset was measured, if it was.
    /// </summary>
    public DateTimeOffset? MeasuredAt { get; set; }

    #endregion // Properties
}