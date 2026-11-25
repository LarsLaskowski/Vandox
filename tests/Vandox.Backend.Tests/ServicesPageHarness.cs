using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Microsoft.JSInterop;

using Vandox.Backend.Components.Charts;
using Vandox.Backend.Components.Pages;
using Vandox.Backend.ServiceHealth;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Renders the services page over a <see cref="ServicesReader"/>, a <see cref="MariaDbReader"/> and a <see cref="ThreadSnapshotReader"/> on a
/// <see cref="FakeServiceHistoryReader"/>, a <see cref="FakeMariaDbHistoryReader"/> and a <see cref="ScriptedTimeSeriesReader"/>, a fake clock, a navigation manager that records its
/// navigations, a logger that keeps its messages and a script runtime that answers the width of every chart. The renderer is interactive like a circuit. A page is found by its text: the preset and
/// action buttons by their text, the fields by <c>name</c> (<c>from</c>, <c>to</c>), the chart hosts by the class <c>history-chart</c> and the sections by their <c>h2</c> headings. At the
/// start the services reader names <c>nginx.service</c> (two hours not available in 30 days) with one restart, the series reader names the five metrics without a point, the MariaDB reader
/// answers every step up with 1.2 GiB in use and holds one thread snapshot of five hours ago with one running thread, and the range of the address is the last 30 days.
/// The page is created with the services <see cref="ServicesReader"/>, <see cref="MariaDbReader"/>, <see cref="ThreadSnapshotReader"/>, <see cref="TimeProvider"/>,
/// <see cref="NavigationManager"/>, <see cref="ILogger{TCategoryName}"/> of <see cref="ServicesPage"/> and <see cref="IJSRuntime"/>.
/// </summary>
internal sealed class ServicesPageHarness : IAsyncDisposable
{
    #region Constants

    /// <summary>
    /// The component ID of the root component that <see cref="AddAsync"/> renders.
    /// </summary>
    internal const int RootId = 0;

    /// <summary>
    /// The width in CSS pixels that the script runtime answers for each chart.
    /// </summary>
    internal const double ChartWidth = 640;

    /// <summary>
    /// The number of charts of the MariaDB section.
    /// </summary>
    internal const int ChartCount = 5;

