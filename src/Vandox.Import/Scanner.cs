using System.Formats.Tar;
using System.IO.Compression;

using Vandox.Core.IO;
using Vandox.Core.LogParsing;

namespace Vandox.Import;

/// <summary>
/// Pass 1 of a run: walks the input, lists every file and hashes the content of the files a parser recognizes.
/// </summary>
internal sealed class Scanner
{
    #region Constants

    private const string ReasonSymlink = "symbolic link (not followed)";
    private const string ReasonHardLink = "hard link (the content is in the linked entry)";
    private const string ReasonNotRegular = "not a regular file";
    private const string ReasonEmpty = "empty";
    private const string ReasonTwice = "compressed twice";
    private const string ReasonNested = "archive inside an archive (not opened)";
    private const string ReasonNoParser = "no parser recognized the file";
    private const string ReasonTooLong = "path too long";

    #endregion // Constants

    #region Fields

    private readonly SourceRoot _source;
    private readonly ImportOptions _options;
    private readonly long _progressBytes;
    private readonly List<FoundFile> _found = [];
    private int _entries;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="Scanner"/> class.
    /// </summary>
    /// <param name="source">The opened import root</param>
    /// <param name="options">The options of the run, with defaults filled in</param>
    internal Scanner(SourceRoot source, ImportOptions options)
    {
        _source = source;
        _options = options;
        _progressBytes = options.ProgressBytes > 0 ? options.ProgressBytes : ImportLimits.DefaultProgressBytes;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the files found so far, in input order.
    /// </summary>
    internal List<FoundFile> Found => _found;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Walks the source, lists every file and hashes the recognized ones. The files are in <see cref="Found"/>, also when an
    /// exception ends the walk.
    /// </summary>
    /// <param name="cancellationToken">Cancels the walk</param>
    /// <returns>A task that completes when the walk is done</returns>
    /// <exception cref="TooManyFilesException">The input holds more than <see cref="ImportLimits.MaxFiles"/> entries</exception>
    internal async Task ScanAsync(CancellationToken cancellationToken)
    {
        if (_source.File.Length > 0)
        {
            await RegularFileAsync(_source.File, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await WalkDirectoryAsync(".", cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Tells whether an exception is a failure of one file: unreadable, corrupt or truncated content, as opposed to a
    /// cancellation or the end of the run.
    /// </summary>
    /// <param name="exception">The exception</param>
    /// <returns><c>true</c> when the exception fails one file</returns>
    private static bool IsFailure(Exception exception)
    {
        return exception is IOException or InvalidDataException or UnauthorizedAccessException or EndOfStreamException;
    }

    /// <summary>
    /// Reads up to <see cref="ILogParser.SniffBytes"/> bytes.
    /// </summary>
    /// <param name="stream">The stream</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the head</returns>
    private static async Task<byte[]> ReadHeadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[ILogParser.SniffBytes];
        var total = 0;
        int read;

        do
        {
            read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            total += read;
        }
        while (read > 0 && total < buffer.Length);

        return buffer[..total];
    }

    /// <summary>
    /// Counts one more entry.
    /// </summary>
    /// <exception cref="TooManyFilesException">The count exceeds <see cref="ImportLimits.MaxFiles"/></exception>
    private void Count()
    {
        _entries++;

        if (_entries > ImportLimits.MaxFiles)
        {
            throw new TooManyFilesException();
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
    /// Lists the directory below the root, counting its entries while it reads them, and examines them in lexical order.
    /// </summary>
    /// <param name="relative">The directory; <c>.</c> is the root</param>
    /// <param name="cancellationToken">Cancels the walk</param>
    /// <returns>A task that completes when the directory is done</returns>
    private async Task WalkDirectoryAsync(string relative, CancellationToken cancellationToken)
    {
        List<string> names;

        try
        {
            names = ReadNames(relative, cancellationToken);
        }
        catch (Exception exception) when (relative != "." && IsFailure(exception))
        {
            List(new ScanItem
                 {
                     Display = PathText.Cut(relative),
                     Name = relative,
                     Location = new ImportLocation
                                {
                                    FsPath = relative
                                }
                 },
                 ImportOutcome.Failed,
                 ImportReasons.Of(exception));

            return;
        }

        foreach (var name in names)
        {
            await DirectoryEntryAsync(relative, name, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads the names of a directory, counting them, and sorts them by name.
    /// </summary>
    /// <param name="relative">The directory</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>The sorted names</returns>
    private List<string> ReadNames(string relative, CancellationToken cancellationToken)
    {
        var names = new List<string>();

        foreach (var name in _source.Root.ListNames(relative))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Count();
            names.Add(name);
        }

        names.Sort(StringComparer.Ordinal);

        return names;
    }

    /// <summary>
    /// Examines one entry of a directory.
    /// </summary>
    /// <param name="directory">The directory</param>
    /// <param name="name">The name of the entry</param>
    /// <param name="cancellationToken">Cancels the walk</param>
    /// <returns>A task that completes when the entry is done</returns>
    private async Task DirectoryEntryAsync(string directory, string name, CancellationToken cancellationToken)
    {
        var relative = directory == "." ? name : $"{directory}/{name}";
        var item = new ScanItem
                   {
                       Display = PathText.Cut(relative),
                       Name = relative,
                       Location = new ImportLocation
                                  {
                                      FsPath = relative
                                  }
                   };

        if (PathText.IsTooLong(relative))
        {
            List(item, ImportOutcome.Failed, ReasonTooLong);

            return;
        }

        switch (_source.Root.GetKind(relative))
        {
            case FileKind.Directory:
                await WalkDirectoryAsync(relative, cancellationToken).ConfigureAwait(false);
                break;

            case FileKind.Symlink:
                List(item, ImportOutcome.Unrecognized, ReasonSymlink);
                break;

            case FileKind.Regular:
                await RegularFileAsync(relative, cancellationToken).ConfigureAwait(false);
                break;

            default:
                List(item, ImportOutcome.Unrecognized, ReasonNotRegular);
                break;
        }
    }

    /// <summary>
    /// Opens a file of the root and examines its content.
    /// </summary>
    /// <param name="relative">The file</param>
    /// <param name="cancellationToken">Cancels the scan</param>
    /// <returns>A task that completes when the file is done</returns>
    private async Task RegularFileAsync(string relative, CancellationToken cancellationToken)
    {
        var item = new ScanItem
                   {
                       Display = relative,
                       Name = relative,
                       Location = new ImportLocation
                                  {
                                      FsPath = relative
                                  }
                   };

        try
        {
            await using var file = _source.Root.OpenRegular(relative);

            item.Modified = File.GetLastWriteTimeUtc(file.SafeFileHandle);
            await StreamAsync(item, file, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsFailure(exception))
        {
            Fail(item, exception);
        }
    }

    /// <summary>
    /// Records a file that is not imported and reports it as finished.
    /// </summary>
    /// <param name="item">The item</param>
    /// <param name="outcome">The outcome</param>
    /// <param name="reason">Why the file is not imported</param>
    private void List(ScanItem item, ImportOutcome outcome, string reason)
    {
        var found = new FoundFile
                    {
                        Result = new FileResult
                                 {
                                     Path = item.Display,
                                     Outcome = outcome,
                                     Reason = reason
                                 },
                        File = new LogFile(PathText.Cut(item.Name), item.Modified),
                        Location = item.Location
                    };

        _found.Add(found);
        Report(new ImportProgress
               {
                   Event = ProgressEvent.FileFinished,
                   Path = item.Display,
                   Result = found.Result.Snapshot()
               });
    }

    /// <summary>
    /// Lists an item as failed with the reason of an exception. An exception that must stop the scan instead is thrown: the
    /// error of the raw content of an archive entry, which ends the archive.
    /// </summary>
    /// <param name="item">The item</param>
    /// <param name="exception">What went wrong</param>
    private void Fail(ScanItem item, Exception exception)
    {
        if (item.Tracker?.Error is { } archiveError)
        {
            throw archiveError;
        }

        List(item, ImportOutcome.Failed, ImportReasons.Of(exception));
    }

    /// <summary>
    /// Examines the content of an item: one gzip layer is recognized, then the content is listed, opened as an archive or
    /// handed to the parsers.
    /// </summary>
    /// <param name="item">The item</param>
    /// <param name="raw">The raw content</param>
    /// <param name="cancellationToken">Cancels the scan</param>
    /// <returns>A task that completes when the item is done</returns>
    private async Task StreamAsync(ScanItem item, Stream raw, CancellationToken cancellationToken)
    {
        var head = await ReadHeadAsync(raw, cancellationToken).ConfigureAwait(false);
        var (format, unsupported) = FormatSniffer.Sniff(head);

        Stream content = new PrefixStream(head, raw);

        if (format == ContentFormat.Gzip)
        {
            item.Location.Gzip = true;
            item.Name = PathText.TrimGzip(item.Name);

            var gzip = new GZipStream(content, CompressionMode.Decompress);

            head = await ReadHeadAsync(gzip, cancellationToken).ConfigureAwait(false);
            (format, unsupported) = FormatSniffer.Sniff(head);
            content = new PrefixStream(head, gzip);
        }

        await ClassifyAsync(item, format, unsupported, head, content, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Acts on the format of the decompressed content.
    /// </summary>
    /// <param name="item">The item</param>
    /// <param name="format">The format</param>
    /// <param name="unsupported">The name of an unsupported format</param>
    /// <param name="head">The head of the content</param>
    /// <param name="content">The content, starting with the head</param>
    /// <param name="cancellationToken">Cancels the scan</param>
    /// <returns>A task that completes when the item is done</returns>
    private async Task ClassifyAsync(ScanItem item, ContentFormat format, string unsupported, byte[] head, Stream content, CancellationToken cancellationToken)
    {
        switch (format)
        {
            case ContentFormat.Gzip:
                {
                    List(item, ImportOutcome.Unrecognized, ReasonTwice);
                }
                break;

            case ContentFormat.Empty:
                {
                    List(item, ImportOutcome.Unrecognized, ReasonEmpty);
                }
                break;

            case ContentFormat.Unsupported:
                {
                    List(item, ImportOutcome.Unrecognized, $"unsupported format: {unsupported}");
                }
                break;

            case ContentFormat.Tar:
                {
                    if (item.InArchive)
                    {
                        List(item, ImportOutcome.Unrecognized, ReasonNested);
                    }
                    else
                    {
                        await ArchiveAsync(item, content, cancellationToken).ConfigureAwait(false);
                    }
                }
                break;

            default:
                {
                    await RecognizeAsync(item, head, content, cancellationToken).ConfigureAwait(false);
                }
                break;
        }
    }

    /// <summary>
    /// Lets the registry pick a parser for the file and, when one claims it, reads the content to its end to hash it.
    /// </summary>
    /// <param name="item">The item</param>
    /// <param name="head">The head of the content</param>
    /// <param name="content">The content</param>
    /// <param name="cancellationToken">Cancels the scan</param>
    /// <returns>A task that completes when the item is done</returns>
    private async Task RecognizeAsync(ScanItem item, byte[] head, Stream content, CancellationToken cancellationToken)
    {
        var file = new LogFile(item.Name, item.Modified);
        var (parser, _) = _options.Parsers!.Detect(file, head);

        if (parser is null)
        {
            List(item, ImportOutcome.Unrecognized, ReasonNoParser);

            return;
        }

        await using var hashing = new HashingStream(content,
                                                    _progressBytes,
                                                    mark => Report(new ImportProgress
                                                                   {
                                                                       Event = ProgressEvent.ScanProgress,
                                                                       Path = item.Display,
                                                                       Bytes = mark
                                                                   }));

        await hashing.CopyToAsync(Stream.Null, cancellationToken).ConfigureAwait(false);

        _found.Add(new FoundFile
                   {
                       Result = new FileResult
                                {
                                    Path = item.Display,
                                    SourceType = parser.Type
                                },
                       File = file,
                       Location = item.Location,
                       Parser = parser,
                       Size = hashing.BytesRead,
                       Sum = hashing.GetHash()
                   });
    }

    /// <summary>
    /// Reads a tar archive and examines its entries; an error of the archive itself is listed as the archive's failure.
    /// </summary>
    /// <param name="item">The archive</param>
    /// <param name="content">The decompressed content of the archive</param>
    /// <param name="cancellationToken">Cancels the scan</param>
    /// <returns>A task that completes when the archive is done</returns>
    private async Task ArchiveAsync(ScanItem item, Stream content, CancellationToken cancellationToken)
    {
        try
        {
            await using var reader = new TarReader(new TarHeaderGuardStream(content), leaveOpen: true);
            var index = 0;

            while (await reader.GetNextEntryAsync(false, cancellationToken).ConfigureAwait(false) is { } entry)
            {
                await TarEntryAsync(item, index, entry, cancellationToken).ConfigureAwait(false);
                index++;
            }
        }
        catch (Exception exception) when (IsFailure(exception))
        {
            List(new ScanItem
                 {
                     Display = item.Display,
                     Name = item.Name,
                     Modified = item.Modified,
                     Location = new ImportLocation
                                {
                                    FsPath = item.Location.FsPath
                                }
                 },
                 ImportOutcome.Failed,
                 ImportReasons.Of(exception));
        }
    }

    /// <summary>
    /// Examines one header of an archive.
    /// </summary>
    /// <param name="archive">The archive</param>
    /// <param name="index">The ordinal of the entry among the archive's headers</param>
    /// <param name="entry">The entry</param>
    /// <param name="cancellationToken">Cancels the scan</param>
    /// <returns>A task that completes when the entry is done</returns>
    private async Task TarEntryAsync(ScanItem archive, int index, TarEntry entry, CancellationToken cancellationToken)
    {
        if (entry.EntryType == TarEntryType.GlobalExtendedAttributes)
        {
            return;
        }

        Count();

        if (entry.EntryType == TarEntryType.Directory)
        {
            return;
        }

        var name = PathText.CleanName(entry.Name);
        var item = new ScanItem
                   {
                       Display = $"{archive.Display}:{PathText.Cut(name)}",
                       Name = name,
                       Modified = entry.ModificationTime.ToUniversalTime(),
                       Location = new ImportLocation
                                  {
                                      FsPath = archive.Location.FsPath,
                                      Entry = index,
                                      ArchiveGzip = archive.Location.Gzip
                                  },
                       InArchive = true
                   };

        if (PathText.IsTooLong(name))
        {
            List(item, ImportOutcome.Failed, ReasonTooLong);
        }
        else if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile)
        {
            item.Tracker = new ErrorTrackingStream(entry.DataStream ?? Stream.Null);

            try
            {
                await StreamAsync(item, item.Tracker, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsFailure(exception))
            {
                Fail(item, exception);
            }
        }
        else if (entry.EntryType == TarEntryType.SymbolicLink)
        {
            List(item, ImportOutcome.Unrecognized, ReasonSymlink);
        }
        else if (entry.EntryType == TarEntryType.HardLink)
        {
            List(item, ImportOutcome.Unrecognized, ReasonHardLink);
        }
        else
        {
            List(item, ImportOutcome.Unrecognized, ReasonNotRegular);
        }
    }

    #endregion // Methods
}