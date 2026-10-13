using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Microsoft.JSInterop;

using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// The services, test doubles and renderer that the tests of the log pages share. The renderer is interactive like a circuit; a page is the first and only root
/// component, so its id is <see cref="RootId"/>.
/// </summary>
internal sealed class LogPageHarness : IAsyncDisposable
{
    #region Constants

    /// <summary>
    /// The id of the first root component of a renderer.
    /// </summary>
    internal const int RootId = 0;

    #endregion // Constants

    #region Fields

    private readonly ServiceProvider _services;

    private bool _disposed;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="LogPageHarness"/> class.
    /// </summary>
    /// <param name="uri">The address the page starts at</param>
    internal LogPageHarness(string uri)
    {
        Explorer = new FakeLogExplorer();
        Navigation = new RecordingNavigationManager(uri);
        Clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero));
        Logger = new ListLogger();
        Runtime = new RecordingJsRuntime();

        _services = new ServiceCollection().AddLogging(builder => builder.AddProvider(new ListLoggerProvider(Logger)))
                                           .AddSingleton<ILogExplorer>(Explorer)
                                           .AddSingleton<NavigationManager>(Navigation)
                                           .AddSingleton<TimeProvider>(Clock)
                                           .AddSingleton<IJSRuntime>(Runtime)
                                           .BuildServiceProvider();

        Renderer = new ComponentTestRenderer(_services, _services.GetRequiredService<ILoggerFactory>());
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the explorer the pages get.
    /// </summary>
    internal FakeLogExplorer Explorer { get; }

    /// <summary>
    /// Gets the navigation manager.
    /// </summary>
    internal RecordingNavigationManager Navigation { get; }

    /// <summary>
    /// Gets the clock of the pages.
    /// </summary>
    internal FakeTimeProvider Clock { get; }

    /// <summary>
    /// Gets the logger every page logs to.
    /// </summary>
    internal ListLogger Logger { get; }

    /// <summary>
    /// Gets the script runtime of the charts.
    /// </summary>
    internal RecordingJsRuntime Runtime { get; }

    /// <summary>
    /// Gets the interactive renderer.
    /// </summary>
    internal ComponentTestRenderer Renderer { get; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Renders a page as the root component without parameters. The task completes when the page has finished its lifecycle, including a search.
    /// </summary>
    /// <typeparam name="TPage">The type of the page</typeparam>
    /// <returns>A task that returns the id of the root component</returns>
    internal Task<int> AddAsync<TPage>()
        where TPage : IComponent
    {
        return Renderer.AddAsync<TPage>(new Dictionary<string, object?>());
    }

    /// <summary>
    /// Sets the parameters of the page again, as the router does after a navigation to the same page.
    /// </summary>
    /// <returns>A task that completes when the page has finished its lifecycle</returns>
    internal Task UpdateAsync()
    {
        return Renderer.UpdateAsync(RootId, new Dictionary<string, object?>());
    }

    /// <summary>
    /// Renders a page statically, as the prerender does.
    /// </summary>
    /// <typeparam name="TPage">The type of the page</typeparam>
    /// <returns>A task that returns the markup</returns>
    internal async Task<string> RenderStaticAsync<TPage>()
        where TPage : IComponent
    {
        await using var renderer = new HtmlRenderer(_services, _services.GetRequiredService<ILoggerFactory>());

        return await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<TPage>()).ToHtmlString());
    }

    /// <summary>
    /// Gets the markup of the page as it is now.
    /// </summary>
    /// <returns>The markup</returns>
    internal string Html()
    {
        return Renderer.ToHtml(RootId);
    }

    /// <summary>
    /// Finds the buttons with a text.
    /// </summary>
    /// <param name="text">The text of the button, without surrounding white space</param>
    /// <returns>The buttons in document order</returns>
    internal IReadOnlyList<RenderedElement> Buttons(string text)
    {
        return Renderer.Elements(RootId).Where(element => element.Tag == "button" && element.Text.Trim() == text).ToArray();
    }

    /// <summary>
    /// Finds the input or select with a name.
    /// </summary>
    /// <param name="name">The value of the name attribute</param>
    /// <returns>The element</returns>
    internal RenderedElement Field(string name)
    {
        return Renderer.Elements(RootId).FirstOrDefault(element => element.Tag is "input" or "select" or "textarea" && element.Attributes.GetValueOrDefault("name") == name)
                   ?? throw new InvalidOperationException($"The page has no field named {name}.");
    }

    /// <summary>
    /// Finds the form.
    /// </summary>
    /// <returns>The first form element</returns>
    internal RenderedElement Form()
    {
        return Renderer.Elements(RootId).FirstOrDefault(element => element.Tag == "form") ?? throw new InvalidOperationException("The page has no form.");
    }

    /// <summary>
    /// Finds the links with a text.
    /// </summary>
    /// <param name="text">The text of the link, without surrounding white space</param>
    /// <returns>The links in document order</returns>
    internal IReadOnlyList<RenderedElement> Links(string text)
    {
        return Renderer.Elements(RootId).Where(element => element.Tag == "a" && element.Text.Trim() == text).ToArray();
    }

    /// <summary>
    /// Enters values in the fields of the form.
    /// </summary>
    /// <param name="values">The values by field name</param>
    /// <returns>A task that completes when the handlers are done</returns>
    internal async Task EnterAsync(params (string Name, string Value)[] values)
    {
        foreach (var (name, value) in values)
        {
            await Renderer.ChangeAsync(Field(name), value);
        }
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