namespace Vandox.Storage;

/// <summary>
/// A query for the snapshot closest to a moment on one side.
/// </summary>
public sealed class SnapshotQuery
{
    #region Properties

    /// <summary>
    /// Gets the record kind, a snapshot kind.
    /// </summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>
    /// Gets the moment.
    /// </summary>
    public DateTimeOffset At { get; init; }

    /// <summary>
    /// Gets the window beside the moment in which a snapshot counts.
    /// </summary>
    public TimeSpan Window { get; init; }

    /// <summary>
    /// Gets the side of the moment to read.
    /// </summary>
    public SnapshotSide Side { get; init; }

    /// <summary>
    /// Gets the source to read from, <c>null</c> for any source.
    /// </summary>
    public string? Source { get; init; }

    #endregion // Properties
}