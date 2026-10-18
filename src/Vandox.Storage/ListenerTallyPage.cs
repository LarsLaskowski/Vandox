using Vandox.Core.Reports;

namespace Vandox.Storage;

/// <summary>
/// The comparison counts of the lowest agent IDs.
/// </summary>
public sealed class ListenerTallyPage
{
    #region Properties

    /// <summary>
    /// Gets the counts of the lowest <see cref="ListenerReport.MaxAgents"/> agent IDs.
    /// </summary>
    public IReadOnlyList<ListenerTally> Agents { get; init; } = [];

    /// <summary>
    /// Gets the number of further agent IDs.
    /// </summary>
    public long Omitted { get; init; }

    #endregion // Properties
}