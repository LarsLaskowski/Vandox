namespace Vandox.Backend.History;

/// <summary>
/// The panel of a moment.
/// </summary>
public sealed class HistoryMoment
{
    #region Properties

    /// <summary>
    /// Gets the moment.
    /// </summary>
    public DateTimeOffset At { get; init; }

    /// <summary>
    /// Gets the process snapshot; <see langword="null"/> when none was stored.
    /// </summary>
    public ProcessSnapshotView? Process { get; init; }

    /// <summary>
    /// Gets the capture time of the latest process snapshot before the moment when the one shown is later.
    /// </summary>
    public DateTimeOffset? ProcessBefore { get; init; }

    /// <summary>
    /// Gets the connection snapshot; <see langword="null"/> when none was stored.
    /// </summary>
    public ConnectionSnapshotView? Connection { get; init; }

    #endregion // Properties
}