namespace Vandox.Storage;

/// <summary>
/// The newest MariaDB status record.
/// </summary>
public sealed class MariaDbLatest
{
    #region Properties

    /// <summary>
    /// Gets the capture time.
    /// </summary>
    public DateTimeOffset CapturedAt { get; init; }

    /// <summary>
    /// Gets the availability text.
    /// </summary>
    public string Availability { get; init; } = string.Empty;

    /// <summary>
    /// Gets the number of connected threads, if present.
    /// </summary>
    public double? ThreadsConnected { get; init; }

    /// <summary>
    /// Gets the memory used, if present.
    /// </summary>
    public double? MemoryUsed { get; init; }

    /// <summary>
    /// Gets the connection limit, if present.
    /// </summary>
    public long? MaxConnections { get; init; }

    #endregion // Properties
}