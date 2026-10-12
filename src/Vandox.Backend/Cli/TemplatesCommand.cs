using System.Globalization;

using Microsoft.Extensions.Logging;

using Vandox.Backend.Hosting;
using Vandox.Storage;

namespace Vandox.Backend.Cli;

/// <summary>
/// <c>vandoxd templates</c>: rebuilds the log template statistics from the stored log lines. The summary goes to standard
/// output, progress to standard error as JSON lines.
/// </summary>
internal static class TemplatesCommand
{
    #region Methods

    /// <summary>
    /// Runs the sub-command.
    /// </summary>
    /// <param name="configPath">The path of the configuration file</param>
    /// <param name="environment">The environment as name and value pairs</param>
    /// <param name="stdout">Standard output: the summary</param>
    /// <param name="stderr">Standard error: the log</param>
    /// <param name="hooks">The hooks of the host</param>
    /// <param name="cancellationToken">The cancellation token</param>
    /// <returns>The exit code: 0 when the statistics are rebuilt, 1 otherwise</returns>
    internal static Task<int> RunAsync(string configPath, IEnumerable<KeyValuePair<string, string>> environment, TextWriter stdout, TextWriter stderr, ServeHooks hooks, CancellationToken cancellationToken)
    {
        return StoreCommand.RunAsync(configPath, environment, stderr, hooks, logger => logger.TemplateRebuildInterrupted(), (store, _, logger, _) => RebuildAsync(store, logger, stdout, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Formats the summary of a rebuild.
    /// </summary>
    /// <param name="result">The result</param>
    /// <returns>The three summary lines, each ended by a line feed</returns>
    internal static string FormatSummary(TemplateRebuildResult result)
    {
        return string.Create(CultureInfo.InvariantCulture, $"Log lines:           {result.Lines}\nBatches:             {result.Batches}\nTemplates:           {result.Templates}\n");
    }

    /// <summary>
    /// Rebuilds the statistics and writes the summary.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="logger">The logger</param>
    /// <param name="stdout">Standard output</param>
    /// <param name="cancellationToken">The cancellation token</param>
    /// <returns>The exit code</returns>
    private static async Task<int> RebuildAsync(SqliteStore store, ILogger logger, TextWriter stdout, CancellationToken cancellationToken)
    {
        logger.TemplateRebuildStarted();

        try
        {
            var result = await store.RebuildTemplatesAsync(StorageLimits.TemplateRebuildBatchLines, (lines, _) => ReportProgress(logger, lines), cancellationToken).ConfigureAwait(false);

            await stdout.WriteAsync(FormatSummary(result)).ConfigureAwait(false);

            return 0;
        }
        catch (StoreException exception)
        {
            logger.TemplateRebuildFailed(exception, exception.Message);

            return 1;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.TemplateRebuildInterrupted();

            return 1;
        }
    }

    /// <summary>
    /// Logs the progress of the rebuild.
    /// </summary>
    /// <param name="logger">The logger</param>
    /// <param name="lines">The log lines counted so far</param>
    /// <returns>A completed task</returns>
    private static Task ReportProgress(ILogger logger, long lines)
    {
        logger.TemplateRebuildProgress(lines);

        return Task.CompletedTask;
    }

    #endregion // Methods
}