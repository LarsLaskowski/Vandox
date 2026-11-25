using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Microsoft.JSInterop;

using Vandox.Backend.Components.Charts;
using Vandox.Backend.Components.Pages;
using Vandox.Backend.Network;
using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Renders the network page over a <see cref="NetworkChartReader"/> and a <see cref="NetworkAddressReader"/> on a <see cref="FakeConnectionRollupReader"/>, a
/// <see cref="NetworkListenerReader"/> on a <see cref="FakeHistoryReader"/>, a fake clock, a navigation manager that records its navigations, a logger that keeps its messages and a script runtime that
/// answers the width of both charts. The renderer is interactive like a circuit. A page is found by its text: the preset and action buttons by their text, the
/// fields by <c>name</c> (<c>from</c>, <c>to</c>, <c>source</c>), the chart hosts by the class <c>history-chart</c> and the sections by their <c>h2</c> headings. The rollup reader starts with one source,
/// <c>agent</c>, whose latest bucket is five minutes old, the rollups of two hours ago, one remote address and, in the history reader, one connection snapshot of
/// <c>agent</c> five minutes old with the listener on port 22.
/// The page is created with the services <see cref="NetworkChartReader"/>, <see cref="NetworkAddressReader"/>, <see cref="NetworkListenerReader"/>, <see cref="TimeProvider"/>,
/// <see cref="NavigationManager"/>, <see cref="ILogger{TCategoryName}"/> of <see cref="NetworkPage"/> and <see cref="IJSRuntime"/>.
/// </summary>
internal sealed class NetworkPageHarness : IAsyncDisposable
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
    /// The source the rollup reader lists at the start.
    /// </summary>
    internal const string Agent = "agent";

    /// <summary>
    /// The only remote address of the address result at the start.
    /// </summary>
    internal const string FirstAddress = "203.0.113.5";

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

    /// <summary>
    /// The task the address reads wait for, or <c>null</c> to answer at once.
    /// </summary>
    private Task? _addressHold;

    /// <summary>
    /// The exception the address reads throw, or <c>null</c> to answer.
    /// </summary>
    private Exception? _addressFailure;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="NetworkPageHarness"/> class.
    /// </summary>
    /// <param name="query">The query of the address of the page including the question mark, or an empty text</param>
    /// <param name="interactive">Whether the renderer is interactive like a circuit, else it prerenders</param>
    internal NetworkPageHarness(string query, bool interactive = true)
    {
        Rollups = new FakeConnectionRollupReader();
        Reader = new FakeHistoryReader();
        Navigation = new RecordingNavigationManager("http://localhost/network" + query);
        Clock = new FakeTimeProvider(Now);
        Logger = new ListLogger();
        Runtime = new RecordingJsRuntime();

        Rollups.Sources = ProcessesSamples.SourceList(false, (Agent, Now.AddMinutes(-5)));
        Rollups.Rollups = NetworkSamples.Rollups(Now.AddHours(-2),
                                                 [NetworkSamples.Line("ESTABLISHED", 3, 4), NetworkSamples.Line("UDP", 1, 1)],
                                                 [NetworkSamples.Line("nginx", 2, 3), NetworkSamples.Line("mysqld", 1, 1)]);
        Rollups.Addresses = NetworkSamples.Addresses(Now.AddHours(-2), NetworkSamples.Address(FirstAddress, 4.5, 9));
        AddConnections(NetworkSamples.Connections(1, Agent, Now.AddMinutes(-5), "all", NetworkSamples.Tcp(22)));
        Runtime.Answer("module", "width", ChartWidth);

        _services = new ServiceCollection().AddLogging(builder => builder.AddProvider(new ListLoggerProvider(Logger)))
                                           .AddSingleton(new NetworkChartReader(Rollups, HistoryPageHarness.Policy(), Clock))
                                           .AddSingleton(new NetworkAddressReader(Rollups))
                                           .AddSingleton(new NetworkListenerReader(Reader))
                                           .AddSingleton<NavigationManager>(Navigation)
                                           .AddSingleton<TimeProvider>(Clock)
                                           .AddSingleton<IJSRuntime>(Runtime)
                                           .BuildServiceProvider();

        Renderer = new ComponentTestRenderer(_services, _services.GetRequiredService<ILoggerFactory>(), interactive)
                   {
                       Rendered = Pulse
                   };
        Runtime.Recorded = Pulse;
        Rollups.Entered = OnRollupEntered;
        Reader.Entered = Pulse;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the time of the page: its clock never moves unless a test advances it.
    /// </summary>
    internal static DateTimeOffset Now => HistoryPageHarness.Now;

    /// <summary>
    /// Gets the reader of the connection rollups.
    /// </summary>
    internal FakeConnectionRollupReader Rollups { get; }

    /// <summary>
    /// Gets the history reader: the stamps and records of the connection snapshots.
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
    /// Gets the reads of the latest snapshot stamp of the history reader, in order.
    /// </summary>
    internal IReadOnlyList<(string Name, object? Argument, CancellationToken Token)> LatestReads => [.. Reader.Calls.Where(call => call.Name == nameof(IHistoryReader.LatestSnapshotStampAsync))];

    /// <summary>
    /// Gets the queries of the reads of the latest snapshot stamp, in order.
    /// </summary>
    internal IReadOnlyList<LatestSnapshotQuery> LatestQueries => [.. LatestReads.Select(call => (LatestSnapshotQuery)call.Argument!)];

    /// <summary>
    /// Gets the queries of the reads of the snapshot stamp at or near a moment, in order.
    /// </summary>
    internal IReadOnlyList<SnapshotQuery> StampQueries => [.. Reader.Calls.Where(call => call.Name == nameof(IHistoryReader.SnapshotStampAsync)).Select(call => (SnapshotQuery)call.Argument!)];

    /// <summary>
    /// Gets the calls of the rollup reader that read the addresses, in order.
    /// </summary>
    internal IReadOnlyList<(string Name, object? Argument, CancellationToken Token)> AddressReads => [.. Rollups.Calls.Where(call => call.Name == nameof(IConnectionRollupReader.ConnectionAddressesAsync))];

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Stores a connection snapshot the history reader finds by its time and source.
    /// </summary>
    /// <param name="record">The record</param>
    internal void AddConnections(StoredRecord record)
    {
        Reader.Stamps.Add(HistorySamples.Stamp(record.Id, RecordKind.ConnectionSnapshot, record.Record.CapturedAt));
        Reader.Records[record.Id] = record;
    }

    /// <summary>
    /// Makes the reads of the addresses wait for a task, the other reads of the rollups not.
    /// </summary>
    /// <param name="hold">The task</param>
    internal void HoldAddresses(Task hold)
    {
        _addressHold = hold;
    }

    /// <summary>
    /// Makes the reads of the addresses fail, the other reads of the rollups not.
    /// </summary>
    /// <param name="failure">The exception</param>
    internal void FailAddresses(Exception failure)
    {
        _addressFailure = failure;
    }

    /// <summary>
    /// Renders the page as the interactive renderer does. The task completes when the first render and its lifecycle are done; the reads of the page may run on.
    /// </summary>
    /// <returns>A task that returns the root component ID</returns>
    internal Task<int> AddAsync()
    {
        return Renderer.AddAsync<NetworkPage>(new Dictionary<string, object?>());
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
    /// Waits until the markup meets a condition.
    /// </summary>
    /// <param name="condition">The condition</param>
    /// <returns>A task that completes when the condition holds</returns>
    internal Task WaitForHtmlAsync(Func<string, bool> condition)
    {
        return WaitForAsync(() => condition(Html()));
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
    /// Waits until the rollup reader has had a number of calls, source lists and rollup reads together.
    /// </summary>
    /// <param name="count">The number of calls</param>
    /// <returns>A task that completes when the reader has had that many</returns>
    internal Task WaitForRollupCallsAsync(int count)
    {
        return WaitForAsync(() => Rollups.Calls.Count >= count);
    }

    /// <summary>
    /// Waits until the history reader has had a number of calls of any kind.
    /// </summary>
    /// <param name="count">The number of calls</param>
    /// <returns>A task that completes when the reader has had that many</returns>
    internal Task WaitForReaderCallsAsync(int count)
    {
        return WaitForAsync(() => Reader.Calls.Count >= count);
    }

    /// <summary>
    /// Waits until the charts, the table and the panel of the page are shown.
    /// </summary>
    /// <returns>A task that completes when the page shows all four sections</returns>
    internal async Task WaitForLoadedAsync()
    {
        await WaitForDrawsAsync(2);
        await WaitForTextAsync(FirstAddress);
        await WaitForTextAsync("Listeners of ");
    }

    /// <summary>
    /// Waits until the script runtime was asked for a number of widths.
    /// </summary>
    /// <param name="count">The number of widths</param>
    /// <returns>A task that completes when the runtime was asked that often</returns>
    internal Task WaitForWidthsAsync(int count)
    {
        return WaitForAsync(() => Widths.Count >= count);
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
    /// Gets the texts of the second-level headings in document order.
    /// </summary>
    /// <returns>The texts without surrounding white space</returns>
    internal IReadOnlyList<string> Headings()
    {
        return [.. Renderer.Elements(RootId).Where(element => element.Tag == "h2").Select(element => element.Text.Trim())];
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
    /// Chooses the option of the source select that shows a source, as the user does.
    /// </summary>
    /// <param name="source">The text of the option</param>
    /// <returns>A task that completes when the handler is done</returns>
    internal Task ChooseSourceAsync(string source)
    {
        return EnterAsync(("source", Options("source").Single(option => option.Text == source).Value));
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
    /// Gets the object that receives the browser callbacks of a chart drawn, as the browser holds it. The first chart drawn is the memory chart.
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
        return Renderer.Dispatcher.InvokeAsync(() => Navigation.Go("network" + query));
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
    /// Wakes the waiters, and gives a read of the addresses the hold or failure that was asked for.
    /// </summary>
    private void OnRollupEntered()
    {
        var sequence = Rollups.Sequence;

        if (sequence.Count > 0 && sequence[^1] == "Addresses")
        {
            Rollups.Hold = _addressHold ?? Rollups.Hold;
            Rollups.Failure = _addressFailure ?? Rollups.Failure;
        }

        Pulse();
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

        Rollups.Dispose();
        Reader.Dispose();
    }

    #endregion // IAsyncDisposable
}