namespace Vandox.Storage;

/// <summary>
/// Searches stored log lines.
/// </summary>
public interface ILogSearcher
{
    #region Methods

    /// <summary>
    /// Returns the log lines matching a search, ordered by capture time then ID.
    /// </summary>
    /// <param name="search">The search</param>
    /// <param name="cancellationToken">Cancels the search</param>
    /// <returns>A task that returns the log lines</returns>
    /// <exception cref="InvalidQueryException">The search breaks a rule</exception>
    Task<IReadOnlyList<StoredRecord>> SearchLogsAsync(LogSearch search, CancellationToken cancellationToken);

    #endregion // Methods
}