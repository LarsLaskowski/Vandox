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
    private readonly Dictionary<long, long> _hashesAttempts = [];
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
    /// Gets the rows of line hashes the store holds: those it accepted, less those it deleted when a content was begun again or completed.
    /// </summary>
    internal List<ImportLineHashes> LineHashes { get; } = [];

    /// <summary>
    /// Gets every row of line hashes the store accepted, in order, whether or not it was deleted later.
    /// </summary>
    internal List<ImportLineHashes> LineHashWrites { get; } = [];

    /// <summary>
    /// Gets the number of calls of <see cref="AddLineHashesAsync"/>.
    /// </summary>
    internal int LineHashCalls { get; private set; }

    /// <summary>
    /// Gets or sets the exception <see cref="AddLineHashesAsync"/> throws, starting with the call at <see cref="FailFromLineHashCall"/>.
    /// </summary>
    internal Exception? LineHashFailure { get; set; }

    /// <summary>
    /// Gets or sets the number of the first call (from 1) of <see cref="AddLineHashesAsync"/> that fails with <see cref="LineHashFailure"/>.
    /// </summary>
    internal int FailFromLineHashCall { get; set; } = 1;

    /// <summary>
    /// Gets or sets a value indicating whether <see cref="LineHashFailure"/> is thrown once and then cleared.
    /// </summary>
    internal bool LineHashFailOnce { get; set; }

    /// <summary>
    /// Gets or sets an action that runs at the start of every <see cref="AddLineHashesAsync"/> call, with the number of calls so far.
    /// </summary>
    internal Action<int>? OnLineHashes { get; set; }

    /// <summary>
    /// Gets or sets the object <see cref="BeginImportAsync"/> returns instead of the state, after it recorded the call.
    /// </summary>
    internal ImportFile? ScriptedBeginResult { get; set; }

    /// <summary>
    /// Gets the file ID of every call that carried one (batches and line hashes), in order, whether or not the call was accepted.
    /// </summary>
    internal List<long> FileIdCalls { get; } = [];

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
    /// Tells whether a row of line hashes holds the first 16 bytes of a SHA-256 at a position that is a multiple of 16.
    /// </summary>
    /// <param name="hashes">The hashes of the row</param>
    /// <param name="sha256">The SHA-256</param>
    /// <returns><c>true</c> when one hash equals the first 16 bytes of the SHA-256</returns>
    private static bool Holds(byte[] hashes, byte[] sha256)
    {
        for (var offset = 0; offset + StorageLimits.LineHashBytes <= hashes.Length; offset += StorageLimits.LineHashBytes)
        {
            if (hashes.AsSpan(offset, StorageLimits.LineHashBytes).SequenceEqual(sha256.AsSpan(0, StorageLimits.LineHashBytes)))
            {
                return true;
            }
        }

        return false;
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

    /// <summary>
    /// Returns the state as <see cref="BeginImportAsync"/> reports it.
    /// </summary>
    /// <param name="existing">The stored state</param>
    /// <returns>A copy of the state</returns>
    private ImportFile Copy(ImportFile existing)
    {
        return new ImportFile
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
                   ContinuedFrom = existing.ContinuedFrom,
                   Attempt = existing.Attempt
               };
    }

    /// <summary>
    /// Applies the store's containment rule: the largest of at most 64 complete states of the head key and source type that are larger than the content, whose line hashes of the completing attempt hold the SHA-256 of the content.
    /// </summary>
    /// <param name="start">The import start</param>
    /// <returns>The state of the container, or <c>null</c> when the content is not the start of one</returns>
    private ImportFile? FindContainer(ImportFileStart start)
    {
        var candidates = _files.Where(pair => pair.Value.Complete && pair.Value.SourceType == start.SourceType && pair.Value.Size > start.Size && _heads.TryGetValue(pair.Key, out var head) && head.AsSpan().SequenceEqual(start.HeadSha256))
                               .Select(pair => pair.Value)
                               .OrderByDescending(file => file.Size)
                               .ThenBy(file => file.Id)
                               .Take(StorageLimits.MaxImportContainers);

        foreach (var candidate in candidates)
        {
            if (_hashesAttempts.TryGetValue(candidate.Id, out var attempt)
                && LineHashes.FirstOrDefault(row => row.FileId == candidate.Id && row.Attempt == attempt && row.FirstOffset < start.Size && row.LastOffset >= start.Size) is { } spanning
                && Holds(spanning.Hashes, start.Sha256))
            {
                return candidate;
            }
        }

        return null;
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

        if (batch.Import is not null)
        {
            FileIdCalls.Add(batch.Import.FileId);
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

        if (file.Records != batch.Import!.Done || file.Complete || (batch.Import.Attempt > 0 && batch.Import.Attempt != file.Attempt))
        {
            return Task.FromException<WriteResult>(new ImportConflictException($"store: import state changed: file {file.Id} does not have {batch.Import.Done} records stored, is complete or was begun again"));
        }

        file.Records += batch.Records.Count;
        file.Complete = batch.Import.Complete;

        if (batch.Import.Complete)
        {
            _hashesAttempts.Remove(file.Id);

            if (batch.Import.Attempt > 0)
            {
                _hashesAttempts[file.Id] = batch.Import.Attempt;
            }

            LineHashes.RemoveAll(row => row.FileId == file.Id && row.Attempt != batch.Import.Attempt);
        }

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

        if (ScriptedBeginResult is not null)
        {
            return Task.FromResult(ScriptedBeginResult);
        }

        var known = _files.ContainsKey(Convert.ToHexString(start.Sha256));
        var container = known ? null : FindContainer(start);

        if (container is not null)
        {
            return Task.FromResult(new ImportFile
                                   {
                                       ContainedIn = container.Name
                                   });
        }

        var existing = GetOrCreate(start);

        if (existing.Complete)
        {
            return Task.FromResult(Copy(existing));
        }

        existing.Attempt++;
        LineHashes.RemoveAll(row => row.FileId == existing.Id && row.Attempt != existing.Attempt);

        return Task.FromResult(Copy(existing));
    }

    /// <inheritdoc />
    public Task AddLineHashesAsync(ImportLineHashes hashes, CancellationToken cancellationToken)
    {
        LineHashCalls++;
        FileIdCalls.Add(hashes.FileId);
        OnLineHashes?.Invoke(LineHashCalls);

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        if (LineHashFailure is not null && LineHashCalls >= FailFromLineHashCall)
        {
            var failure = LineHashFailure;

            if (LineHashFailOnce)
            {
                LineHashFailure = null;
            }

            return Task.FromException(failure);
        }

        var file = _files.Values.FirstOrDefault(candidate => candidate.Id == hashes.FileId);

        if (file is null || file.Complete || file.Attempt != hashes.Attempt)
        {
            return Task.FromException(new ImportConflictException($"store: import state changed: file {hashes.FileId} is complete, unknown or was begun again"));
        }

        var row = new ImportLineHashes
                  {
                      FileId = hashes.FileId,
                      Attempt = hashes.Attempt,
                      FirstOffset = hashes.FirstOffset,
                      LastOffset = hashes.LastOffset,
                      Hashes = [.. hashes.Hashes]
                  };

        LineHashes.Add(row);
        LineHashWrites.Add(row);

        return Task.CompletedTask;
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