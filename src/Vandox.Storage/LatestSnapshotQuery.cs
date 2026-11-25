namespace Vandox.Storage;

/// <summary>
/// A query for the latest snapshot of a kind and source inside a time range.
/// </summary>
public sealed class LatestSnapshotQuery
{
    #region Properties

    /// <summary>
    /// Gets the record kind, a snapshot kind.
    /// </summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>
    /// Gets the source to read from.
    /// </summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>
    /// Gets the start of the range.
    /// </summary>
    public DateTimeOffset From { get; init; }

    /// <summary>
    /// Gets the end of the range.
    /// </summary>
    public DateTimeOffset To { get; init; }

    #endregion // Properties
}