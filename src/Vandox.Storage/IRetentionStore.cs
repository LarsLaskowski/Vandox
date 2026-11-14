namespace Vandox.Storage;

/// <summary>
/// The store operations of the retention job: folding rollups, scanning for kills and removing expired data.
/// </summary>
public interface IRetentionStore
{
    #region Methods

    /// <summary>
    /// Folds one bounded step of new metric values into the rollup buckets.
    /// </summary>
    /// <param name="policy">The retention policy</param>
    /// <param name="now">The current time</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that returns the result of the step</returns>
    Task<RollupStepResult> FoldRollupsAsync(RetentionPolicy policy, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Returns how far the kill scan has read.
    /// </summary>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that returns the progress</returns>
    Task<KillScanProgress> KillScanProgressAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Reads a page of kernel event records for the kill scan on a reader connection.
    /// </summary>
    /// <param name="query">The ID to start after and the page size</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that returns the records</returns>
    Task<IReadOnlyList<StoredRecord>> ScanRecordsAsync(KillScanQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Merges new holds with the stored ones and saves the scan progress in one write.
    /// </summary>
    /// <param name="holds">The new holds</param>
    /// <param name="progress">The progress to store</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that completes when the write is committed</returns>
    Task SaveKillScanAsync(IReadOnlyList<RetentionHold> holds, KillScanProgress progress, CancellationToken cancellationToken);

    /// <summary>
    /// Removes one bounded step of expired data.
    /// </summary>
    /// <param name="policy">The retention policy</param>
    /// <param name="now">The current time</param>
    /// <param name="cursor">Where the step continues, or <c>null</c> to start a pass</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that returns the result of the step</returns>
    Task<RemovalStepResult> RemoveExpiredAsync(RetentionPolicy policy, DateTimeOffset now, RemovalCursor? cursor, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the holds that overlap a range, on a reader connection.
    /// </summary>
    /// <param name="rangeStart">The start of the range</param>
    /// <param name="rangeEnd">The end of the range</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that returns the holds</returns>
    Task<IReadOnlyList<RetentionHold>> HoldsAsync(DateTimeOffset rangeStart, DateTimeOffset rangeEnd, CancellationToken cancellationToken);

    #endregion // Methods
}