using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Microsoft.JSInterop;

using Vandox.Backend.Components.Charts;
using Vandox.Backend.Components.Pages;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Renders the history page over a real <see cref="TimeSeriesService"/> on a <see cref="ScriptedTimeSeriesReader"/>, a <see cref="FakeHistoryReader"/>, a fake clock,
/// a navigation manager that records its navigations, a logger that keeps its messages and a script runtime that answers the width of the chart. The renderer is
/// interactive like a circuit. A page is found by its text: the preset and action buttons by their text, the fields by <c>name</c> (<c>from</c>, <c>to</c>,
/// <c>metric</c>), and the chart host by the class <c>history-chart</c>.
/// </summary>
internal sealed class HistoryPageHarness : IAsyncDisposable
{
    #region Constants

    /// <summary>
    /// The component ID of the root component that <see cref="AddAsync"/> renders.
    /// </summary>
    internal const int RootId = 0;

    /// <summary>
    /// The width in CSS pixels that the script runtime answers for the chart.
    /// </summary>
    internal const double ChartWidth = 640;

    #endregion // Constants

    #region Fields

    /// <summary>
    /// The services the page is created from.
    /// </summary>
    private readonly ServiceProvider _services;

    /// <summary>
    /// Guards the signal.
    /// </summary>
    private readonly Lock _lock = new();

