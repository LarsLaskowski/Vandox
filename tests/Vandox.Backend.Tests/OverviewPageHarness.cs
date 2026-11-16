using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Vandox.Backend.Components.Pages;
using Vandox.Backend.Overview;
using Vandox.Backend.Security;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Renders the overview page with a fake overview reader, a fake incident browser, a given clock, the circuit connection and the session activity of one circuit.
/// </summary>
internal sealed class OverviewPageHarness : IAsyncDisposable
{
    #region Constants

    /// <summary>
    /// The id of the page as the root component.
    /// </summary>
    internal const int RootId = 0;

    #endregion // Constants

    #region Fields

    /// <summary>
    /// The services the page is created from.
    /// </summary>
    private readonly ServiceProvider _services;

    /// <summary>
    /// Whether the renderer is disposed.
    /// </summary>
    private bool _rendererDisposed;

    /// <summary>
    /// Whether the harness is disposed.
    /// </summary>
    private bool _disposed;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="OverviewPageHarness"/> class.
    /// </summary>
    /// <param name="clock">The clock of the page</param>
    /// <param name="dispatcher">The dispatcher of the renderer, or <c>null</c> for the default one</param>
    internal OverviewPageHarness(TimeProvider clock, Dispatcher? dispatcher = null)
    {
        Clock = clock;
        Reader = new FakeOverviewReader();
        Browser = new FakeIncidentBrowser();
        Connection = new CircuitConnection();
        Activity = new SessionActivity();
        Logger = new ListLogger();

        _services = new ServiceCollection().AddLogging(builder => builder.AddProvider(new ListLoggerProvider(Logger)))
                                           .AddSingleton<IOverviewReader>(Reader)
                                           .AddSingleton<IIncidentBrowser>(Browser)
                                           .AddSingleton(Clock)
                                           .AddSingleton(Connection)
                                           .AddSingleton(Activity)
                                           .BuildServiceProvider();

        Renderer = new ComponentTestRenderer(_services, _services.GetRequiredService<ILoggerFactory>(), dispatcher: dispatcher);
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the clock of the page.
    /// </summary>
    internal TimeProvider Clock { get; }

    /// <summary>
    /// Gets the overview reader.
    /// </summary>
    internal FakeOverviewReader Reader { get; }

    /// <summary>
    /// Gets the incident browser.
    /// </summary>
    internal FakeIncidentBrowser Browser { get; }

    /// <summary>
    /// Gets the connection of the circuit.
    /// </summary>
    internal CircuitConnection Connection { get; }

    /// <summary>
    /// Gets the session activity of the circuit.
    /// </summary>
    internal SessionActivity Activity { get; }

    /// <summary>
    /// Gets the logger.
    /// </summary>
    internal ListLogger Logger { get; }

    /// <summary>
    /// Gets the interactive renderer.
    /// </summary>
    internal ComponentTestRenderer Renderer { get; }

    /// <summary>
    /// Gets the added page.
    /// </summary>
    internal OverviewPage Page => (OverviewPage)Renderer.Component(RootId);

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Renders the page interactively, as in a circuit.
    /// </summary>
    /// <returns>A task that returns the id of the page</returns>
    internal Task<int> AddAsync()
    {
        return Renderer.AddAsync<OverviewPage>(new Dictionary<string, object?>());
    }

    /// <summary>
    /// Renders the page statically, as the prerendered first response.
    /// </summary>
    /// <returns>A task that returns the markup</returns>
    internal async Task<string> RenderStaticAsync()
    {
        await using var renderer = new HtmlRenderer(_services, _services.GetRequiredService<ILoggerFactory>());

        return await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<OverviewPage>()).ToHtmlString());
    }

    /// <summary>
    /// Gets the markup of the interactively rendered page.
    /// </summary>
    /// <returns>The markup</returns>
    internal string Html()
    {
        return Renderer.ToHtml(RootId);
    }

    /// <summary>
    /// Waits until the markup meets a condition, at most 10 seconds after the last render.
    /// </summary>
    /// <param name="condition">The condition</param>
    /// <returns>A task that completes when the condition holds</returns>
    internal Task WaitForHtmlAsync(Func<string, bool> condition)
    {
        return Renderer.WaitUntilAsync(() => condition(Html()));
    }

    /// <summary>
    /// Disposes the renderer and with it the page, as leaving the page does; an exception of the disposal is returned, not thrown.
    /// </summary>
    /// <returns>A task that returns the exception of the disposal, or <c>null</c></returns>
    internal async Task<Exception?> DisposePageAsync()
    {
        if (_rendererDisposed)
        {
            return null;
        }

        _rendererDisposed = true;

        try
        {
            await Renderer.DisposeAllAsync();

            return null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotImplementedException or AggregateException)
        {
            return exception;
        }
    }

    #endregion // Methods

    #region IAsyncDisposable

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await DisposePageAsync();
        await _services.DisposeAsync();
        Reader.Dispose();
    }

    #endregion // IAsyncDisposable
}