using Vandox.Core.IO;
using Vandox.Storage;

namespace Vandox.Import;

/// <summary>
/// Imports log files, directories and archives into the store. Pass 1 lists every file and hashes the content of the files a
/// parser recognizes; pass 2 writes the parsers' records in resumable batches, so the same content is never stored twice.
/// </summary>
public static class Importer
{
    #region Methods

    /// <summary>
    /// Imports a directory, an archive or a file.
    /// </summary>
    /// <param name="root">The path of the input</param>
    /// <param name="options">The options</param>
    /// <param name="cancellationToken">Interrupts the run</param>
    /// <returns>A task that returns the summary and the error that stopped the run, if any</returns>
    /// <exception cref="ArgumentException">A required option is missing or out of range</exception>
    public static async Task<ImportRun> RunAsync(string root, ImportOptions options, CancellationToken cancellationToken)
    {
        StrictGzip.Require();

        var effective = Normalize(options);
        var summary = new ImportSummary
                      {
                          Root = root,
                          Started = effective.Clock!.GetUtcNow()
                      };
        SourceRoot source;

        try
        {
            source = SourceRoot.Open(root);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            summary.Finished = effective.Clock.GetUtcNow();

            return new ImportRun(summary, new ImportException($"importer: opening the input: {ImportReasons.Of(exception)}", exception));
        }

        using (source)
        {
            var scanner = new Scanner(source, effective);
            Exception? error = null;

            try
            {
                await scanner.ScanAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is TooManyFilesException or OperationCanceledException)
            {
                error = exception;
            }

            if (error is null)
            {
                var pending = scanner.Found.Count(file => file.Parser is not null);

                effective.Progress?.Invoke(new ImportProgress
                                           {
                                               Event = ProgressEvent.Scanned,
                                               Files = scanner.Found.Count,
                                               Pending = pending
                                           });
                error = await new ImportPass(source, effective, scanner.Found).RunAsync(cancellationToken).ConfigureAwait(false);
            }

            summary.Interrupted = error is OperationCanceledException && cancellationToken.IsCancellationRequested;
            Summarize(summary, scanner.Found);
            summary.Finished = effective.Clock.GetUtcNow();

            return new ImportRun(summary, error);
        }
    }

    /// <summary>
    /// Checks the options and replaces zero bounds by their defaults.
    /// </summary>
    /// <param name="options">The options</param>
    /// <returns>A copy with the defaults filled in</returns>
    private static ImportOptions Normalize(ImportOptions options)
    {
        if (options.Parsers is null || options.Store is null || options.Clock is null)
        {
            throw new ArgumentException("importer: parsers, store and clock are required", nameof(options));
        }

        if (options.BatchRecords < 0 || options.BatchRecords > StorageLimits.MaxBatchRecords)
        {
            throw new ArgumentException($"importer: BatchRecords must be between 0 and {StorageLimits.MaxBatchRecords}", nameof(options));
        }

        if (options.BatchBytes < 0 || options.ProgressBytes < 0)
        {
            throw new ArgumentException("importer: BatchBytes and ProgressBytes must not be negative", nameof(options));
        }

        return new ImportOptions
               {
                   Parsers = options.Parsers,
                   Store = options.Store,
                   Clock = options.Clock,
                   Progress = options.Progress,
                   BatchRecords = options.BatchRecords == 0 ? ImportLimits.DefaultBatchRecords : options.BatchRecords,
                   BatchBytes = options.BatchBytes == 0 ? ImportLimits.DefaultBatchBytes : options.BatchBytes,
                   ProgressBytes = options.ProgressBytes == 0 ? ImportLimits.DefaultProgressBytes : options.ProgressBytes
               };
    }

    /// <summary>
    /// Fills the files and the totals of the summary from the files that have an outcome.
    /// </summary>
    /// <param name="summary">The summary</param>
    /// <param name="items">The files of pass 1</param>
    private static void Summarize(ImportSummary summary, List<FoundFile> items)
    {
        foreach (var result in items.Select(item => item.Result).Where(result => result.Outcome != ImportOutcome.None))
        {
            summary.Files.Add(result);
            summary.Lines += result.Lines;
            summary.Records += result.Records;
            summary.Skipped += result.Skipped;
            summary.First = result.First is { } first && (summary.First is null || first < summary.First) ? first : summary.First;
            summary.Last = result.Last is { } last && (summary.Last is null || last > summary.Last) ? last : summary.Last;
        }
    }

    #endregion // Methods
}