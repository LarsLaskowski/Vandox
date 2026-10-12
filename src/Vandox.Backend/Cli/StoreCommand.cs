using Microsoft.Extensions.Logging;

using Vandox.Backend.Hosting;
using Vandox.Backend.Logging;
using Vandox.Core.Configuration;
using Vandox.Storage;

namespace Vandox.Backend.Cli;

/// <summary>
/// The shared start-up of the sub-commands that work on the store: <c>incidents</c> and <c>templates</c>.
/// </summary>
internal static class StoreCommand
{
    #region Methods

    /// <summary>
    /// Loads the configuration, creates the logger and opens the store, then runs the command on it; logs and returns 1 when the
    /// configuration is invalid, the store cannot be opened or the open is cancelled.
    /// </summary>
    /// <param name="configPath">The path of the configuration file</param>
    /// <param name="environment">The environment as name and value pairs</param>
    /// <param name="stderr">Standard error: the log</param>
    /// <param name="hooks">The hooks of the host</param>
    /// <param name="logInterrupted">Logs the interruption of the open</param>
    /// <param name="run">The command, run on the open store</param>
    /// <param name="cancellationToken">The cancellation token</param>
    /// <returns>The exit code</returns>
    internal static async Task<int> RunAsync(string configPath, IEnumerable<KeyValuePair<string, string>> environment, TextWriter stderr, ServeHooks hooks, Action<ILogger> logInterrupted, Func<SqliteStore, BackendConfig, ILogger, TimeProvider, Task<int>> run, CancellationToken cancellationToken)
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
            logInterrupted(logger);

            return 1;
        }

        await using (store.ConfigureAwait(false))
        {
            return await run(store, config, logger, clock).ConfigureAwait(false);
        }
    }

    #endregion // Methods
}