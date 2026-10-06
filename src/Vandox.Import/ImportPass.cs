using System.Formats.Tar;
using System.IO.Compression;

using Vandox.Core.LogParsing;
using Vandox.Storage;

namespace Vandox.Import;

/// <summary>
/// Pass 2 of a run: imports the recognized files in input order. The entries of one archive are imported in a single pass over
/// the archive.
/// </summary>
internal sealed class ImportPass
{
    #region Fields

    private readonly SourceRoot _source;
    private readonly ImportOptions _options;
    private readonly List<FoundFile> _items;
    private Exception? _fatal;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ImportPass"/> class.
    /// </summary>
    /// <param name="source">The opened import root</param>
    /// <param name="options">The options of the run, with defaults filled in</param>
    /// <param name="items">The files of pass 1</param>
    internal ImportPass(SourceRoot source, ImportOptions options, List<FoundFile> items)
    {
        _source = source;
        _options = options;
        _items = items;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Imports the recognized files in input order.
    /// </summary>
    /// <param name="cancellationToken">Cancels the import</param>
    /// <returns>A task that returns the exception that ended the run, or <c>null</c> when every file was handled</returns>
    internal async Task<Exception?> RunAsync(CancellationToken cancellationToken)
    {
        var index = 0;

        while (index < _items.Count && _fatal is null)
        {
            var item = _items[index];

            if (item.Parser is null)
            {
                index++;
            }
            else if (item.Location.Entry < 0)
            {
                await ImportPlainAsync(item, cancellationToken).ConfigureAwait(false);
                index++;
            }
            else
            {
                index = await ImportArchiveAsync(index, cancellationToken).ConfigureAwait(false);
            }
        }

        return _fatal;
    }

    /// <summary>
    /// Returns the stream, or its gzip decompression when it is gzipped.
    /// </summary>
    /// <param name="stream">The stream</param>
    /// <param name="gzipped">Whether the stream is gzip-compressed</param>
    /// <returns>The stream to read</returns>
    private static Stream Decompress(Stream stream, bool gzipped)
    {
        return gzipped ? new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true) : stream;
    }

    /// <summary>
    /// Tells whether an exception fails one file, as opposed to ending the run.
    /// </summary>
    /// <param name="exception">The exception</param>
    /// <returns><c>true</c> when the exception fails one file</returns>
    private static bool IsFileFailure(Exception exception)
    {
        return exception is IOException or InvalidDataException or UnauthorizedAccessException or EndOfStreamException;
    }

    /// <summary>
    /// Stores what the emitter still holds: after a parser error what was parsed before it, otherwise, once the whole hashed
    /// content was read again unchanged, the last batch that completes the file.
    /// </summary>
    /// <param name="emitter">The emitter</param>
    /// <param name="parseError">The exception of the parser, if any</param>
    /// <param name="cancellationToken">Cancels the import</param>
    /// <returns>A task that returns the error that failed the file, if any</returns>
    private static async Task<Exception?> SettleAsync(RecordEmitter emitter, Exception? parseError, CancellationToken cancellationToken)
    {
        if (parseError is not null)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return parseError;
            }

            await TryFlushAsync(emitter, false, cancellationToken).ConfigureAwait(false);

