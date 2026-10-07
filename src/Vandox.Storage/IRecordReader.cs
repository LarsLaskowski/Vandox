namespace Vandox.Storage;

/// <summary>
/// Reads stored records by kind, source, name and time range.
/// </summary>
public interface IRecordReader
{
    #region Methods

    /// <summary>
    /// Returns the records matching a query, ordered by capture time then ID.
    /// </summary>
    /// <param name="query">The query</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the records</returns>
    /// <exception cref="InvalidQueryException">The query breaks a rule</exception>
    Task<IReadOnlyList<StoredRecord>> RecordsAsync(RecordQuery query, CancellationToken cancellationToken);

    #endregion // Methods
}