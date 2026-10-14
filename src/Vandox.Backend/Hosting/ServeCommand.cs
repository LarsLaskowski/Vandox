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
using Vandox.Backend.Incidents;
using Vandox.Backend.Logging;
using Vandox.Backend.Logs;
using Vandox.Backend.Security;
using Vandox.Core.Configuration;
using Vandox.Storage;

namespace Vandox.Backend.Hosting;

/// <summary>
/// Runs the backend service: opens the database and the two listeners, serves until it is told to stop and shuts down
/// gracefully.
/// </summary>
internal static class ServeCommand
{
    #region Constants

    private const int MaxHeaderBytes = 16 << 10;

    /// <summary>
    /// The name of the environment variable that holds the secret of the web login, for the log line that reports it missing; it names the variable, never a value.
    /// </summary>
    private const string HashSecretVariable = "VANDOX_WEB_PASSWORD_HASH";

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

        var verifier = CheckPasswordHash(config, logger);

        if (verifier is null)
        {
            return 1;
        }

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

            var code = await ServeAsync(config, loggerFactory, hooks, store, verifier, cancellationToken).ConfigureAwait(false);

            logger.Stopped();

            return code;
        }
    }

    /// <summary>
    /// Checks that the web password hash is set and in the accepted form.
    /// </summary>
    /// <param name="config">The configuration</param>
    /// <param name="logger">The logger</param>
    /// <returns>The hash; <c>null</c> when it is missing or refused, with the reason logged</returns>
    private static PasswordHash? CheckPasswordHash(BackendConfig config, ILogger logger)
    {
        var secret = config.Secrets.WebPasswordHash;

        if (secret.IsSet)
        {
            return ParsePasswordHash(secret.Reveal(), logger);
        }

        logger.WebPasswordHashMissing(HashSecretVariable);

        return null;
    }

    /// <summary>
    /// Parses the web password hash.
    /// </summary>
    /// <param name="text">The hash text</param>
    /// <param name="logger">The logger</param>
    /// <returns>The hash; <c>null</c> when it is refused, with the reason logged</returns>
    private static PasswordHash? ParsePasswordHash(string text, ILogger logger)
    {
        try
        {
            return PasswordHash.Parse(text);
        }
        catch (PasswordHashException exception)
        {
            logger.WebPasswordHashInvalid(exception.Message);

            return null;
        }
    }

    /// <summary>
    /// Builds the web application, runs it and reports its failure.
    /// </summary>
    /// <param name="config">The configuration</param>
    /// <param name="loggerFactory">The logger factory</param>
    /// <param name="hooks">Replaces the listeners in tests</param>
    /// <param name="store">The open database</param>
    /// <param name="verifier">The check of the web password</param>
    /// <param name="cancellationToken">Stops the service</param>
    /// <returns>A task that returns the exit code</returns>
    private static async Task<int> ServeAsync(BackendConfig config, ILoggerFactory loggerFactory, ServeHooks hooks, SqliteStore store, PasswordHash verifier, CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("vandoxd");

        try
        {
            await using var app = Build(config, loggerFactory, hooks, store, verifier);

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
    /// <param name="verifier">The check of the web password</param>
    /// <returns>The application, not yet started</returns>
    private static WebApplication Build(BackendConfig config, ILoggerFactory loggerFactory, ServeHooks hooks, SqliteStore store, PasswordHash verifier)
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
        builder.Services.AddSingleton<ILogTemplateReader>(store);
        builder.Services.AddSingleton(_ => new WebReaderGate(LogsView.MaxConcurrentQueries));
        builder.Services.AddSingleton<ILogExplorer>(provider => new GatedLogExplorer(store, provider.GetRequiredService<WebReaderGate>()));
        builder.Services.AddSingleton<IIncidentBrowser>(provider => new GatedIncidentBrowser(store, provider.GetRequiredService<WebReaderGate>()));
        builder.Services.AddSingleton(provider => new IncidentWindowReader(new GatedRecordReader(store, provider.GetRequiredService<WebReaderGate>())));
        builder.Services.AddSingleton(hooks.Clock ?? TimeProvider.System);
        builder.Services.AddSingleton<IImportTracker>(store);
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = _shutdownTimeout);
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        WebAuthentication.AddServices(builder.Services, verifier, hooks.Clock ?? TimeProvider.System, loggerFactory);
        builder.WebHost.UseStaticWebAssets();
        builder.WebHost.ConfigureKestrel(options => Listen(options, config, hooks));

        var app = builder.Build();

        hooks.Built?.Invoke(app.Services);

        var checker = new PingChecker(store.PingAsync, _pingTimeout);

        var forwarded = ForwardedHeadersSetup.CreateOptions(config.Web.GetTrustedProxies());

        app.UseMiddleware<PortRoutingMiddleware>();

        if (forwarded is not null)
        {
            app.UseForwardedHeaders(forwarded);
        }

        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();
        app.MapStaticAssets().AllowAnonymous();
        HealthEndpoint.Map(app, checker, loggerFactory.CreateLogger("health"));
        WebAuthentication.MapLogout(app);
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode(options => options.ContentSecurityFrameAncestorsPolicy = null);

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