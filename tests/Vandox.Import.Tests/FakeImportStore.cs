using System.Security.Cryptography;

using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.Import.Tests;

/// <summary>
/// A store double that keeps the import state and the records in memory and can be told to fail.
/// </summary>
internal sealed class FakeImportStore : IImportStore
{
    #region Fields

    private readonly Dictionary<string, ImportFile> _files = [];
    private long _nextId = 1;

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets the batches written, in order.
    /// </summary>
    internal List<RecordBatch> Batches { get; } = [];

    /// <summary>
    /// Gets the records stored, in order.
    /// </summary>
    internal List<DataRecord> Records { get; } = [];

    /// <summary>
    /// Gets or sets the exception <see cref="BeginImportAsync"/> throws.
    /// </summary>
    internal Exception? BeginFailure { get; set; }

    /// <summary>
    /// Gets or sets the exception <see cref="WriteBatchAsync"/> throws, starting with the batch at <see cref="FailFromBatch"/>.
    /// </summary>
    internal Exception? WriteFailure { get; set; }

    /// <summary>
    /// Gets or sets the number of the first batch (from 1) that fails with <see cref="WriteFailure"/>.
    /// </summary>
    internal int FailFromBatch { get; set; } = 1;

    /// <summary>
    /// Gets or sets an action that runs after a batch was stored, with the number of batches so far.
    /// </summary>
    internal Action<int>? AfterBatch { get; set; }

    /// <summary>
    /// Gets or sets the source type reported for every content, overriding the one that was stored.
    /// </summary>
    internal string? ReportedSourceType { get; set; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Returns the hash the importer stores for a content.
    /// </summary>
    /// <param name="content">The content</param>
    /// <returns>The SHA-256</returns>
    internal static byte[] Hash(byte[] content)
    {
        return SHA256.HashData(content);
    }

    /// <summary>
    /// Returns the state of a content, creating it when it is unknown.
    /// </summary>
    /// <param name="start">The import start</param>
    /// <returns>The stored state</returns>
    private ImportFile GetOrCreate(ImportFileStart start)
    {
        var key = Convert.ToHexString(start.Sha256);

        if (_files.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var created = new ImportFile
                      {
                          Id = _nextId++,
                          Sha256 = start.Sha256,
                          Size = start.Size,
                          Name = start.Name,
                          FileName = start.FileName,
                          ModTime = start.ModTime,
                          SourceType = start.SourceType
                      };

        _files[key] = created;

        return created;
    }

    #endregion // Methods

    #region IRecordWriter

    /// <inheritdoc />
    /// <returns>A task that returns the result</returns>
    public Task<WriteResult> WriteBatchAsync(RecordBatch batch, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<WriteResult>(cancellationToken);
        }

        if (WriteFailure is not null && Batches.Count + 1 >= FailFromBatch)
        {
            return Task.FromException<WriteResult>(WriteFailure);
        }

        var file = _files.Values.First(candidate => candidate.Id == batch.Import!.FileId);

        if (file.Records != batch.Import!.Done || file.Complete)
        {
            return Task.FromException<WriteResult>(new ImportConflictException("store: import state changed"));
        }

        file.Records += batch.Records.Count;
        file.Complete = batch.Import.Complete;
        Batches.Add(batch);
        Records.AddRange(batch.Records);
        AfterBatch?.Invoke(Batches.Count);

        return Task.FromResult(new WriteResult(batch.Records.Count, 0));
    }

    #endregion // IRecordWriter

    #region IImportTracker

    /// <inheritdoc />
    /// <returns>A task that returns the result</returns>
    public Task<ImportFile> BeginImportAsync(ImportFileStart start, CancellationToken cancellationToken)
    {
        if (BeginFailure is not null)
        {
            return Task.FromException<ImportFile>(BeginFailure);
        }

        var existing = GetOrCreate(start);

        return Task.FromResult(new ImportFile
                               {
                                   Id = existing.Id,
                                   Sha256 = existing.Sha256,
                                   Size = existing.Size,
                                   Name = existing.Name,
                                   FileName = existing.FileName,
                                   ModTime = existing.ModTime,
                                   SourceType = ReportedSourceType ?? existing.SourceType,
                                   Records = existing.Records,
                                   Complete = existing.Complete
                               });
    }

    #endregion // IImportTracker
}