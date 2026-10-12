using Microsoft.Extensions.Logging;

using Vandox.Backend.Hosting;
using Vandox.Backend.Incidents;
using Vandox.Backend.Logging;
using Vandox.Core.Configuration;
using Vandox.Core.Signatures;
using Vandox.Storage;

namespace Vandox.Backend.Cli;

/// <summary>
/// <c>vandoxd incidents</c>: rebuilds the incidents from the stored data. The incidents and the summary go to standard
/// output, progress to standard error as JSON lines.
/// </summary>
internal static class IncidentsCommand
{
    #region Methods

    /// <summary>
    /// Runs the sub-command.
    /// </summary>
    /// <param name="configPath">The path of the configuration file</param>
    /// <param name="environment">The environment as name and value pairs</param>
    /// <param name="stdout">Standard output: the incidents and the summary</param>
    /// <param name="stderr">Standard error: the log</param>
    /// <param name="hooks">The hooks of the host</param>
    /// <param name="cancellationToken">The cancellation token</param>
    /// <returns>The exit code: 0 when the incidents are rebuilt, 1 otherwise</returns>
    internal static async Task<int> RunAsync(string configPath, IEnumerable<KeyValuePair<string, string>> environment, TextWriter stdout, TextWriter stderr, ServeHooks hooks, CancellationToken cancellationToken)
    {
        var clock = hooks.Clock ?? TimeProvider.System;
        using var bootstrap = BackendLogging.Create(stderr, "info", clock)!;
        BackendConfig config;

        try
        {
            config = BackendConfigLoader.Load(configPath, environment);
        }
        catch (ConfigException exception)
        {
            bootstrap.CreateLogger("vandoxd").ConfigurationInvalid(exception, exception.Message);

            return 1;
        }

        using var loggerFactory = BackendLogging.Create(stderr, config.Log.Level, clock)!;
        var logger = loggerFactory.CreateLogger("vandoxd");
        SqliteStore store;

        try
        {
            store = await SqliteStore.OpenAsync(config.Storage.Directory, cancellationToken).ConfigureAwait(false);
        }
        catch (StoreException exception)
        {
            logger.OpeningDatabaseFailed(exception, exception.Message);

            return 1;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.IncidentsInterrupted();

            return 1;
        }

        await using (store.ConfigureAwait(false))
        {
            return await ReconstructAsync(store, config, logger, stdout, clock, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Rebuilds the incidents and writes them and the summary.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="config">The configuration</param>
    /// <param name="logger">The logger</param>
    /// <param name="stdout">Standard output</param>
    /// <param name="clock">The clock</param>
    /// <param name="cancellationToken">The cancellation token</param>
    /// <returns>The exit code</returns>
    private static async Task<int> ReconstructAsync(SqliteStore store, BackendConfig config, ILogger logger, TextWriter stdout, TimeProvider clock, CancellationToken cancellationToken)
    {
        logger.IncidentsStarted();

        var reconstructor = new IncidentReconstructor(store, store, store, BuiltInSignatures.Catalog, config.Incidents.GetWindowBefore(), config.Incidents.GetWindowAfter(), clock, logger);

        try
        {
            var result = await reconstructor.RunAsync(saved => stdout.WriteAsync(IncidentSummaryWriter.FormatIncident(saved) + "\n"), cancellationToken).ConfigureAwait(false);

            await IncidentSummaryWriter.WriteAsync(stdout, result).ConfigureAwait(false);

            return 0;
        }
        catch (StoreException exception)
        {
            logger.IncidentsFailed(exception, exception.Message);

            return 1;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.IncidentsInterrupted();

            return 1;
        }
    }

    #endregion // Methods
}