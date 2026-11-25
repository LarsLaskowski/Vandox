namespace Vandox.Storage;

/// <summary>
/// Reads what the history view needs: the stored metrics and the snapshots around a moment.
/// </summary>
public interface IHistoryReader
{
    #region Methods

    /// <summary>
    /// Returns the metrics stored as raw values or hourly rollups.
    /// </summary>
    /// <param name="limit">The most metrics to return, 1 to <see cref="StorageLimits.MaxStoredMetrics"/></param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the metrics</returns>
    /// <exception cref="InvalidQueryException">The limit is out of range</exception>
    Task<StoredMetricList> StoredMetricsAsync(int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the ID and capture time of the snapshot closest to a moment on one side.
    /// </summary>
    /// <param name="query">The query</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the stamp, or <see langword="null"/> when there is none</returns>
    /// <exception cref="InvalidQueryException">The query breaks a rule</exception>
    Task<SnapshotStamp?> SnapshotStampAsync(SnapshotQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the ID and capture time of the latest snapshot of a kind and source inside a time range.
    /// </summary>
    /// <param name="query">The query</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the stamp, or <see langword="null"/> when there is none</returns>
    /// <exception cref="InvalidQueryException">The query breaks a rule</exception>
    Task<SnapshotStamp?> LatestSnapshotStampAsync(LatestSnapshotQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the record of a snapshot stamp.
    /// </summary>
    /// <param name="stamp">The stamp</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the record, or <see langword="null"/> when it was removed meanwhile</returns>
    /// <exception cref="InvalidQueryException">The stamp breaks a rule</exception>
    Task<StoredRecord?> SnapshotAsync(SnapshotStamp stamp, CancellationToken cancellationToken);

    #endregion // Methods
}