    /// <summary>
    /// The record ID of the thread snapshot at the start.
    /// </summary>
    internal const long SnapshotId = 7;

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
    /// Initializes a new instance of the <see cref="ServicesPageHarness"/> class.
    /// </summary>
    /// <param name="query">The query of the address of the page including the question mark, or an empty text</param>
    /// <param name="interactive">Whether the renderer is interactive like a circuit, else it prerenders</param>
    internal ServicesPageHarness(string query, bool interactive = true)
    {
        Services = new FakeServiceHistoryReader();
        MariaDb = new FakeMariaDbHistoryReader();
        Series = new ScriptedTimeSeriesReader();
        Navigation = new RecordingNavigationManager("http://localhost/services" + query);
        Clock = new FakeTimeProvider(Now);
        Logger = new ListLogger();
        Runtime = new RecordingJsRuntime();

        Services.Result = ServicesSamples.Availability(ServicesSamples.Unit(ServicesSamples.Nginx, TimeSpan.FromHours(718), TimeSpan.FromHours(2), TimeSpan.Zero, "active", "running", Now.AddDays(-1)));
        Series.Services.Add(ServicesSamples.RestartRecord(Now.AddDays(-3), ServicesSamples.Nginx, 2));

        foreach (var (source, name) in new[] { ("mariadb", "mariadb_memory_max_bytes"), ("system", "mem_total_bytes"), ("mariadb", "mariadb_questions_per_second"), ("mariadb", "mariadb_innodb_buffer_pool_used_percent"), ("mariadb", "mariadb_innodb_buffer_pool_read_hit_percent") })
        {
            Series.AddLine(source, name, string.Empty);
        }

        MariaDb.AnswerStatus = query => ServicesSamples.StatusAnswer(query);
        MariaDb.Stamps.Add(ServicesSamples.Stamp(SnapshotId, SnapshotAt, 40, 1, 125));
        MariaDb.Snapshots[SnapshotId] = ServicesSamples.Snapshot(SnapshotAt, 40, 1, 39, ServicesSamples.Thread(99, 125, "SELECT sleep(125)"));
        Runtime.Answer("module", "width", ChartWidth);

        var policy = HistoryPageHarness.Policy();
        var series = new TimeSeriesService(Series, policy, Clock);

        _services = new ServiceCollection().AddLogging(builder => builder.AddProvider(new ListLoggerProvider(Logger)))
                                           .AddSingleton(new ServicesReader(series, Services, Clock))
                                           .AddSingleton(new MariaDbReader(series, MariaDb, policy, Clock))
                                           .AddSingleton(new ThreadSnapshotReader(MariaDb))
                                           .AddSingleton<NavigationManager>(Navigation)
                                           .AddSingleton<TimeProvider>(Clock)
                                           .AddSingleton<IJSRuntime>(Runtime)
                                           .BuildServiceProvider();

        Renderer = new ComponentTestRenderer(_services, _services.GetRequiredService<ILoggerFactory>(), interactive)
                   {
                       Rendered = Pulse
                   };
        Runtime.Recorded = Pulse;
        Services.Entered = Pulse;
        MariaDb.Entered = Pulse;
        Series.Entered = Pulse;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the time of the page: its clock never moves unless a test advances it.
    /// </summary>
    internal static DateTimeOffset Now => HistoryPageHarness.Now;

    /// <summary>
    /// Gets the time of the thread snapshot at the start.
    /// </summary>
    internal static DateTimeOffset SnapshotAt => Now.AddHours(-5);

    /// <summary>
    /// Gets the service history reader.
    /// </summary>
    internal FakeServiceHistoryReader Services { get; }

    /// <summary>
    /// Gets the MariaDB history reader: the status, the stamps and the records of the thread snapshots.
    /// </summary>
    internal FakeMariaDbHistoryReader MariaDb { get; }

    /// <summary>
    /// Gets the reader of the series: the metrics of MariaDB and the restarts of the services.
    /// </summary>
    internal ScriptedTimeSeriesReader Series { get; }

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
    /// Gets the script runtime of the charts.
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
    /// Gets the calls that asked the browser for the width of a chart.
    /// </summary>
    internal IReadOnlyList<RecordedJsCall> Widths => [.. Runtime.Calls.Where(call => call.Identifier == "width")];

    /// <summary>
    /// Gets the number of calls of every reader of the page together: the store calls of the services, MariaDB and the series.
    /// </summary>
    internal int StoreCalls => Services.Calls.Count + MariaDb.Calls.Count + Series.Calls.Count;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Renders the page as the interactive renderer does. The task completes when the first render and its lifecycle are done; the reads of the page may run on.
    /// </summary>
    /// <returns>A task that returns the root component ID</returns>
    internal Task<int> AddAsync()
    {
        return Renderer.AddAsync<ServicesPage>(new Dictionary<string, object?>());
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
    /// Gets the text of the page without markup.
    /// </summary>
    /// <returns>The text</returns>
    internal string Text()
    {
        return string.Concat(Renderer.Tree(RootId).Select(element => element.Text));
    }

    /// <summary>
    /// Gets the text of the page from a second-level heading up to the next one, or up to the end of the page.
    /// </summary>
    /// <param name="heading">The text of the heading</param>
    /// <param name="next">The text of the next second-level heading, or <see langword="null"/> for the rest of the page</param>
    /// <returns>The text, empty when the page has no such heading</returns>
    internal string Section(string heading, string? next)
    {
        var headings = Renderer.Elements(RootId).Where(element => element.Tag == "h2").Select(element => element.Text.Trim()).ToArray();
        var start = Array.IndexOf(headings, heading);

        if (start < 0)
        {
            return string.Empty;
        }

        var text = Text();
        var marker = text.IndexOf(ServicesView.Heading, StringComparison.Ordinal) + ServicesView.Heading.Length;

        for (var index = 0; index < start; index++)
        {
            marker = text.IndexOf(headings[index], marker, StringComparison.Ordinal) + headings[index].Length;
        }

        var begin = text.IndexOf(heading, marker, StringComparison.Ordinal);
        var end = next is null ? -1 : text.IndexOf(next, begin + heading.Length, StringComparison.Ordinal);

        return end < 0 ? text[begin..] : text[begin..end];
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
    /// Waits until the text of the page contains a text.
    /// </summary>
    /// <param name="text">The text</param>
    /// <returns>A task that completes when the page shows the text</returns>
    internal Task WaitForTextAsync(string text)
    {
        return WaitForAsync(() => Text().Contains(text, StringComparison.Ordinal));
    }

    /// <summary>
    /// Waits until the charts were drawn a number of times.
    /// </summary>
    /// <param name="count">The number of draws</param>
    /// <returns>A task that completes when the charts were drawn that often</returns>
    internal Task WaitForDrawsAsync(int count)
    {
        return WaitForAsync(() => Drawn.Count >= count);
    }

    /// <summary>
    /// Waits until the MariaDB reader has had a call of a member.
    /// </summary>
    /// <param name="name">The member name</param>
    /// <returns>A task that completes when the reader has had a call of the member</returns>
    internal Task WaitForMariaDbCallAsync(string name)
    {
        return WaitForAsync(() => MariaDb.CallsOf(name).Count > 0);
    }

    /// <summary>
    /// Waits until the services, the charts and the thread panel of the page are shown.
    /// </summary>
    /// <returns>A task that completes when the page shows all three sections</returns>
    internal async Task WaitForLoadedAsync()
    {
        await WaitForTextAsync(ServicesSamples.Nginx);
        await WaitForDrawsAsync(ChartCount);
        await WaitForTextAsync("Threads of ");
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
    /// Gets the texts of the second-level headings in document order.
    /// </summary>
    /// <returns>The texts without surrounding white space</returns>
    internal IReadOnlyList<string> Headings()
    {
        return [.. Renderer.Elements(RootId).Where(element => element.Tag == "h2").Select(element => element.Text.Trim())];
    }

    /// <summary>
    /// Gets the texts of the third-level headings in document order.
    /// </summary>
    /// <returns>The texts without surrounding white space</returns>
    internal IReadOnlyList<string> SubHeadings()
    {
        return [.. Renderer.Elements(RootId).Where(element => element.Tag == "h3").Select(element => element.Text.Trim())];
    }

    /// <summary>
    /// Gets the rows of the table with a caption, each as its cells joined with a bar.
    /// </summary>
    /// <param name="caption">The caption</param>
    /// <returns>The rows; none when the page has no such table</returns>
    internal IReadOnlyList<string> TableRows(string caption)
    {
        var table = Renderer.Elements(RootId).FirstOrDefault(element => element.Tag == "table" && element.Descendants().Any(descendant => descendant.Tag == "caption" && descendant.Text == caption));

        if (table is null)
        {
            return [];
        }

        return [.. table.Descendants().Where(row => row.Tag == "tr" && row.Descendants().Any(cell => cell.Tag == "td")).Select(row => string.Join('|', row.Descendants().Where(cell => cell.Tag == "td").Select(cell => cell.Text)))];
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
        var button = Buttons(text).Single();

        if (button.Handlers.ContainsKey("onclick"))
        {
            return Renderer.ClickAsync(button);
        }

        var form = Renderer.Elements(RootId).First(element => element.Tag == "form" && element.Descendants().Any(descendant => ReferenceEquals(descendant, button)));

        return Renderer.SubmitAsync(form);
    }

    /// <summary>
    /// Gets the object that receives the browser callbacks of a chart drawn, as the browser holds it.
    /// </summary>
    /// <param name="index">The place of the draw in <see cref="Drawn"/></param>
    /// <returns>The events object</returns>
    internal HistoryChartEvents Events(int index)
    {
        return HistoryPageHarness.EventsOf(Drawn[index]);
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
        return Renderer.Dispatcher.InvokeAsync(() => Navigation.Go("services" + query));
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

        Services.Dispose();
        MariaDb.Dispose();
        Series.Dispose();
    }

    #endregion // IAsyncDisposable
}