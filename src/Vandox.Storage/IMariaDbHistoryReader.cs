namespace Vandox.Storage;

/// <summary>
/// Reads the stored MariaDB status and thread snapshots for the services view.
/// </summary>
public interface IMariaDbHistoryReader
{
    #region Methods

    /// <summary>
    /// Reads the availability, the memory in use and the connections of the MariaDB status records on the steps of a time axis.
    /// </summary>
    /// <param name="query">The query</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>The values per step, the times by state and the facts about the gaps</returns>
    /// <exception cref="InvalidQueryException">The query breaks a rule; nothing was read</exception>
    /// <exception cref="StoreException">The read fails</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled</exception>
    Task<MariaDbStatusResult> MariaDbStatusAsync(MariaDbStatusQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the stamps of the status records with threads in a range, newest first.
    /// </summary>
    /// <param name="query">The query</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>The stamps, at most the limit of the query</returns>
    /// <exception cref="InvalidQueryException">The query breaks a rule; nothing was read</exception>
    /// <exception cref="StoreException">The read fails</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled</exception>
    Task<IReadOnlyList<ThreadSnapshotStamp>> ThreadSnapshotsAsync(ThreadSnapshotQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the stamp of the thread snapshot closest to a time within a window.
    /// </summary>
    /// <param name="at">The time</param>
    /// <param name="window">The window on each side of the time</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>The stamp, or <c>null</c> when no snapshot lies within the window</returns>
    /// <exception cref="InvalidQueryException">A parameter breaks a rule; nothing was read</exception>
    /// <exception cref="StoreException">The read fails</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled</exception>
    Task<ThreadSnapshotStamp?> ClosestThreadSnapshotAsync(DateTimeOffset at, TimeSpan window, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the threads of one status record.
    /// </summary>
    /// <param name="recordId">The ID of the record</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>The snapshot, or <c>null</c> when the record does not exist or has no threads</returns>
    /// <exception cref="StoreException">The read fails, or a thread does not parse</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled</exception>
    Task<ThreadSnapshot?> ThreadSnapshotAsync(long recordId, CancellationToken cancellationToken);

    #endregion // Methods
}