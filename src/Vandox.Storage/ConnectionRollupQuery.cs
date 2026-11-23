namespace Vandox.Storage;

/// <summary>
/// A read of the connection rollups of one source over a range.
/// </summary>
public sealed class ConnectionRollupQuery
{
    #region Properties

    /// <summary>
    /// Gets the source.
    /// </summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>
    /// Gets the start of the range.
    /// </summary>
    public DateTimeOffset From { get; init; }

    /// <summary>
    /// Gets the end of the range, exclusive.
    /// </summary>
    public DateTimeOffset To { get; init; }

    /// <summary>
    /// Gets the step of the axis.
    /// </summary>
    public TimeSpan Step { get; init; }

    #endregion // Properties
}