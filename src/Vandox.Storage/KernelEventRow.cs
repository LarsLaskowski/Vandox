namespace Vandox.Storage;

/// <summary>
/// A kernel event projected without its payload.
/// </summary>
public sealed class KernelEventRow
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
    /// Gets the type.
    /// </summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>
    /// Gets the boot ID.
    /// </summary>
    public string BootId { get; init; } = string.Empty;

    /// <summary>
    /// Gets the victim command.
    /// </summary>
    public string VictimCommand { get; init; } = string.Empty;

    /// <summary>
    /// Gets the kernel time in nanoseconds, if recorded.
    /// </summary>
    public long? KernelTimeNs { get; init; }

    /// <summary>
    /// Gets the victim process ID.
    /// </summary>
    public long? VictimPid { get; init; }

    #endregion // Properties
}