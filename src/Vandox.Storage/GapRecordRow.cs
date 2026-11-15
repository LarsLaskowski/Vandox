namespace Vandox.Storage;

/// <summary>
/// A gap record projected without its payload.
/// </summary>
public sealed class GapRecordRow
{
    #region Properties

    /// <summary>
    /// Gets the ID.
    /// </summary>
    public long Id { get; init; }

    /// <summary>
    /// Gets the agent ID.
    /// </summary>
    public string AgentId { get; init; } = string.Empty;

    /// <summary>
    /// Gets the cause.
    /// </summary>
    public string Cause { get; init; } = string.Empty;

    /// <summary>
    /// Gets the collector.
    /// </summary>
    public string Collector { get; init; } = string.Empty;

    /// <summary>
    /// Gets the start of the span as stored.
    /// </summary>
    public string SpanFrom { get; init; } = string.Empty;

    /// <summary>
    /// Gets the end of the span as stored.
    /// </summary>
    public string SpanTo { get; init; } = string.Empty;

    /// <summary>
    /// Gets the capture time.
    /// </summary>
    public DateTimeOffset CapturedAt { get; init; }

    /// <summary>
    /// Gets the first missing sequence number.
    /// </summary>
    public long? FirstSeq { get; init; }

    /// <summary>
    /// Gets the last missing sequence number.
    /// </summary>
    public long? LastSeq { get; init; }

    #endregion // Properties
}