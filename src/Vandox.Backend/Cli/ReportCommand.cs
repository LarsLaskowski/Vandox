using Microsoft.Extensions.Logging;

using Vandox.Backend.Hosting;
using Vandox.Backend.Reports;
using Vandox.Core.Configuration;
using Vandox.Core.Signatures;
using Vandox.Storage;

namespace Vandox.Backend.Cli;

/// <summary>
/// <c>vandoxd report</c>: writes the forensics report of the stored data as Markdown to standard output; the log goes to
/// standard error as JSON lines.
/// </summary>
internal static class ReportCommand
{
    #region Methods

    /// <summary>
    /// Runs the sub-command.
    /// </summary>
    /// <param name="configPath">The path of the configuration file</param>
    /// <param name="language">The language of the report: <c>en</c> or <c>de</c></param>
    /// <param name="environment">The environment as name and value pairs</param>
    /// <param name="stdout">Standard output: the report</param>
    /// <param name="stderr">Standard error: the log</param>
    /// <param name="hooks">The hooks of the host</param>
    /// <param name="cancellationToken">The cancellation token</param>
    /// <returns>The exit code: 0 when the report is written, 1 otherwise</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="language"/> is not <c>en</c> or <c>de</c></exception>
    internal static Task<int> RunAsync(string configPath, string language, IEnumerable<KeyValuePair<string, string>> environment, TextWriter stdout, TextWriter stderr, ServeHooks hooks, CancellationToken cancellationToken)
    {
        var text = ReportText.For(language) ?? throw new ArgumentOutOfRangeException(nameof(language), language, "the language of the report must be en or de");

        return StoreCommand.RunAsync(configPath, environment, stderr, hooks, logger => logger.ReportInterrupted(), (store, config, logger, clock) => ReportAsync(store, config, logger, stdout, text, clock, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Writes the report.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="config">The configuration</param>
    /// <param name="logger">The logger</param>
    /// <param name="stdout">Standard output</param>
    /// <param name="text">The labels of the language</param>
    /// <param name="clock">The clock</param>
    /// <param name="cancellationToken">The cancellation token</param>
    /// <returns>The exit code</returns>
    private static async Task<int> ReportAsync(SqliteStore store, BackendConfig config, ILogger logger, TextWriter stdout, ReportText text, TimeProvider clock, CancellationToken cancellationToken)
    {
        logger.ReportStarted();

        var reporter = new ForensicsReporter(store, store, BuiltInSignatures.Catalog, config.Incidents.GetWindowBefore(), clock);

        try
        {
            await reporter.RunAsync(new MarkdownReportWriter(stdout, text), cancellationToken).ConfigureAwait(false);
            await stdout.FlushAsync(cancellationToken).ConfigureAwait(false);

            return 0;
        }
        catch (StoreException exception)
        {
            logger.ReportFailed(exception, exception.Message);

            return 1;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.ReportInterrupted();

            return 1;
        }
    }

    #endregion // Methods
}