    /// <summary>
    /// Completed by the next change of the page, a call or a render.
    /// </summary>
    private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
    /// Initializes a new instance of the <see cref="HistoryPageHarness"/> class.
    /// </summary>
    /// <param name="query">The query of the address of the page including the question mark, or an empty text</param>
    internal HistoryPageHarness(string query)
    {
        Series = new ScriptedTimeSeriesReader();
        Reader = new FakeHistoryReader();
        Navigation = new RecordingNavigationManager("http://localhost/history" + query);
        Clock = new FakeTimeProvider(Now);
        Logger = new ListLogger();
        Runtime = new RecordingJsRuntime();

        Runtime.Answer("module", "width", ChartWidth);

        _services = new ServiceCollection().AddLogging(builder => builder.AddProvider(new ListLoggerProvider(Logger)))
                                           .AddSingleton(new TimeSeriesService(Series, Policy(), Clock))
                                           .AddSingleton<IHistoryReader>(Reader)
                                           .AddSingleton<NavigationManager>(Navigation)
                                           .AddSingleton<TimeProvider>(Clock)
                                           .AddSingleton<IJSRuntime>(Runtime)
                                           .BuildServiceProvider();

        Renderer = new ComponentTestRenderer(_services, _services.GetRequiredService<ILoggerFactory>())
                   {
                       Rendered = Pulse
                   };
        Runtime.Recorded = Pulse;
        Series.Entered = Pulse;
        Reader.Entered = Pulse;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the time of the page: its clock never moves unless a test advances it.
    /// </summary>
    internal static DateTimeOffset Now { get; } = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Gets the reader of the time-series service.
    /// </summary>
    internal ScriptedTimeSeriesReader Series { get; }

    /// <summary>
    /// Gets the history reader: the metric list and the snapshots.
    /// </summary>
    internal FakeHistoryReader Reader { get; }

    /// <summary>
    /// Gets the navigation manager.
    /// </summary>
    internal RecordingNavigationManager Navigation { get; }

    /// <summary>
    /// Gets the clock of the page.
    /// </summary>
    internal FakeTimeProvider Clock { get; }

    /// <summary>
    /// Gets the logger every page logs to.
    /// </summary>
    internal ListLogger Logger { get; }

    /// <summary>
    /// Gets the script runtime of the chart.
    /// </summary>
    internal RecordingJsRuntime Runtime { get; }

    /// <summary>
    /// Gets the interactive renderer.
    /// </summary>
    internal ComponentTestRenderer Renderer { get; }

    /// <summary>
    /// Gets the calls that drew a chart, in order.
    /// </summary>
    internal IReadOnlyList<RecordedJsCall> Drawn => [.. Runtime.Calls.Where(call => call.Identifier == "createHistory")];

    /// <summary>
    /// Gets the calls that marked a moment, in order.
    /// </summary>
    internal IReadOnlyList<RecordedJsCall> Marked => [.. Runtime.Calls.Where(call => call.Target == "chart" && call.Identifier == "mark")];

    /// <summary>
    /// Gets the snapshot stamp reads of the history reader, in order.
    /// </summary>
    internal IReadOnlyList<(string Name, object? Argument, CancellationToken Token)> StampReads => [.. Reader.Calls.Where(call => call.Name == nameof(IHistoryReader.SnapshotStampAsync))];

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Gets the object that receives the browser callbacks from a call that drew a chart.
    /// </summary>
    /// <param name="draw">The call</param>
    /// <returns>The events object</returns>
    internal static HistoryChartEvents EventsOf(RecordedJsCall draw)
    {
        return ((DotNetObjectReference<HistoryChartEvents>)draw.Arguments[3]!).Value;
    }

    /// <summary>
    /// Creates the retention periods of the default configuration.
    /// </summary>
    /// <returns>The policy</returns>
    internal static RetentionPolicy Policy()
    {
        return new RetentionPolicy
               {
                   Raw = TimeSpan.FromDays(30),
                   FiveMinuteRollups = TimeSpan.FromDays(365),
                   HourlyRollups = TimeSpan.FromDays(1095),
                   Logs = TimeSpan.FromDays(90),
                   Snapshots = TimeSpan.FromDays(30),
                   Events = TimeSpan.FromDays(1095),
                   KillWindowBefore = TimeSpan.FromHours(1),
                   KillWindowAfter = TimeSpan.FromMinutes(30)
               };
    }

    /// <summary>
    /// Renders the page as the interactive renderer does. The task completes when the first render and its lifecycle are done; the reads of the page may run on.
    /// </summary>
    /// <returns>A task that returns the root component ID</returns>
    internal Task<int> AddAsync()
    {
        return Renderer.AddAsync<HistoryPage>(new Dictionary<string, object?>());
    }

    /// <summary>
    /// Renders the page statically, as the prerender does.
    /// </summary>
    /// <returns>A task that returns the markup</returns>
    internal async Task<string> RenderStaticAsync()
    {
        await using var renderer = new HtmlRenderer(_services, _services.GetRequiredService<ILoggerFactory>());

        return await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<HistoryPage>()).ToHtmlString());
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
    /// Waits until a condition holds. The condition runs on the dispatcher of the renderer after each render, each call of the script runtime and each call of a
    /// reader; a condition that never holds ends the wait after 5 seconds.
    /// </summary>
    /// <param name="condition">The condition</param>
    /// <returns>A task that completes when the condition holds</returns>
    internal async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        while (true)
        {
            Task next;

            lock (_lock)
            {
                next = _signal.Task;
            }

            if (await Renderer.Dispatcher.InvokeAsync(condition))
            {
                return;
            }

            await next.WaitAsync(timeout.Token);
        }
    }

    /// <summary>
    /// Waits until the markup meets a condition.
    /// </summary>
    /// <param name="condition">The condition</param>
    /// <returns>A task that completes when the condition holds</returns>
    internal Task WaitForHtmlAsync(Func<string, bool> condition)
    {
        return WaitForAsync(() => condition(Html()));
    }

    /// <summary>
    /// Waits until the chart was drawn a number of times.
    /// </summary>
    /// <param name="count">The number of draws</param>
    /// <returns>A task that completes when the chart was drawn that often</returns>
    internal Task WaitForDrawsAsync(int count)
    {
        return WaitForAsync(() => Drawn.Count >= count);
    }

    /// <summary>
    /// Waits until the time-series reader has had a number of raw value reads.
    /// </summary>
    /// <param name="count">The number of reads</param>
    /// <returns>A task that completes when the reader has had that many</returns>
    internal Task WaitForRawReadsAsync(int count)
    {
        return WaitForAsync(() => Series.RawQueries.Count >= count);
    }

    /// <summary>
    /// Waits until the history reader has had a number of snapshot stamp reads.
    /// </summary>
    /// <param name="count">The number of reads</param>
    /// <returns>A task that completes when the reader has had that many</returns>
    internal Task WaitForStampReadsAsync(int count)
    {
        return WaitForAsync(() => StampReads.Count >= count);
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
    /// Finds the buttons whose text starts with a text.
    /// </summary>
    /// <param name="prefix">The start of the text of the button</param>
    /// <returns>The buttons in document order</returns>
    internal IReadOnlyList<RenderedElement> ButtonsStartingWith(string prefix)
    {
        return Renderer.Elements(RootId).Where(element => element.Tag == "button" && element.Text.Trim().StartsWith(prefix, StringComparison.Ordinal)).ToArray();
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
    /// Finds the input or select with a name.
    /// </summary>
    /// <param name="name">The value of the name attribute</param>
    /// <returns>The element</returns>
    internal RenderedElement Field(string name)
    {
        return Renderer.Elements(RootId).FirstOrDefault(element => element.Tag is "input" or "select" && element.Attributes.GetValueOrDefault("name") == name)
                   ?? throw new InvalidOperationException($"The page has no field named {name}.");
    }

    /// <summary>
    /// Finds the elements that have a class.
    /// </summary>
    /// <param name="className">The class name</param>
    /// <returns>The elements in document order</returns>
    internal IReadOnlyList<RenderedElement> WithClass(string className)
    {
        return Renderer.Elements(RootId).Where(element => element.Classes.Contains(className)).ToArray();
    }

    /// <summary>
    /// Gets the options of a select: the value and the text of each.
    /// </summary>
    /// <param name="name">The name of the select</param>
    /// <returns>The options in order</returns>
    internal IReadOnlyList<(string Value, string Text)> Options(string name)
    {
        return [.. Field(name).Descendants().Where(element => element.Tag == "option").Select(element => (element.Attributes.GetValueOrDefault("value") ?? string.Empty, element.Text.Trim()))];
    }

    /// <summary>
    /// Gets the text of the page without markup.
    /// </summary>
    /// <returns>The text</returns>
    internal string Text()
    {
        return string.Concat(Renderer.Tree(RootId).Select(element => element.Text));
    }

    /// <summary>
    /// Enters values in fields: runs their change handlers.
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

    /// <summary>
    /// Presses the only button with a text: clicks it, or when it has no click handler submits the form that holds it.
    /// </summary>
    /// <param name="text">The text of the button</param>
    /// <returns>A task that completes when the handler is done</returns>
    internal Task PressAsync(string text)
    {
        return PressAsync(Buttons(text).Single());
    }

    /// <summary>
    /// Presses a button: clicks it, or when it has no click handler submits the form that holds it.
    /// </summary>
    /// <param name="button">The button</param>
    /// <returns>A task that completes when the handler is done</returns>
    internal Task PressAsync(RenderedElement button)
    {
        if (button.Handlers.ContainsKey("onclick"))
        {
            return Renderer.ClickAsync(button);
        }

        var form = Renderer.Elements(RootId).First(element => element.Tag == "form" && element.Descendants().Any(descendant => ReferenceEquals(descendant, button)));

        return Renderer.SubmitAsync(form);
    }

    /// <summary>
    /// Submits the form that holds the field with a name, as a press of its submit button does.
    /// </summary>
    /// <param name="fieldName">The name of a field of the form</param>
    /// <returns>A task that completes when the handler is done</returns>
    internal Task SubmitFormOfAsync(string fieldName)
    {
        var field = Field(fieldName);
        var form = Renderer.Elements(RootId).First(element => element.Tag == "form" && element.Descendants().Any(descendant => ReferenceEquals(descendant, field)));

        return Renderer.SubmitAsync(form);
    }

    /// <summary>
    /// Gets the object that receives the browser callbacks of the chart drawn last, as the browser holds it.
    /// </summary>
    /// <returns>The events object</returns>
    internal HistoryChartEvents Events()
    {
        return EventsOf(Drawn[^1]);
    }

    /// <summary>
    /// Runs a callback on the dispatcher of the renderer, as the circuit does for a call from the browser.
    /// </summary>
    /// <param name="callback">The callback</param>
    /// <returns>A task that completes when the callback is done</returns>
    internal Task OnDispatcherAsync(Func<Task> callback)
    {
        return Renderer.Dispatcher.InvokeAsync(callback);
    }

    /// <summary>
    /// Changes the address of the page as the browser does after the address was edited: the page is told, no navigation is recorded.
    /// </summary>
    /// <param name="query">The query including the question mark, or an empty text</param>
    /// <returns>A task that completes when the page has handled the change</returns>
    internal Task GoToAsync(string query)
    {
        return Renderer.Dispatcher.InvokeAsync(() => Navigation.Go("history" + query));
    }

    /// <summary>
    /// Counts the options of a select, or zero when the page has no such select.
    /// </summary>
    /// <param name="name">The name of the select</param>
    /// <returns>The number of options</returns>
    internal int OptionCount(string name)
    {
        var select = Renderer.Elements(RootId).FirstOrDefault(element => element.Tag == "select" && element.Attributes.GetValueOrDefault("name") == name);

        return select?.Descendants().Count(element => element.Tag == "option") ?? 0;
    }

    /// <summary>
    /// Disposes the renderer and with it the page, as leaving the page does.
    /// </summary>
    /// <returns>A task that completes when the page is disposed</returns>
    internal Task DisposePageAsync()
    {
        if (_rendererDisposed)
        {
            return Task.CompletedTask;
        }

        _rendererDisposed = true;

        return Renderer.DisposeAllAsync();
    }

    /// <summary>
    /// Wakes the waiters.
    /// </summary>
    private void Pulse()
    {
        lock (_lock)
        {
            var current = _signal;

            _signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            current.TrySetResult();
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

        await DisposePageAsync();
        await _services.DisposeAsync();

        Series.Dispose();
        Reader.Dispose();
    }

    #endregion // IAsyncDisposable
}