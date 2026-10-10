using Microsoft.Extensions.Logging;

using Vandox.Backend.Cli;
using Vandox.Core.Configuration;

namespace Vandox.Backend.Hosting;

/// <summary>
/// The logic of vandoxd behind the entry point: it takes the process boundaries as arguments, so every path is tested without
/// starting a process.
/// </summary>
public static class BackendApp
{
    #region Methods

    /// <summary>
    /// Runs vandoxd with arguments (without the program name).
    /// </summary>
    /// <param name="args">The command-line arguments</param>
    /// <param name="environment">The environment as name and value pairs</param>
    /// <param name="stdout">The standard output</param>
    /// <param name="stderr">The standard error</param>
    /// <param name="hooks">Replaces the listeners and the parsers, and tells when the service serves</param>
    /// <param name="cancellationToken">Stops the service or interrupts the import</param>
    /// <returns>A task that returns the exit code: 0 on success, 1 on a runtime or start-up failure, 2 on a usage error</returns>
    public static async Task<int> RunAsync(IReadOnlyList<string> args, IEnumerable<KeyValuePair<string, string>> environment, TextWriter stdout, TextWriter stderr, ServeHooks hooks, CancellationToken cancellationToken)
    {
        var options = CommandLine.Parse(args);

        if (options.Help)
        {
            await stderr.WriteAsync(CommandLine.Usage(options.UsageFor == "import")).ConfigureAwait(false);

            return 0;
        }

        if (options.Error.Length > 0)
        {
            await stderr.WriteLineAsync(options.Error).ConfigureAwait(false);
            await stderr.WriteAsync(CommandLine.Usage(options.UsageFor == "import")).ConfigureAwait(false);

            return 2;
        }

        if (options.Version)
        {
            await stdout.WriteLineAsync(VersionInfo.Describe(CommandLine.BinaryName, typeof(Program).Assembly)).ConfigureAwait(false);

            return 0;
        }

        if (options.ImportPath is not null)
        {
            return await ImportCommand.RunAsync(options.ImportPath, options.ConfigPath, environment, stdout, stderr, hooks, cancellationToken).ConfigureAwait(false);
        }

        if (options.Healthcheck)
        {
            return await HealthCheckCommand.RunAsync(options.ConfigPath, stderr, null, cancellationToken).ConfigureAwait(false);
        }

        return await ServeAsync(options.ConfigPath, environment, stderr, hooks, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads the configuration and runs the service.
    /// </summary>
    /// <param name="configPath">The path of the configuration file</param>
    /// <param name="environment">The environment</param>
    /// <param name="stderr">The standard error</param>
    /// <param name="hooks">Replaces the listeners</param>
    /// <param name="cancellationToken">Stops the service</param>
    /// <returns>A task that returns the exit code</returns>
    private static async Task<int> ServeAsync(string configPath, IEnumerable<KeyValuePair<string, string>> environment, TextWriter stderr, ServeHooks hooks, CancellationToken cancellationToken)
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

        return await ServeCommand.RunAsync(config, configPath, loggerFactory, hooks, cancellationToken).ConfigureAwait(false);
    }

    #endregion // Methods
}