            return parseError;
        }

        try
        {
            if (await emitter.VerifyAsync(cancellationToken).ConfigureAwait(false))
            {
                await TryFlushAsync(emitter, true, cancellationToken).ConfigureAwait(false);

                return null;
            }

            return new ContentChangedException();
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or EndOfStreamException)
        {
            return exception;
        }
    }

    /// <summary>
    /// Writes a batch; a failure is kept in the emitter and not thrown.
    /// </summary>
    /// <param name="emitter">The emitter</param>
    /// <param name="complete">Whether the batch completes the file</param>
    /// <param name="cancellationToken">Cancels the write</param>
    /// <returns>A task that completes when the batch is written or failed</returns>
    private static async Task TryFlushAsync(RecordEmitter emitter, bool complete, CancellationToken cancellationToken)
    {
        try
        {
            await emitter.FlushAsync(complete, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The emitter keeps the first error of the store; the caller reads it from there.
        }
    }

    /// <summary>
    /// Passes a progress event to the callback, if any.
    /// </summary>
    /// <param name="progress">The event</param>
    private void Report(ImportProgress progress)
    {
        _options.Progress?.Invoke(progress);
    }

    /// <summary>
    /// Imports a file of the root that may be gzip-compressed.
    /// </summary>
    /// <param name="item">The file</param>
    /// <param name="cancellationToken">Cancels the import</param>
    /// <returns>A task that completes when the file is done</returns>
    private async Task ImportPlainAsync(FoundFile item, CancellationToken cancellationToken)
    {
        var state = await BeginAsync(item, cancellationToken).ConfigureAwait(false);

        if (state is null)
        {
            return;
        }

        try
        {
            await using var file = _source.Root.OpenRegular(item.Location.FsPath);
            await using var content = Decompress(file, item.Location.Gzip);

            await ImportContentAsync(item, state, content, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            FailFile(item, ImportReasons.Of(exception));
        }
    }

    /// <summary>
    /// Imports the recognized entries of the archive that holds the item at <paramref name="first"/> in one pass over the
    /// archive.
    /// </summary>
    /// <param name="first">The index of the first item of the archive</param>
    /// <param name="cancellationToken">Cancels the import</param>
    /// <returns>A task that returns the index of the first item after the archive's items</returns>
    private async Task<int> ImportArchiveAsync(int first, CancellationToken cancellationToken)
    {
        var end = first;
        var group = new List<FoundFile>();

        while (end < _items.Count && _items[end].Location.FsPath == _items[first].Location.FsPath && _items[end].Location.Entry >= 0)
        {
            if (_items[end].Parser is not null)
            {
                group.Add(_items[end]);
            }

            end++;
        }

        var handled = 0;
        string? reason = null;

        try
        {
            handled = await ReadArchiveAsync(_items[first].Location, group, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            reason = ImportReasons.Of(exception);
        }

        if (_fatal is not null)
        {
            return end;
        }

        reason ??= "the archive changed while it was imported";

        foreach (var item in group.Skip(handled))
        {
            FailFile(item, reason);
        }

        return end;
    }

    /// <summary>
    /// Opens an archive and imports the entries of <paramref name="group"/>, which are in ascending order of their ordinals.
    /// </summary>
    /// <param name="location">Where the archive is</param>
    /// <param name="group">The entries to import</param>
    /// <param name="cancellationToken">Cancels the import</param>
    /// <returns>A task that returns how many entries of the group were handled</returns>
    private async Task<int> ReadArchiveAsync(ImportLocation location, List<FoundFile> group, CancellationToken cancellationToken)
    {
        await using var file = _source.Root.OpenRegular(location.FsPath);
        await using var raw = Decompress(file, location.ArchiveGzip);
        await using var reader = new TarReader(new TarHeaderGuardStream(raw), leaveOpen: true);
        var position = 0;
        var index = 0;

        while (position < group.Count && _fatal is null && await reader.GetNextEntryAsync(false, cancellationToken).ConfigureAwait(false) is { } entry)
        {
            if (group[position].Location.Entry == index)
            {
                var item = group[position];

                position++;
                await ImportEntryAsync(item, entry, cancellationToken).ConfigureAwait(false);
            }

            index++;
        }

        return position;
    }

    /// <summary>
    /// Imports an archive entry.
    /// </summary>
    /// <param name="item">The entry</param>
    /// <param name="entry">The entry of the archive</param>
    /// <param name="cancellationToken">Cancels the import</param>
    /// <returns>A task that completes when the entry is done</returns>
    private async Task ImportEntryAsync(FoundFile item, TarEntry entry, CancellationToken cancellationToken)
    {
        var state = await BeginAsync(item, cancellationToken).ConfigureAwait(false);

        if (state is null)
        {
            return;
        }

        try
        {
            await using var content = Decompress(entry.DataStream ?? Stream.Null, item.Location.Gzip);

            await ImportContentAsync(item, state, content, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            FailFile(item, ImportReasons.Of(exception));
        }
    }

    /// <summary>
    /// Asks the store about the content of a file.
    /// </summary>
    /// <param name="item">The file</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that returns the stored state when the content is to be imported (again), otherwise <c>null</c> and the result of the file is final</returns>
    private async Task<ImportFile?> BeginAsync(FoundFile item, CancellationToken cancellationToken)
    {
        ImportFile state;

        try
        {
            state = await _options.Store!.BeginImportAsync(new ImportFileStart
                                                           {
                                                               Sha256 = item.Sum,
                                                               Size = item.Size,
                                                               Name = item.Result.Path,
                                                               FileName = item.File.Name,
                                                               ModTime = item.File.ModTime,
                                                               SourceType = item.Parser!.Type,
                                                               StartedAt = _options.Clock!.GetUtcNow()
                                                           },
                                                           cancellationToken)
                                        .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is StoreException or OperationCanceledException)
        {
            FailFile(item, StoreReason(exception, cancellationToken));

            return null;
        }

        if (state.Complete)
        {
            item.Result.Outcome = ImportOutcome.AlreadyImported;
            Finished(item);

            return null;
        }

        if (state.SourceType != item.Parser.Type)
        {
            FailFile(item, $"the content was imported before as source type \"{state.SourceType}\"");

            return null;
        }

        return state;
    }

    /// <summary>
    /// Ends the import of a file as failed.
    /// </summary>
    /// <param name="item">The file</param>
    /// <param name="reason">Why it failed</param>
    private void FailFile(FoundFile item, string reason)
    {
        item.Result.Outcome = ImportOutcome.Failed;
        item.Result.Reason = reason;
        Finished(item);
    }

    /// <summary>
    /// Reports a file as finished.
    /// </summary>
    /// <param name="item">The file</param>
    private void Finished(FoundFile item)
    {
        var result = item.Result;

        Report(new ImportProgress
               {
                   Event = ProgressEvent.FileFinished,
                   Path = result.Path,
                   SourceType = result.SourceType,
                   Lines = result.Lines,
                   Records = result.Records,
                   Result = result.Snapshot()
               });
    }

    /// <summary>
    /// Parses the content of a file and stores its records.
    /// </summary>
    /// <param name="item">The file</param>
    /// <param name="state">The stored import state</param>
    /// <param name="content">The decompressed content</param>
    /// <param name="cancellationToken">Cancels the import</param>
    /// <returns>A task that completes when the file is done</returns>
    private async Task ImportContentAsync(FoundFile item, ImportFile state, Stream content, CancellationToken cancellationToken)
    {
        var result = item.Result;

        result.ResumedAfter = state.Records;
        Report(new ImportProgress
               {
                   Event = ProgressEvent.FileStarted,
                   Path = result.Path,
                   SourceType = result.SourceType
               });

        using var counted = new ContentStream(content,
                                              item.Size,
                                              lines => Report(new ImportProgress
                                                              {
                                                                  Event = ProgressEvent.FileProgress,
                                                                  Path = result.Path,
                                                                  SourceType = result.SourceType,
                                                                  Lines = lines,
                                                                  Records = result.Records
                                                              }));
        var emitter = new RecordEmitter(_options, item, counted, state);
        Exception? parseError = null;

        try
        {
            await item.Parser!.ParseAsync(new LogFile(state.FileName, state.ModTime), counted, emitter, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || cancellationToken.IsCancellationRequested)
        {
            parseError = exception;
        }

        var reason = await JudgeAsync(emitter, parseError, cancellationToken).ConfigureAwait(false);

        result.Lines = counted.Lines;

        if (reason.Length > 0)
        {
            FailFile(item, reason);

            return;
        }

        result.Outcome = ImportOutcome.Imported;
        Finished(item);
    }

    /// <summary>
    /// Finishes the import of a content after the parser returned and returns why it failed, or an empty text when it was
    /// imported completely.
    /// </summary>
    /// <param name="emitter">The emitter</param>
    /// <param name="parseError">The exception of the parser, if any</param>
    /// <param name="cancellationToken">Cancels the import</param>
    /// <returns>A task that returns the reason, or an empty text</returns>
    private async Task<string> JudgeAsync(RecordEmitter emitter, Exception? parseError, CancellationToken cancellationToken)
    {
        var error = parseError;

        if (emitter.Error is null)
        {
            error = await SettleAsync(emitter, parseError, cancellationToken).ConfigureAwait(false);
        }

        if (emitter.Error is not null)
        {
            return StoreReason(emitter.Error, cancellationToken);
        }

        if (error is null)
        {
            return string.Empty;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            _fatal = new OperationCanceledException(cancellationToken);

            return "interrupted";
        }

        return error is IOException or InvalidDataException or UnauthorizedAccessException or EndOfStreamException ? ImportReasons.Of(error) : error.Message;
    }

    /// <summary>
    /// Returns the reason for a failed store call. A cancelled run is an interruption, and a store error other than a conflict
    /// ends the run.
    /// </summary>
    /// <param name="exception">The exception of the store</param>
    /// <param name="cancellationToken">The token of the run</param>
    /// <returns>The reason</returns>
    private string StoreReason(Exception exception, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || exception is OperationCanceledException)
        {
            _fatal = new OperationCanceledException(cancellationToken);

            return "interrupted";
        }

        if (exception is ImportConflictException)
        {
            return "the import state changed (another import of the same content is running?)";
        }

        _fatal = new ImportException("importer: writing to the database failed", exception);

        return "database error";
    }

    #endregion // Methods
}