using Vandox.Core.LogParsing;
using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.Import;

/// <summary>
/// Receives the records of a parser, drops those an earlier run stored and writes the rest in batches.
/// </summary>
internal sealed class RecordEmitter : IRecordEmitter
{
    #region Fields

    private readonly ImportOptions _options;
    private readonly IImportStore _store;
    private readonly FoundFile _item;
    private readonly ContentStream _content;
    private readonly ImportStep _step;
    private readonly List<DataRecord> _buffer = [];
    private long _drop;
    private long _startBytes;
    private bool _atStart;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="RecordEmitter"/> class.
    /// </summary>
    /// <param name="options">The options of the run, with defaults filled in</param>
    /// <param name="item">The file being imported</param>
    /// <param name="content">The content the parser reads</param>
    /// <param name="state">The stored import state of the content</param>
    internal RecordEmitter(ImportOptions options, FoundFile item, ContentStream content, ImportFile state)
    {
        _options = options;
        _store = options.Store!;
        _item = item;
        _content = content;
        _step = new ImportStep
                {
                    FileId = state.Id,
                    Done = state.Records,
                    Attempt = state.Attempt
                };
        _drop = state.Records;
        _atStart = state.Offset > 0;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the first exception of the store; sticky.
    /// </summary>
    internal Exception? Error { get; private set; }

    /// <summary>
    /// Gets the lines before the continuation point, added to the line numbers of skipped lines.
    /// </summary>
    internal long LineOffset { get; set; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Stores one row of line hashes for the file, with the file ID and the attempt of its import state.
    /// </summary>
    /// <param name="hashes">The row, with <c>FileId</c> and <c>Attempt</c> not set</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that completes when the row is stored</returns>
    internal async Task WriteLineHashesAsync(ImportLineHashes hashes, CancellationToken cancellationToken)
    {
        if (Error is not null)
        {
            throw Error;
        }

        hashes.FileId = _step.FileId;
        hashes.Attempt = _step.Attempt;

        try
        {
            await _store.AddLineHashesAsync(hashes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Error = exception;

            throw;
        }
    }

    /// <summary>
    /// Writes the buffered records, completing the file when <paramref name="complete"/> is set. It writes nothing when there
    /// is nothing to write and the file is not completed.
    /// </summary>
    /// <param name="complete">Whether the file is completely imported after this batch</param>
    /// <param name="cancellationToken">Cancels the write</param>
    /// <returns>A task that completes when the batch is written; the exception of a failed write is also kept in <see cref="Error"/></returns>
    internal async Task FlushAsync(bool complete, CancellationToken cancellationToken)
    {
        if (Error is not null)
        {
            throw Error;
        }

        if (_buffer.Count > 0 || complete)
        {
            await WriteBatchAsync(complete, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads the rest of the hashed content, which the parser may have left unread, and checks that exactly the content hashed
    /// in pass 1 was read.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns <c>true</c> when the content is the one hashed in pass 1</returns>
    internal async Task<bool> VerifyAsync(CancellationToken cancellationToken)
    {
        await _content.CopyToAsync(Stream.Null, cancellationToken).ConfigureAwait(false);

        return _content.BytesRead >= _item.Size && _content.GetHash().AsSpan().SequenceEqual(_item.Sum);
    }

    /// <summary>
    /// Writes the buffered records as one batch.
    /// </summary>
    /// <param name="complete">Whether the file is completely imported after this batch</param>
    /// <param name="cancellationToken">Cancels the write</param>
    /// <returns>A task that completes when the batch is written</returns>
    private async Task WriteBatchAsync(bool complete, CancellationToken cancellationToken)
    {
        var batch = new RecordBatch
                    {
                        ReceivedAt = _options.Clock!.GetUtcNow(),
                        Records = [.. _buffer],
                        Import = new ImportStep
                                 {
                                     FileId = _step.FileId,
                                     Done = _step.Done,
                                     Complete = complete,
                                     Attempt = _step.Attempt
                                 }
                    };

        try
        {
            await _store.WriteBatchAsync(batch, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Error = exception;

            throw;
        }

        var result = _item.Result;

        foreach (var captured in _buffer.Select(record => record.CapturedAt))
        {
            result.First = result.First is null || captured < result.First ? captured : result.First;
            result.Last = result.Last is null || captured > result.Last ? captured : result.Last;
        }

        result.Records += _buffer.Count;
        _step.Done += _buffer.Count;
        _buffer.Clear();
    }

    #endregion // Methods

    #region IRecordEmitter

    /// <inheritdoc />
    public async ValueTask RecordAsync(DataRecord record, CancellationToken cancellationToken)
    {
        if (Error is not null)
        {
            throw Error;
        }

        _atStart = false;

        var refusal = RecordRules.CheckImportRecord(record);

        if (refusal is not null)
        {
            Skip(0, $"record refused: {refusal}");

            return;
        }

        if (_drop > 0)
        {
            _drop--;

            return;
        }

        if (_buffer.Count == 0)
        {
            _startBytes = _content.BytesRead;
        }

        _buffer.Add(record);

        if (_buffer.Count >= _options.BatchRecords || _content.BytesRead - _startBytes >= _options.BatchBytes)
        {
            await FlushAsync(false, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void Skip(long line, string reason)
    {
        var result = _item.Result;

        result.Skipped++;

        if (line > 0 && _atStart)
        {
            result.SkippedAtStart++;
        }

        if (result.Problems.Count < ImportLimits.MaxProblems)
        {
            result.Problems.Add(new ImportProblem(line > 0 ? line + LineOffset : line, reason));
        }
    }

    /// <inheritdoc />
    public void Warn(long line, string reason)
    {
        var result = _item.Result;

        result.Raw++;

        if (result.RawProblems.Count < ImportLimits.MaxProblems)
        {
            result.RawProblems.Add(new ImportProblem(line > 0 ? line + LineOffset : line, reason));
        }
    }

    #endregion // IRecordEmitter
}