namespace Vandox.Storage;

/// <summary>
/// A recorded service state with the record before it.
/// </summary>
public sealed class ServiceStateRow
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
    /// Gets the active enter time as stored, if present.
    /// </summary>
    public string? ActiveEnterAt { get; init; }

    /// <summary>
    /// Gets the capture time.
    /// </summary>
    public DateTimeOffset CapturedAt { get; init; }

    /// <summary>
    /// Gets the restart counter.
    /// </summary>
    public long Restarts { get; init; }

    /// <summary>
    /// Gets the boot ID, empty for none.
    /// </summary>
    public string BootId { get; init; } = string.Empty;

    /// <summary>
    /// Gets the previous state of the unit, if any.
    /// </summary>
    public ServiceStateRow? Previous { get; init; }

    #endregion // Properties
}