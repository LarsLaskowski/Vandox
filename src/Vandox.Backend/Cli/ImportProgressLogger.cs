using Microsoft.Extensions.Logging;

using Vandox.Import;

namespace Vandox.Backend.Cli;

/// <summary>
/// Logs the progress events of an import. The values that come from the input (paths, reasons) are logged as they are: the
/// JSON logger escapes control and non-ASCII characters.
/// </summary>
internal static class ImportProgressLogger
{
    #region Methods

    /// <summary>
    /// Logs one progress event.
    /// </summary>
    /// <param name="logger">The logger</param>
    /// <param name="progress">The event</param>
    internal static void Log(ILogger logger, ImportProgress progress)
    {
        switch (progress.Event)
        {
            case ProgressEvent.ScanProgress:
                logger.Hashing(progress.Path, progress.Bytes);
                break;

            case ProgressEvent.Scanned:
                logger.ScanFinished(progress.Files, progress.Pending);
                break;

            case ProgressEvent.FileStarted:
                logger.FileStarted(progress.Path, progress.SourceType);
                break;

            case ProgressEvent.FileProgress:
                logger.FileProgress(progress.Path, progress.Lines, progress.Records);
                break;

            default:
                LogFinished(logger, progress.Result);
                break;
        }
    }

    /// <summary>
    /// Returns the name of an outcome in the log.
    /// </summary>
    /// <param name="outcome">The outcome</param>
    /// <returns>The name</returns>
    private static string OutcomeName(ImportOutcome outcome)
    {
        return outcome switch
               {
                   ImportOutcome.Imported => "imported",
                   ImportOutcome.AlreadyImported => "already_imported",
                   ImportOutcome.Unrecognized => "unrecognized",
                   ImportOutcome.Failed => "failed",
                   _ => "none"
               };
    }

    /// <summary>
    /// Logs the result of one file.
    /// </summary>
    /// <param name="logger">The logger</param>
    /// <param name="result">The result</param>
    private static void LogFinished(ILogger logger, FileResult? result)
    {
        if (result is null)
        {
            return;
        }

        var outcome = OutcomeName(result.Outcome);

        logger.FileFinished(result.Path, outcome, result.SourceType, result.Lines, result.Records, result.Skipped);

        if (result.Reason.Length > 0)
        {
            logger.FileNotImported(result.Path, result.Reason);
        }
    }

    #endregion // Methods
}