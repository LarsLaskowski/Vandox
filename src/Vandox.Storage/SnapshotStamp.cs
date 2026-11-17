namespace Vandox.Storage;

/// <summary>
/// The ID, kind and capture time of a stored snapshot, without its payload.
/// </summary>
public sealed class SnapshotStamp
{
    #region Properties

    /// <summary>
    /// Gets the ID of the record.
    /// </summary>
    public long Id { get; init; }

    /// <summary>
    /// Gets the record kind.
    /// </summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>
    /// Gets the capture time.
    /// </summary>
    public DateTimeOffset CapturedAt { get; init; }

    #endregion // Properties
}