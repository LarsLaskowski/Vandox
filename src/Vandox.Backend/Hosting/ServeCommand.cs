using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Vandox.Backend.Cli;
using Vandox.Backend.Components;
using Vandox.Backend.Logging;
using Vandox.Core.Configuration;
using Vandox.Storage;

namespace Vandox.Backend;

/// <summary>
/// Runs the backend service: opens the database and the two listeners, serves until it is told to stop and shuts down
/// gracefully.
/// </summary>
internal static class ServeCommand
{
    #region Constants

    private const int MaxHeaderBytes = 16 << 10;

    #endregion // Constants

    #region Fields

    private static readonly TimeSpan _pingTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan _shutdownTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan _headerTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan _keepAlive = TimeSpan.FromSeconds(120);

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Runs the service until <paramref name="cancellationToken"/> is cancelled or a listener fails.
    /// </summary>
    /// <param name="config">The configuration</param>
    /// <param name="configPath">The path of the configuration file, for the start-up log line</param>
    /// <param name="loggerFactory">The logger factory</param>
    /// <param name="hooks">Replaces the listeners in tests</param>
    /// <param name="cancellationToken">Stops the service</param>
    /// <returns>A task that returns the exit code</returns>
    internal static async Task<int> RunAsync(BackendConfig config, string configPath, ILoggerFactory loggerFactory, ServeHooks hooks, CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("vandoxd");

        var version = VersionInfo.Describe(CommandLine.BinaryName, typeof(Program).Assembly);

        logger.Starting(version, configPath, config.Web.Listen, config.Ingest.Listen, config.Storage.Directory);

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
            logger.ShuttingDown();

            return 0;
        }

        await using (store.ConfigureAwait(false))
        {
            var databasePath = Path.Combine(config.Storage.Directory, StorageLimits.FileName);

            logger.DatabaseOpened(databasePath, store.Created);

            var code = await ServeAsync(config, loggerFactory, hooks, store, cancellationToken).ConfigureAwait(false);

            logger.Stopped();

            return code;
        }
    }

    /// <summary>
    /// Builds the web application, runs it and reports its failure.
    /// </summary>
    /// <param name="config">The configuration</param>
    /// <param name="loggerFactory">The logger factory</param>
    /// <param name="hooks">Replaces the listeners in tests</param>
    /// <param name="store">The open database</param>
    /// <param name="cancellationToken">Stops the service</param>
    /// <returns>A task that returns the exit code</returns>
    private static async Task<int> ServeAsync(BackendConfig config, ILoggerFactory loggerFactory, ServeHooks hooks, SqliteStore store, CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("vandoxd");

        try
        {
            await using var app = Build(config, loggerFactory, hooks, store);

            await app.StartAsync(cancellationToken).ConfigureAwait(false);
            Announce(app, config, hooks, logger);
            await app.WaitForShutdownAsync(cancellationToken).ConfigureAwait(false);
            logger.ShuttingDown();
            await app.StopAsync(CancellationToken.None).ConfigureAwait(false);

            return 0;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or OperationCanceledException)
        {
            if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                logger.ShuttingDown();

                return 0;
            }

            logger.ServingFailed(exception, exception.Message);

            return 1;
        }
    }

    /// <summary>
    /// Builds the web application with its two listeners.
    /// </summary>
    /// <param name="config">The configuration</param>
    /// <param name="loggerFactory">The logger factory</param>
    /// <param name="hooks">Replaces the listeners in tests</param>
    /// <param name="store">The open database</param>
    /// <returns>The application, not yet started</returns>
    private static WebApplication Build(BackendConfig config, ILoggerFactory loggerFactory, ServeHooks hooks, SqliteStore store)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
                                                       {
                                                           ApplicationName = typeof(Program).Assembly.GetName().Name,
                                                           ContentRootPath = AppContext.BaseDirectory
                                                       });

        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(loggerFactory);
        builder.Services.AddSingleton<IRecordWriter>(store);
        builder.Services.AddSingleton<IRecordReader>(store);
        builder.Services.AddSingleton<ILogSearcher>(store);
        builder.Services.AddSingleton<IImportTracker>(store);
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = _shutdownTimeout);
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        builder.WebHost.ConfigureKestrel(options => Listen(options, config, hooks));

        var app = builder.Build();
        var checker = new PingChecker(store.PingAsync, _pingTimeout);

        app.UseMiddleware<PortRoutingMiddleware>();
        app.UseStaticFiles();
        app.UseAntiforgery();
        HealthEndpoint.Map(app, checker, loggerFactory.CreateLogger("health"));
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

        return app;
    }

    /// <summary>
    /// Opens the web and ingest listeners with the limits that keep a slow client from holding a connection forever.
    /// </summary>
    /// <param name="options">The Kestrel options</param>
    /// <param name="config">The configuration</param>
    /// <param name="hooks">Replaces the listeners in tests</param>
    private static void Listen(KestrelServerOptions options, BackendConfig config, ServeHooks hooks)
    {
        options.AddServerHeader = false;
        options.Limits.RequestHeadersTimeout = _headerTimeout;
        options.Limits.KeepAliveTimeout = _keepAlive;
        options.Limits.MaxRequestHeadersTotalSize = MaxHeaderBytes;

        if (hooks.Listen is not null)
        {
            hooks.Listen(options);

            return;
        }

        Open(options, ListenAddress.Parse(config.Web.Listen), ListenerRoutes.Web);
        Open(options, ListenAddress.Parse(config.Ingest.Listen), ListenerRoutes.Ingest);
    }

    /// <summary>
    /// Opens one listener.
    /// </summary>
    /// <param name="options">The Kestrel options</param>
    /// <param name="address">The address</param>
    /// <param name="label">Labels the connections of the listener</param>
    private static void Open(KestrelServerOptions options, ListenAddress address, Action<ListenOptions> label)
    {
        if (address.Address is null)
        {
            options.ListenAnyIP(address.Port, label);
        }
        else
        {
            options.Listen(address.Address, address.Port, label);
        }
    }

    /// <summary>
    /// Logs the addresses the service listens on and tells the hook.
    /// </summary>
    /// <param name="app">The started application</param>
    /// <param name="config">The configuration</param>
    /// <param name="hooks">Replaces the listeners in tests</param>
    /// <param name="logger">The logger</param>
    private static void Announce(WebApplication app, BackendConfig config, ServeHooks hooks, ILogger logger)
    {
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.ToList() ?? [];

        logger.Listening("web", config.Web.Listen);
        logger.Listening("ingest", config.Ingest.Listen);
        hooks.Started?.Invoke(addresses);
    }

    #endregion // Methods
}