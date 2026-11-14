namespace Vandox.Storage;

/// <summary>
/// A gap in the data of an agent.
/// </summary>
public sealed class SeriesGap
{
    #region Properties

    /// <summary>
    /// Gets the agent ID.
    /// </summary>
    public string AgentId { get; init; } = string.Empty;

    /// <summary>
    /// Gets the kind: record, sequence or time.
    /// </summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>
    /// Gets the cause.
    /// </summary>
    public string Cause { get; init; } = string.Empty;

    /// <summary>
    /// Gets the state.
    /// </summary>
    public string State { get; init; } = string.Empty;

    /// <summary>
    /// Gets the collector, empty for none.
    /// </summary>
    public string Collector { get; init; } = string.Empty;

    /// <summary>
    /// Gets the start of the gap.
    /// </summary>
    public DateTimeOffset From { get; init; }

    /// <summary>
    /// Gets the end of the gap.
    /// </summary>
    public DateTimeOffset To { get; init; }

    #endregion // Properties

    #region Methods

    // Skeleton: removed with the implementation
#pragma warning disable S2325, RH2003
    /// <summary>
    /// Returns whether the gap concerns a series.
    /// </summary>
    /// <param name="source">The source of the series</param>
    /// <param name="name">The name of the series</param>
    /// <returns><see langword="true"/> when the gap concerns the series</returns>
    public bool Concerns(string source, string name)
    {
        throw new NotImplementedException();
    }
#pragma warning restore S2325, RH2003

    #endregion // Methods
}