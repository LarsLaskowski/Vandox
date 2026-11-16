using Vandox.Storage;

namespace Vandox.Backend.History;

/// <summary>
/// A marker of an event on the history chart.
/// </summary>
public sealed class HistoryMark
{
    #region Properties

    /// <summary>
    /// Gets the kind.
    /// </summary>
    public MarkerKind Kind { get; init; }

    /// <summary>
    /// Gets the time.
    /// </summary>
    public DateTimeOffset At { get; init; }

    /// <summary>
    /// Gets the end of a marker that spans time.
    /// </summary>
    public DateTimeOffset? EndsAt { get; init; }

    #endregion // Properties
}