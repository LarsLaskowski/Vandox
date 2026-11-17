namespace Vandox.Backend.History;

/// <summary>
/// A click in the chart: the time under the pointer and the tolerance that snaps it to a marker.
/// </summary>
public sealed class HistoryPick
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="HistoryPick"/> class.
    /// </summary>
    /// <param name="at">The time under the pointer</param>
    /// <param name="tolerance">The span within which a marker is taken</param>
    public HistoryPick(DateTimeOffset at, TimeSpan tolerance)
    {
        At = at;
        Tolerance = tolerance;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the the time under the pointer.
    /// </summary>
    public DateTimeOffset At { get; }

    /// <summary>
    /// Gets the the span within which a marker is taken.
    /// </summary>
    public TimeSpan Tolerance { get; }

    #endregion // Properties
}