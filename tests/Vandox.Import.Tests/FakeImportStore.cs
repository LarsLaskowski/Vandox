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
    private readonly Dictionary<string, byte[]> _heads = [];
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

    /// <summary>
    /// Gets or sets the exception <see cref="FindImportBasesAsync"/> throws.
    /// </summary>
    internal Exception? FindFailure { get; set; }

    /// <summary>
    /// Gets or sets an action that runs at the start of every <see cref="FindImportBasesAsync"/> call.
    /// </summary>
    internal Action? BeforeFind { get; set; }

    /// <summary>
    /// Gets the messages of the log line records stored, in order.
    /// </summary>
    internal List<string> Messages => [.. Records.Select(record => ((LogLine)record.Data!).Message)];

    /// <summary>
    /// Gets or sets a value indicating whether <see cref="WriteFailure"/> is thrown once and then cleared.
    /// </summary>
    internal bool FailOnce { get; set; }

    /// <summary>
    /// Gets the number of calls of <see cref="FindImportBasesAsync"/>.
    /// </summary>
    internal int FindCalls { get; private set; }

    /// <summary>
    /// Gets the prefixes each call of <see cref="BeginImportAsync"/> got, in order.
    /// </summary>
    internal List<IReadOnlyList<ImportPrefix>> Prefixes { get; } = [];

    /// <summary>
    /// Gets the head key each call of <see cref="BeginImportAsync"/> got, in order.
    /// </summary>
    internal List<byte[]> Heads { get; } = [];

    /// <summary>
    /// Gets the states in the order they were created.
    /// </summary>
    internal IEnumerable<ImportFile> States => _files.Values.OrderBy(file => file.Id);

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
    /// Adds a state as if an earlier run had stored it.
    /// </summary>
    /// <param name="state">The state; its ID is assigned</param>
    /// <param name="head">The head key of the content</param>
    /// <returns>The stored state</returns>
    internal ImportFile Seed(ImportFile state, byte[] head)
    {
        var key = Convert.ToHexString(state.Sha256);

        state.Id = _nextId++;
        _files[key] = state;
        _heads[key] = head;

        return state;
    }

    /// <summary>
    /// Returns the largest complete state whose hash, size and source type a prefix names.
    /// </summary>
    /// <param name="start">The import start</param>
    /// <returns>The prefix and its state, or <c>null</c> when there is none</returns>
    private (ImportPrefix Prefix, ImportFile Base)? ChooseBase(ImportFileStart start)
    {
        (ImportPrefix Prefix, ImportFile Base)? chosen = null;

        foreach (var prefix in start.Prefixes)
        {
            if (_files.TryGetValue(Convert.ToHexString(prefix.Sha256), out var candidate)
                && candidate.Complete
                && candidate.Size == prefix.Size
                && candidate.SourceType == start.SourceType
                && (chosen is null || prefix.Size > chosen.Value.Prefix.Size))
            {
                chosen = (prefix, candidate);
            }
        }

        return chosen;
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

        var chosen = ChooseBase(start);
        var created = new ImportFile
                      {
                          Id = _nextId++,
                          Sha256 = start.Sha256,
                          Size = start.Size,
                          Name = start.Name,
                          FileName = start.FileName,
                          ModTime = start.ModTime,
                          SourceType = start.SourceType,
                          ContinuesId = chosen?.Base.Id,
                          Offset = chosen?.Prefix.Offset ?? 0,
                          ContinuedFrom = chosen?.Base.Name ?? string.Empty
                      };

        _files[key] = created;
        _heads[key] = start.HeadSha256;

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
            var failure = WriteFailure;

            if (FailOnce)
            {
                WriteFailure = null;
            }

            return Task.FromException<WriteResult>(failure);
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

        if (start.Prefixes.Count > StorageLimits.MaxImportBases)
        {
            return Task.FromException<ImportFile>(new InvalidImportException("store: invalid import: at most 64 prefixes"));
        }

        Prefixes.Add(start.Prefixes);
        Heads.Add(start.HeadSha256);

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
                                   Complete = existing.Complete,
                                   ContinuesId = existing.ContinuesId,
                                   Offset = existing.Offset,
                                   ContinuedFrom = existing.ContinuedFrom
                               });
    }

    /// <inheritdoc />
    /// <returns>A task that returns the complete contents of the head key and source type, largest first, at most 64</returns>
    public Task<IReadOnlyList<ImportBase>> FindImportBasesAsync(byte[] headSha256, string sourceType, CancellationToken cancellationToken)
    {
        FindCalls++;
        BeforeFind?.Invoke();

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<IReadOnlyList<ImportBase>>(cancellationToken);
        }

        if (FindFailure is not null)
        {
            return Task.FromException<IReadOnlyList<ImportBase>>(FindFailure);
        }

        var found = _files.Where(pair => pair.Value.Complete && pair.Value.SourceType == sourceType && _heads.TryGetValue(pair.Key, out var head) && head.AsSpan().SequenceEqual(headSha256))
                          .Select(pair => pair.Value)
                          .OrderByDescending(file => file.Size)
                          .Take(StorageLimits.MaxImportBases)
                          .Select(file => new ImportBase
                                          {
                                              Sha256 = file.Sha256,
                                              Size = file.Size
                                          })
                          .ToList();

        return Task.FromResult<IReadOnlyList<ImportBase>>(found);
    }

    #endregion // IImportTracker
}