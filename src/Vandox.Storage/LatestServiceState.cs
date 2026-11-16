namespace Vandox.Storage;

/// <summary>
/// The newest state of one service unit.
/// </summary>
public sealed class LatestServiceState
{
    #region Properties

    /// <summary>
    /// Gets the unit name.
    /// </summary>
    public string Unit { get; init; } = string.Empty;

    /// <summary>
    /// Gets the active state.
    /// </summary>
    public string ActiveState { get; init; } = string.Empty;

    /// <summary>
    /// Gets the sub state.
    /// </summary>
    public string SubState { get; init; } = string.Empty;

    /// <summary>
    /// Gets the active enter time as stored, if present.
    /// </summary>
    public string? ActiveEnterAt { get; init; }

    /// <summary>
    /// Gets the restart counter.
    /// </summary>
    public long Restarts { get; init; }

    /// <summary>
    /// Gets the capture time.
    /// </summary>
    public DateTimeOffset CapturedAt { get; init; }

    #endregion // Properties
}