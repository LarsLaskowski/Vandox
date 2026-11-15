namespace Vandox.Storage;

/// <summary>
/// A marker of an event on the time axis.
/// </summary>
public sealed class SeriesMarker
{
    #region Properties

    /// <summary>
    /// Gets the kind.
    /// </summary>
    public MarkerKind Kind { get; init; }

    /// <summary>
    /// Gets the time of the marker.
    /// </summary>
    public DateTimeOffset At { get; init; }

    /// <summary>
    /// Gets the end of the marker, if it spans time.
    /// </summary>
    public DateTimeOffset? EndsAt { get; init; }

    /// <summary>
    /// Gets the last-seen time, if any.
    /// </summary>
    public DateTimeOffset? LastSeenAt { get; init; }

    /// <summary>
    /// Gets the how a backup run ended.
    /// </summary>
    public BackupRunEnd? End { get; init; }

    /// <summary>
    /// Gets the text: victim command, unit or backup command.
    /// </summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>
    /// Gets the process ID, if any.
    /// </summary>
    public long? Pid { get; init; }

    /// <summary>
    /// Gets the boot ID.
    /// </summary>
    public string BootId { get; init; } = string.Empty;

    #endregion // Properties
}