namespace Vandox.Storage;

/// <summary>
/// The restart markers of a range and the time up to which the list is complete.
/// </summary>
public sealed class SeriesMarkerList
{
    #region Properties

    /// <summary>
    /// Gets the markers in time order.
    /// </summary>
    public IReadOnlyList<SeriesMarker> Markers { get; init; } = [];

    /// <summary>
    /// Gets the time up to which the list is complete, <c>null</c> when it is complete.
    /// </summary>
    public DateTimeOffset? CompleteUntil { get; init; }

    #endregion // Properties
}