using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Microsoft.JSInterop;

using Vandox.Backend.Incidents;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Wires the incident pages with a <see cref="FakeIncidentBrowser"/>, an <see cref="IncidentWindowReader"/> over a <see cref="ScriptedRecordReader"/>, a fake
/// clock, a logger that keeps its messages, a recording script runtime and a fixed antiforgery token, and renders them like <see cref="LogPageHarness"/>.
/// </summary>
internal sealed class IncidentPageHarness : IAsyncDisposable
{
    #region Constants

    /// <summary>
    /// The component ID of the root component that <see cref="AddAsync{TPage}"/> renders.
    /// </summary>
    internal const int RootId = 0;

    #endregion // Constants

    #region Fields

    private readonly ServiceProvider _services;

    private bool _disposed;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="IncidentPageHarness"/> class.
    /// </summary>
    /// <param name="uri">The address the page is shown at</param>
    internal IncidentPageHarness(string uri)
    {
        Browser = new FakeIncidentBrowser();
        Records = new ScriptedRecordReader();
        Window = new IncidentWindowReader(Records);
        Navigation = new RecordingNavigationManager(uri);
        Clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero));
        Logger = new ListLogger();
        Runtime = new RecordingJsRuntime();

        _services = new ServiceCollection().AddLogging(builder => builder.AddProvider(new ListLoggerProvider(Logger)))
                                           .AddSingleton<IIncidentBrowser>(Browser)
                                           .AddSingleton<IRecordReader>(Records)
                                           .AddSingleton(Window)
                                           .AddSingleton<NavigationManager>(Navigation)
                                           .AddSingleton<TimeProvider>(Clock)
                                           .AddSingleton<IJSRuntime>(Runtime)
                                           .AddSingleton<AntiforgeryStateProvider, FixedAntiforgeryStateProvider>()
                                           .BuildServiceProvider();

        Renderer = new ComponentTestRenderer(_services, _services.GetRequiredService<ILoggerFactory>());
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the browser the pages read the incidents and notes from.
    /// </summary>
    internal FakeIncidentBrowser Browser { get; }

    /// <summary>
    /// Gets the records the window reader reads the charts from.
    /// </summary>
    internal ScriptedRecordReader Records { get; }

    /// <summary>
    /// Gets the window reader the incident page uses.
    /// </summary>
    internal IncidentWindowReader Window { get; }

    /// <summary>
    /// Gets the navigation manager.
    /// </summary>
    internal RecordingNavigationManager Navigation { get; }

    /// <summary>
    /// Gets the clock of the pages.
    /// </summary>
    internal FakeTimeProvider Clock { get; }

    /// <summary>
    /// Gets the logger that every page logger writes to.
    /// </summary>
    internal ListLogger Logger { get; }

    /// <summary>
    /// Gets the script runtime that records the calls of the charts.
    /// </summary>
    internal RecordingJsRuntime Runtime { get; }

    /// <summary>
    /// Gets the renderer.
    /// </summary>
    internal ComponentTestRenderer Renderer { get; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Renders a page as the interactive renderer does, with the incident number as its route parameter.
    /// </summary>
    /// <typeparam name="TPage">The type of the page</typeparam>
    /// <param name="id">The incident number, or 0 for a page without a route parameter</param>
    /// <returns>A task that returns the root component ID when the first render and its lifecycle are done</returns>
    internal Task<int> AddAsync<TPage>(long id = 0)
        where TPage : IComponent
    {
        return Renderer.AddAsync<TPage>(Parameters(id));
    }

    /// <summary>
    /// Renders a page as the prerender does: static, without a live connection.
    /// </summary>
    /// <typeparam name="TPage">The type of the page</typeparam>
    /// <param name="id">The incident number, or 0 for a page without a route parameter</param>
    /// <returns>A task that returns the markup</returns>
    internal async Task<string> RenderStaticAsync<TPage>(long id = 0)
        where TPage : IComponent
    {
        await using var renderer = new HtmlRenderer(_services, _services.GetRequiredService<ILoggerFactory>());

        var parameters = ParameterView.FromDictionary(Parameters(id));

        return await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<TPage>(parameters)).ToHtmlString());
    }

    /// <summary>
    /// Returns the markup of the rendered page.
    /// </summary>
    /// <returns>The markup</returns>
    internal string Html()
    {
        return Renderer.ToHtml(RootId);
    }

    /// <summary>
    /// Returns every element of the rendered page.
    /// </summary>
    /// <returns>The elements, depth first</returns>
    internal IReadOnlyList<RenderedElement> Elements()
    {
        return Renderer.Elements(RootId);
    }

    /// <summary>
    /// Returns the buttons with a text.
    /// </summary>
    /// <param name="text">The text of the button</param>
    /// <returns>The buttons</returns>
    internal IReadOnlyList<RenderedElement> Buttons(string text)
    {
        return Elements().Where(element => element.Tag == "button" && element.Text.Trim() == text).ToArray();
    }

    /// <summary>
    /// Returns the links to an address.
    /// </summary>
    /// <param name="href">The value of the <c>href</c> attribute</param>
    /// <returns>The links</returns>
    internal IReadOnlyList<RenderedElement> Links(string href)
    {
        return Elements().Where(element => element.Tag == "a" && element.Attributes.GetValueOrDefault("href") == href).ToArray();
    }

    /// <summary>
    /// Waits until the markup of the page fulfils a condition; a condition that never holds ends the wait after 10 seconds.
    /// </summary>
    /// <param name="condition">The condition on the markup</param>
    /// <returns>A task that completes when the condition holds</returns>
    internal Task WaitForHtmlAsync(Func<string, bool> condition)
    {
        return Renderer.WaitUntilAsync(() => condition(Html()));
    }

    /// <summary>
    /// Creates the parameters of a page.
    /// </summary>
    /// <param name="id">The incident number, or 0 for none</param>
    /// <returns>The parameters</returns>
    private static Dictionary<string, object?> Parameters(long id)
    {
        return id == 0
                   ? []
                   : new Dictionary<string, object?>
                     {
                         ["Id"] = id
                     };
    }

    #endregion // Methods

    #region IAsyncDisposable

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await Renderer.DisposeAllAsync();
        await _services.DisposeAsync();
    }

    #endregion // IAsyncDisposable
}