using Vandox.Core.Reports;

namespace Vandox.Storage;

/// <summary>
/// The latest listener findings of one kind.
/// </summary>
public sealed class ListenerFindingPage
{
    #region Properties

    /// <summary>
    /// Gets the latest findings, by capture time descending, then agent ID ascending, then sequence number descending.
    /// </summary>
    public IReadOnlyList<ListenerFinding> Findings { get; init; } = [];

    /// <summary>
    /// Gets the total number of findings of the kind.
    /// </summary>
    public long Total { get; init; }

    #endregion // Properties
}