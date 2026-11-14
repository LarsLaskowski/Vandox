namespace Vandox.Storage;

/// <summary>
/// A boot recorded at a capture time.
/// </summary>
public sealed class BootTime
{
    #region Properties

    /// <summary>
    /// Gets the capture time.
    /// </summary>
    public DateTimeOffset CapturedAt { get; init; }

    /// <summary>
    /// Gets the boot ID.
    /// </summary>
    public string BootId { get; init; } = string.Empty;

    #endregion // Properties
}