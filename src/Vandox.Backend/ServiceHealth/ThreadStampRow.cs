namespace Vandox.Backend.ServiceHealth;

/// <summary>
/// A row of the list of the thread snapshots.
/// </summary>
public sealed class ThreadStampRow
{
    #region Properties

    /// <summary>
    /// Gets the capture time.
    /// </summary>
    public DateTimeOffset At { get; init; }

    /// <summary>
    /// Gets the capture time as text.
    /// </summary>
    public string Time { get; init; } = string.Empty;

    /// <summary>
    /// Gets the number of recorded threads as text.
    /// </summary>
    public string Threads { get; init; } = string.Empty;

    /// <summary>
    /// Gets the number of running threads as text.
    /// </summary>
    public string Running { get; init; } = string.Empty;

    /// <summary>
    /// Gets the longest running time as text.
    /// </summary>
    public string Longest { get; init; } = string.Empty;

    #endregion // Properties
}