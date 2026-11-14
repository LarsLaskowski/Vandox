namespace Vandox.Storage.Tests;

/// <summary>
/// The columns of a gap row a test stores directly.
/// </summary>
internal sealed class SampleGap
{
    #region Properties

    /// <summary>
    /// Gets the agent ID.
    /// </summary>
    internal string AgentId { get; init; } = TimelineSamples.AgentId;

    /// <summary>
    /// Gets the kind, <c>sequence</c> or <c>time</c>.
    /// </summary>
    internal string Kind { get; init; } = GapKind.Sequence;

    /// <summary>
    /// Gets the stored state.
    /// </summary>
    internal string State { get; init; } = GapState.Open;

    /// <summary>
    /// Gets the first missing sequence number of a sequence gap.
    /// </summary>
    internal long? FirstSeq { get; init; }

    /// <summary>
    /// Gets the last missing sequence number of a sequence gap.
    /// </summary>
    internal long? LastSeq { get; init; }

    /// <summary>
    /// Gets the sequence number before a time gap.
    /// </summary>
    internal long? FromSeq { get; init; }

    /// <summary>
    /// Gets the sequence number after a time gap.
    /// </summary>
    internal long? ToSeq { get; init; }

    /// <summary>
    /// Gets the start of the span.
    /// </summary>
    internal DateTimeOffset From { get; init; }

    /// <summary>
    /// Gets the end of the span.
    /// </summary>
    internal DateTimeOffset To { get; init; }

    #endregion // Properties
}