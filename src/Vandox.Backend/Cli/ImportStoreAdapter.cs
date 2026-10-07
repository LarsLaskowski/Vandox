using Vandox.Import;
using Vandox.Storage;

namespace Vandox.Backend.Cli;

/// <summary>
/// Presents the store as the part the importer writes to.
/// </summary>
internal sealed class ImportStoreAdapter : IImportStore
{
    #region Fields

    private readonly SqliteStore _store;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ImportStoreAdapter"/> class.
    /// </summary>
    /// <param name="store">The store</param>
    internal ImportStoreAdapter(SqliteStore store)
    {
        _store = store;
    }

    #endregion // Constructors

    #region IRecordWriter

    /// <inheritdoc />
    public Task<WriteResult> WriteBatchAsync(RecordBatch batch, CancellationToken cancellationToken)
    {
        return _store.WriteBatchAsync(batch, cancellationToken);
    }

    #endregion // IRecordWriter

    #region IImportTracker

    /// <inheritdoc />
    public Task<ImportFile> BeginImportAsync(ImportFileStart start, CancellationToken cancellationToken)
    {
        return _store.BeginImportAsync(start, cancellationToken);
    }

    #endregion // IImportTracker
}