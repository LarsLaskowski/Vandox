#pragma warning disable RH2003, S2325, S4487 // Skeleton: removed with the implementation
namespace Vandox.Storage;

/// <summary>
/// The time of a range whose state is unknown: the gap spans and the time from the point up to which the gaps are complete.
/// </summary>
internal sealed class UnknownSpans
{
    #region Fields

    private readonly IReadOnlyList<(DateTimeOffset From, DateTimeOffset To)> _spans;

    private readonly DateTimeOffset? _knownUntil;

    private readonly DateTimeOffset _end;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="UnknownSpans"/> class.
    /// </summary>
    /// <param name="spans">The merged gap spans by start</param>
    /// <param name="knownUntil">The time up to which the gap spans are complete, <c>null</c> when they are complete</param>
    /// <param name="end">The end of the range</param>
    internal UnknownSpans(IReadOnlyList<(DateTimeOffset From, DateTimeOffset To)> spans, DateTimeOffset? knownUntil, DateTimeOffset end)
    {
        _spans = spans;
        _knownUntil = knownUntil;
        _end = end;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Returns the parts of a span that are not unknown.
    /// </summary>
    /// <param name="from">The start of the span</param>
    /// <param name="to">The end of the span</param>
    /// <returns>The known parts by start</returns>
    internal IReadOnlyList<(DateTimeOffset From, DateTimeOffset To)> KnownParts(DateTimeOffset from, DateTimeOffset to)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}
#pragma warning restore RH2003, S2325, S4487