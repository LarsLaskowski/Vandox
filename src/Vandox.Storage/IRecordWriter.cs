namespace Vandox.Storage;

/// <summary>
/// Stores batches of records.
/// </summary>
public interface IRecordWriter
{
    #region Methods

    /// <summary>
    /// Validates a batch and stores its records in one transaction; records already present are counted as duplicates and
    /// never overwritten.
    /// </summary>
    /// <param name="batch">The batch</param>
    /// <param name="cancellationToken">Cancels the write</param>
    /// <returns>A task that returns how many records were stored and how many were present</returns>
    /// <exception cref="InvalidBatchException">The batch breaks a rule</exception>
    /// <exception cref="ImportConflictException">The import step does not match the stored state</exception>
    Task<WriteResult> WriteBatchAsync(RecordBatch batch, CancellationToken cancellationToken);

    #endregion // Methods
}