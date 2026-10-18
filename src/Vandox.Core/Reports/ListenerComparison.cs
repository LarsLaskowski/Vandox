using Vandox.Core.Model;

namespace Vandox.Core.Reports;

/// <summary>
/// The result of comparing two connection snapshots for new listeners.
/// </summary>
public sealed class ListenerComparison
{
    #region Properties

    /// <summary>
    /// Gets the outcome of the comparison.
    /// </summary>
    public ListenerOutcome Outcome { get; init; }

    /// <summary>
    /// Gets the new listeners, at most ListenerComparer.MaxNewListeners.
    /// </summary>
    public IReadOnlyList<Listener> NewListeners { get; init; } = [];

    /// <summary>
    /// Gets the number of new listeners beyond the cap.
    /// </summary>
    public long Omitted { get; init; }

    #endregion // Properties
}