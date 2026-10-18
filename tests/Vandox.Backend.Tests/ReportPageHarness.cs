using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

using Vandox.Backend.Reports;
using Vandox.Core.Model;
using Vandox.Core.Signatures;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Wires the report page with a <see cref="ReportService"/> over a <see cref="ScriptedRecordReader"/> and a <see cref="ScriptedIncidentStore"/> (or over a
/// reader and a store the test gives), a fake clock, a logger that keeps its messages and a navigation manager that follows the address, and renders the page
/// like <see cref="IncidentPageHarness"/>.
/// </summary>
internal sealed class ReportPageHarness : IAsyncDisposable
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
    /// Initializes a new instance of the <see cref="ReportPageHarness"/> class over an empty scripted reader and an empty scripted incident store.
    /// </summary>
    /// <param name="uri">The address the page is shown at</param>
    internal ReportPageHarness(string uri)
        : this(uri, new ScriptedRecordReader(), new ScriptedIncidentStore([], false))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ReportPageHarness"/> class over a reader and a store.
    /// </summary>
    /// <param name="uri">The address the page is shown at</param>
    /// <param name="records">The reader of the records; the scripted one of <see cref="Records"/> when it is one</param>
    /// <param name="incidents">The incident store</param>
    internal ReportPageHarness(string uri, IRecordReader records, IIncidentStore incidents)
    {
        Records = records as ScriptedRecordReader ?? new ScriptedRecordReader();
        Navigation = new RecordingNavigationManager(uri);
        Clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero));
        Logger = new ListLogger();
        Service = new ReportService(records, incidents, new ScriptedListenerHistoryReader(), BuiltInSignatures.Catalog, TimeSpan.FromHours(1), Clock);

        _services = new ServiceCollection().AddLogging(builder => builder.AddProvider(new ListLoggerProvider(Logger)))
                                           .AddSingleton(Service)
                                           .AddSingleton<NavigationManager>(Navigation)
                                           .AddSingleton<TimeProvider>(Clock)
                                           .BuildServiceProvider();

        Renderer = new ComponentTestRenderer(_services, _services.GetRequiredService<ILoggerFactory>());
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the scripted reader, when the harness was created over it; an unused one otherwise.
    /// </summary>
    internal ScriptedRecordReader Records { get; }

    /// <summary>
    /// Gets the report service the page runs.
    /// </summary>
    internal ReportService Service { get; }

    /// <summary>
    /// Gets the navigation manager.
    /// </summary>
    internal RecordingNavigationManager Navigation { get; }

    /// <summary>
    /// Gets the clock of the page and of the report.
    /// </summary>
    internal FakeTimeProvider Clock { get; }

    /// <summary>
    /// Gets the logger that every page logger writes to.
    /// </summary>
    internal ListLogger Logger { get; }

    /// <summary>
    /// Gets the renderer.
    /// </summary>
    internal ComponentTestRenderer Renderer { get; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Renders a page as the interactive renderer does.
    /// </summary>
    /// <typeparam name="TPage">The type of the page</typeparam>
    /// <returns>A task that returns the root component ID when the first render and its lifecycle are done</returns>
    internal Task<int> AddAsync<TPage>()
        where TPage : IComponent
    {
        return Renderer.AddAsync<TPage>(new Dictionary<string, object?>());
    }

    /// <summary>
    /// Renders a page as the prerender does: static, without a live connection.
    /// </summary>
    /// <typeparam name="TPage">The type of the page</typeparam>
    /// <returns>A task that returns the markup</returns>
    internal async Task<string> RenderStaticAsync<TPage>()
        where TPage : IComponent
    {
        await using var renderer = new HtmlRenderer(_services, _services.GetRequiredService<ILoggerFactory>());

        return await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<TPage>(ParameterView.Empty)).ToHtmlString());
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
    /// Returns the pieces of the report element of the rendered page.
    /// </summary>
    /// <returns>The pieces</returns>
    internal IReadOnlyList<string> Pieces()
    {
        return ReportPieces.FromHtml(Html());
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
    /// Returns the buttons and links with a text.
    /// </summary>
    /// <param name="text">The text of the button or link</param>
    /// <returns>The elements</returns>
    internal IReadOnlyList<RenderedElement> Actions(string text)
    {
        return Elements().Where(element => element.Tag is "button" or "a" && element.Text.Trim() == text).ToArray();
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
    /// Counts the queries for log lines that the scripted reader answered. A run over an empty reader makes exactly one.
    /// </summary>
    /// <returns>The number of queries for log lines</returns>
    internal int LogReads()
    {
        return Records.Queries.Count(query => query.Kind == RecordKind.LogLine);
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
        Service.Dispose();
    }

    #endregion // IAsyncDisposable
}