using Microsoft.Extensions.Logging;

using Vandox.Backend.Logging;
using Vandox.Core.Configuration;
using Vandox.Core.LogParsing;
using Vandox.Import;
using Vandox.Storage;

namespace Vandox.Backend.Cli;

/// <summary>
/// <c>vandoxd import</c>: imports the logs saved on the backend host at a path (a directory, a tar or tar.gz archive, a gzip
/// file or a log file) into the database. The summary goes to standard output, progress to standard error as JSON lines.
/// </summary>
internal static class ImportCommand
{
    #region Methods

    /// <summary>
    /// Loads the configuration, opens the database and imports a path.
    /// </summary>
    /// <param name="root">The path to import</param>
    /// <param name="configPath">The path of the configuration file</param>
    /// <param name="environment">The environment</param>
    /// <param name="stdout">Receives the summary</param>
    /// <param name="stderr">Receives the log lines</param>
    /// <param name="hooks">Provides the parsers and the clock</param>
    /// <param name="cancellationToken">Interrupts the import</param>
    /// <returns>A task that returns the exit code: 0 when everything was imported, 1 otherwise</returns>
    internal static async Task<int> RunAsync(string root, string configPath, IEnumerable<KeyValuePair<string, string>> environment, TextWriter stdout, TextWriter stderr, ServeHooks hooks, CancellationToken cancellationToken)
    {
        var clock = hooks.Clock ?? TimeProvider.System;
        using var bootstrap = BackendLogging.Create(stderr, "info", clock)!;
        var bootstrapLogger = bootstrap.CreateLogger("vandoxd");
        BackendConfig config;

        try
        {
            config = BackendConfigLoader.Load(configPath, environment);
        }
        catch (ConfigException exception)
        {
            bootstrapLogger.ConfigurationInvalid(exception, exception.Message);

            return 1;
        }

        using var loggerFactory = BackendLogging.Create(stderr, config.Log.Level, clock)!;
        var logger = loggerFactory.CreateLogger("vandoxd");
        ParserRegistry registry;

        try
        {
            registry = new ParserRegistry(hooks.Parsers ?? BuiltInParsers.Create(config.Import.TimeZone));
        }
        catch (ArgumentException exception)
        {
            logger.ParsersInvalid(exception, exception.Message);

            return 1;
        }

        try
        {
            await using var store = await SqliteStore.OpenAsync(config.Storage.Directory, cancellationToken).ConfigureAwait(false);

            return await ImportIntoAsync(store, registry, root, logger, stdout, clock, cancellationToken).ConfigureAwait(false);
        }
        catch (StoreException exception)
        {
            logger.OpeningDatabaseFailed(exception, exception.Message);

            return 1;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.ImportInterrupted();

            return 1;
        }
    }

    /// <summary>
    /// Imports a path into the open database, prints the summary and returns the exit code.
    /// </summary>
    /// <param name="store">The database</param>
    /// <param name="registry">The parsers</param>
    /// <param name="root">The path to import</param>
    /// <param name="logger">The logger</param>
    /// <param name="stdout">Receives the summary</param>
    /// <param name="clock">The clock</param>
    /// <param name="cancellationToken">Interrupts the import</param>
    /// <returns>A task that returns the exit code</returns>
    private static async Task<int> ImportIntoAsync(SqliteStore store, ParserRegistry registry, string root, ILogger logger, TextWriter stdout, TimeProvider clock, CancellationToken cancellationToken)
    {
        logger.ImportStarted(root);

        var run = await Importer.RunAsync(root,
                                          new ImportOptions
                                          {
                                              Parsers = registry,
                                              Store = new ImportStoreAdapter(store),
                                              Clock = clock,
                                              Progress = progress => ImportProgressLogger.Log(logger, progress)
                                          },
                                          cancellationToken)
                                .ConfigureAwait(false);
        var code = 0;

        if (run.Summary.Interrupted)
        {
            logger.ImportInterrupted();
            code = 1;
        }
        else if (run.Error is not null)
        {
            logger.ImportFailed(run.Error, run.Error.Message);
            code = 1;
        }
        else if (run.Summary.Count(ImportOutcome.Failed) > 0)
        {
            code = 1;
        }

        if (run.Error is null || run.Summary.Files.Count > 0 || run.Summary.Interrupted)
        {
            await ImportSummaryWriter.WriteAsync(stdout, run.Summary).ConfigureAwait(false);
        }

        return code;
    }

    #endregion // Methods
}