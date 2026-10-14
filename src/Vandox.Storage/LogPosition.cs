namespace Vandox.Storage;

/// <summary>
/// A position in the time order of log lines.
/// </summary>
public sealed class LogPosition
{
    #region Properties

    /// <summary>
    /// Gets or sets the capture time.
    /// </summary>
    public DateTimeOffset CapturedAt { get; set; }

    /// <summary>
    /// Gets or sets the record ID.
    /// </summary>
    public long Id { get; set; }

    #endregion // Properties
}