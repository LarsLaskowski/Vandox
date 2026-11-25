#pragma warning disable RH2003, S2325 // Skeleton: removed with the implementation
namespace Vandox.Storage;

/// <summary>
/// The merged gap spans of one collector in a range: sorted by start, overlapping and touching spans merged, at most <see cref="MaxSpans"/>.
/// </summary>
internal sealed class GapSpanSet
{
    #region Constants

    /// <summary>
    /// The most spans kept.
    /// </summary>
    internal const int MaxSpans = 1000;

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets the merged spans by start.
    /// </summary>
    internal IReadOnlyList<(DateTimeOffset From, DateTimeOffset To)> Spans => throw new NotImplementedException();

    /// <summary>
    /// Gets the time up to which the spans are complete, <c>null</c> when no span was dropped.
    /// </summary>
    internal DateTimeOffset? CompleteUntil => throw new NotImplementedException();

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Adds a span, merging it with the spans it overlaps or touches. Past <see cref="MaxSpans"/> spans the latest-starting one is dropped.
    /// </summary>
    /// <param name="from">The start of the span</param>
    /// <param name="to">The end of the span</param>
    internal void Add(DateTimeOffset from, DateTimeOffset to)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}
#pragma warning restore RH2003, S2325