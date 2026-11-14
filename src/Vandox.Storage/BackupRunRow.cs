namespace Vandox.Storage;

/// <summary>
/// A backup run projected without its payload.
/// </summary>
public sealed class BackupRunRow
{
    #region Properties

    /// <summary>
    /// Gets the ID.
    /// </summary>
    public long Id { get; init; }

    /// <summary>
    /// Gets the capture time.
    /// </summary>
    public DateTimeOffset CapturedAt { get; init; }

    /// <summary>
    /// Gets the phase.
    /// </summary>
    public string Phase { get; init; } = string.Empty;

    /// <summary>
    /// Gets the boot ID.
    /// </summary>
    public string BootId { get; init; } = string.Empty;

    /// <summary>
    /// Gets the start time as stored, empty when absent.
    /// </summary>
    public string StartedAt { get; init; } = string.Empty;

    /// <summary>
    /// Gets the last-seen time as stored, empty when absent.
    /// </summary>
    public string LastSeenAt { get; init; } = string.Empty;

    /// <summary>
    /// Gets the gone-by time as stored, empty when absent.
    /// </summary>
    public string GoneBy { get; init; } = string.Empty;

    /// <summary>
    /// Gets the command.
    /// </summary>
    public string Command { get; init; } = string.Empty;

    #endregion // Properties
}