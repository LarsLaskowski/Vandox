namespace Vandox.Storage;

/// <summary>
/// Reads the stored listener comparisons for the forensics report.
/// </summary>
public interface IListenerHistoryReader
{
    #region Methods

    /// <summary>
    /// Reads the comparison counts of the agents, the lowest agent IDs first.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>The counts and the number of further agent IDs</returns>
    Task<ListenerTallyPage> ListenerTalliesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Reads the latest findings of one kind.
    /// </summary>
    /// <param name="query">The query</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>The findings and the total number of findings of the kind</returns>
    Task<ListenerFindingPage> ListenerFindingsAsync(ListenerFindingQuery query, CancellationToken cancellationToken);

    #endregion // Methods
}