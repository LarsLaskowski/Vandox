using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;

using Vandox.Backend.Components.Charts;
using Vandox.Backend.History;
using Vandox.Backend.Hosting;
using Vandox.Backend.Logs;
using Vandox.Backend.ServiceHealth;
using Vandox.Storage;

namespace Vandox.Backend.Components.Pages;

/// <summary>
/// The page <c>/services</c> of the web UI: the availability and restarts of the services, MariaDB's availability, memory budget and charts, and the thread snapshots.
/// </summary>
public sealed partial class ServicesPage : ComponentBase, IDisposable
{
    #region Constants

    private const string PagePath = "services";

    #endregion // Constants

    #region Fields

    /// <summary>
    /// The runs of the read of the services.
    /// </summary>
    private PageRunner _servicesRunner = null!;

    /// <summary>
    /// The runs of the read of MariaDB's data.
    /// </summary>
    private PageRunner _mariaDbRunner = null!;

    /// <summary>
    /// The runs of the read of the threads.
    /// </summary>
    private PageRunner _threadsRunner = null!;

    /// <summary>
    /// The address read from the location.
    /// </summary>
    private ServicesAddress _address = new();

    /// <summary>
    /// The address the services were read for, or <see langword="null"/> when they were not.
    /// </summary>
    private ServicesAddress? _servicesKey;

    /// <summary>
    /// The address MariaDB's data was read for, or <see langword="null"/> when it was not.
    /// </summary>
    private ServicesAddress? _mariaDbKey;

    /// <summary>
    /// The address the threads were read for, or <see langword="null"/> when they were not.
    /// </summary>
    private ServicesAddress? _threadsKey;

    /// <summary>
    /// The text of the start field.
    /// </summary>
    private string _from = string.Empty;

    /// <summary>
    /// The text of the end field.
    /// </summary>
    private string _to = string.Empty;

    /// <summary>
    /// The text of an error of the address; it replaces the page.
    /// </summary>
    private string _error = string.Empty;

    /// <summary>
    /// The text of a refusal of a control; it is shown with the working page.
    /// </summary>
    private string _controlError = string.Empty;

    /// <summary>
    /// The text of a failed read of the services.
    /// </summary>
    private string _servicesFailure = string.Empty;

    /// <summary>
    /// The text of a failed read of MariaDB's data.
    /// </summary>
    private string _mariaDbFailure = string.Empty;

    /// <summary>
    /// The text of a failed read of the threads.
    /// </summary>
    private string _threadsFailure = string.Empty;

    /// <summary>
    /// The width of the charts in CSS pixels, once known.
    /// </summary>
    private double _width;

    /// <summary>
    /// Whether the width is known.
    /// </summary>
    private bool _widthKnown;

    /// <summary>
    /// Whether the range lies in the future.
    /// </summary>
    private bool _future;

    /// <summary>
    /// The section of the services.
    /// </summary>
    private ServicesTable? _services;

    /// <summary>
    /// The section MariaDB.
    /// </summary>
    private MariaDbCharts? _mariaDb;

    /// <summary>
    /// The section of the long-running queries.
    /// </summary>
    private ThreadPanel? _panel;

    /// <summary>
    /// The range of the read of MariaDB's data.
    /// </summary>
    private HistoryRange? _range;

    /// <summary>
    /// The moment the charts mark.
    /// </summary>
    private DateTimeOffset? _marked;

    /// <summary>
    /// The chart of the availability with the moment of the address.
    /// </summary>
    private HistoryChartView? _availabilityView;

    /// <summary>
    /// The chart of the memory budget with the moment of the address.
    /// </summary>
    private HistoryChartView? _memoryView;

    /// <summary>
    /// The chart of the connections with the moment of the address.
    /// </summary>
    private HistoryChartView? _connectionsView;

    /// <summary>
    /// The chart of the queries per second with the moment of the address.
    /// </summary>
    private HistoryChartView? _queriesView;

    /// <summary>
    /// The chart of the InnoDB buffer pool with the moment of the address.
    /// </summary>
    private HistoryChartView? _bufferPoolView;

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets the reader of the services.
    /// </summary>
    [Inject]
    private ServicesReader ServicesReader { get; set; } = null!;

    /// <summary>
    /// Gets or sets the reader of MariaDB's data.
    /// </summary>
    [Inject]
    private MariaDbReader MariaDbReader { get; set; } = null!;

    /// <summary>
    /// Gets or sets the reader of the threads.
    /// </summary>
    [Inject]
    private ThreadSnapshotReader ThreadReader { get; set; } = null!;

    /// <summary>
    /// Gets or sets the clock.
    /// </summary>
    [Inject]
    private TimeProvider Clock { get; set; } = null!;

    /// <summary>
    /// Gets or sets the navigation manager.
    /// </summary>
    [Inject]
    private NavigationManager Navigation { get; set; } = null!;

    /// <summary>
    /// Gets or sets the logger.
    /// </summary>
    [Inject]
    private ILogger<ServicesPage> Logger { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Gives the parameters of a chart: its name and view and the callbacks of the width, the selection and the click.
    /// </summary>
    /// <param name="label">The accessible name of the chart</param>
    /// <param name="view">The view to draw</param>
    /// <returns>The parameters</returns>
    private Dictionary<string, object?> ChartParameters(string label, HistoryChartView? view)
    {
        return new Dictionary<string, object?>
               {
                   [nameof(HistoryChart.Label)] = label,
                   [nameof(HistoryChart.View)] = view,
                   [nameof(HistoryChart.OnWidth)] = EventCallback.Factory.Create<double>(this, OnWidthAsync),
                   [nameof(HistoryChart.OnSelect)] = EventCallback.Factory.Create<HistoryRange>(this, OnSelectAsync),
                   [nameof(HistoryChart.OnPick)] = EventCallback.Factory.Create<HistoryPick>(this, OnPickAsync)
               };
    }

    /// <summary>
    /// Reads the address of the location and starts the reads it calls for.
    /// </summary>
    /// <returns>A task that completes when the reads have ended</returns>
    private Task LoadAddressAsync()
    {
        var address = ServicesAddress.Parse(new Uri(Navigation.Uri).Query);

        _address = address;
        _from = address.Times.From is null ? string.Empty : LogsAddress.FormatTime(address.Times.From.Value);
        _to = address.Times.To is null ? string.Empty : LogsAddress.FormatTime(address.Times.To.Value);

        if (address.Error.Length > 0)
        {
            Clear();
            _error = address.Error;
            _future = false;

            return Task.CompletedTask;
        }

        _error = string.Empty;
        _controlError = string.Empty;

        return RendererInfo.IsInteractive ? UpdateAsync() : Task.CompletedTask;
    }

    /// <summary>
    /// Forgets everything that was read and stops the reads that run.
    /// </summary>
    private void Clear()
    {
        _servicesRunner.CancelCurrent();
        _mariaDbRunner.CancelCurrent();
        _threadsRunner.CancelCurrent();
        _servicesKey = null;
        _mariaDbKey = null;
        _threadsKey = null;
        _services = null;
        _mariaDb = null;
        _panel = null;
        _range = null;
        _marked = null;
        ClearViews();
        _servicesFailure = string.Empty;
        _mariaDbFailure = string.Empty;
        _threadsFailure = string.Empty;
    }

    /// <summary>
    /// Forgets the charts with their moment.
    /// </summary>
    private void ClearViews()
    {
        _availabilityView = null;
        _memoryView = null;
        _connectionsView = null;
        _queriesView = null;
        _bufferPoolView = null;
    }

    /// <summary>
    /// Starts the reads the address calls for: all three for another range, only the threads for another moment.
    /// </summary>
    /// <returns>A task that completes when the reads have ended</returns>
    private Task UpdateAsync()
    {
        var now = Clock.GetUtcNow();
        var range = _address.Range(now);

        _future = range.Start >= now;

        if (_future)
        {
            Clear();

            return Task.CompletedTask;
        }

        var reads = new List<Task>();

        MarkMoment();

        if (_servicesKey?.SameRange(_address) != true)
        {
            _servicesKey = _address.Copy();
            reads.Add(ReadServicesAsync(range));
        }

        if (_widthKnown && _mariaDbKey?.SameRange(_address) != true)
        {
            _mariaDbKey = _address.Copy();
            reads.Add(ReadMariaDbAsync(range));
        }

        if (_threadsKey?.SameRange(_address) != true || _threadsKey.Times.At != _address.Times.At)
        {
            _threadsKey = _address.Copy();
            reads.Add(ReadThreadsAsync(range, _address.Times.At));
        }

        return Task.WhenAll(reads);
    }

    /// <summary>
    /// Reads the section Services.
    /// </summary>
    /// <param name="range">The range</param>
    /// <returns>A task that completes when the read has ended</returns>
    private Task ReadServicesAsync(HistoryRange range)
    {
        return _servicesRunner.RunAsync(async token =>
                                        {
                                            _services = null;
                                            _servicesFailure = string.Empty;
                                            StateHasChanged();

                                            var table = await StoreReads.ReadingAsync(nameof(ServicesPage), () => ServicesReader.ReadAsync(range, token)).ConfigureAwait(true);

                                            token.ThrowIfCancellationRequested();

                                            _services = table;
                                            StateHasChanged();
                                        });
    }

    /// <summary>
    /// Reads the section MariaDB.
    /// </summary>
    /// <param name="range">The range</param>
    /// <returns>A task that completes when the read has ended</returns>
    private Task ReadMariaDbAsync(HistoryRange range)
    {
        var width = _width;

        return _mariaDbRunner.RunAsync(async token =>
                                       {
                                           _mariaDb = null;
                                           _mariaDbFailure = string.Empty;
                                           _marked = null;
                                           ClearViews();
                                           StateHasChanged();

                                           var charts = await StoreReads.ReadingAsync(nameof(ServicesPage), () => MariaDbReader.ReadAsync(range, width, token)).ConfigureAwait(true);

                                           token.ThrowIfCancellationRequested();

                                           _mariaDb = charts;
                                           _range = range;
                                           MarkMoment();
                                           StateHasChanged();
                                       });
    }

    /// <summary>
    /// Reads the section Long-running queries.
    /// </summary>
    /// <param name="range">The range</param>
    /// <param name="at">The moment of the address</param>
    /// <returns>A task that completes when the read has ended</returns>
    private Task ReadThreadsAsync(HistoryRange range, DateTimeOffset? at)
    {
        return _threadsRunner.RunAsync(async token =>
                                       {
                                           _panel = null;
                                           _threadsFailure = string.Empty;
                                           StateHasChanged();

                                           var panel = await StoreReads.ReadingAsync(nameof(ServicesPage), () => ThreadReader.ReadAsync(range, at, token)).ConfigureAwait(true);

                                           token.ThrowIfCancellationRequested();

                                           _panel = panel;
                                           StateHasChanged();
                                       });
    }

    /// <summary>
    /// Gives the charts the moment of the address when it is not the one they mark.
    /// </summary>
    private void MarkMoment()
    {
        var at = _address.Times.At;

        if (_mariaDb is null || _range is null || (_marked == at && _availabilityView is not null))
        {
            return;
        }

        _marked = at;
        _availabilityView = _mariaDb.Availability?.WithMoment(at, _range);
        _memoryView = _mariaDb.Memory?.WithMoment(at, _range);
        _connectionsView = _mariaDb.Connections?.WithMoment(at, _range);
        _queriesView = _mariaDb.Queries?.WithMoment(at, _range);
        _bufferPoolView = _mariaDb.BufferPool?.WithMoment(at, _range);
    }

    /// <summary>
    /// Shows the text of a failed read of the services.
    /// </summary>
    /// <param name="text">The text</param>
    private void FailServices(string text)
    {
        _services = null;
        _servicesFailure = text;
    }

    /// <summary>
    /// Shows the text of a failed read of MariaDB's data.
    /// </summary>
    /// <param name="text">The text</param>
    private void FailMariaDb(string text)
    {
        _mariaDb = null;
        _mariaDbFailure = text;
        ClearViews();
    }

    /// <summary>
    /// Shows the text of a failed read of the threads.
    /// </summary>
    /// <param name="text">The text</param>
    private void FailThreads(string text)
    {
        _panel = null;
        _threadsFailure = text;
    }

    /// <summary>
    /// Logs the failure of a read: the exception of the store, also when the store refused the query.
    /// </summary>
    /// <param name="exception">The exception of the read, or the one that wraps a refused query</param>
    private void LogFailure(Exception exception)
    {
        Logger.ServicesReadFailed(exception.InnerException as InvalidQueryException ?? exception);
    }

    /// <summary>
    /// Writes an address as the location.
    /// </summary>
    /// <param name="address">The address</param>
    /// <param name="replace">Whether the current entry of the history is replaced</param>
    private void Go(ServicesAddress address, bool replace)
    {
        _controlError = PageLocation.Go(Navigation, () => PagePath + address.ToQuery(), replace);
    }

    /// <summary>
    /// Gives a copy of the address for a change.
    /// </summary>
    /// <returns>The copy without error</returns>
    private ServicesAddress Changed()
    {
        var address = _address.Copy();

        address.Error = string.Empty;

        return address;
    }

    /// <summary>
    /// Writes a preset range.
    /// </summary>
    /// <param name="preset">The preset</param>
    private void ApplyPreset(HistoryPreset preset)
    {
        var address = Changed();

        address.Times.Preset = preset;
        address.Times.From = null;
        address.Times.To = null;

        Go(address, false);
    }

    /// <summary>
    /// Writes the range of the fields.
    /// </summary>
    private void Apply()
    {
        var parsed = HistoryRangeControls.ParseFields(_from, _to);

        if (parsed.Error.Length > 0)
        {
            _controlError = parsed.Error;

            return;
        }

        var address = Changed();

        address.Times.Preset = null;
        address.Times.From = parsed.From;
        address.Times.To = parsed.To;

        Go(address, false);
    }

    /// <summary>
    /// Writes the capture time of a snapshot as the moment, replacing the current entry of the history.
    /// </summary>
    /// <param name="capturedAt">The capture time</param>
    private void SetMoment(DateTimeOffset capturedAt)
    {
        var address = Changed();

        address.Times.At = new DateTimeOffset(capturedAt.UtcTicks - (capturedAt.UtcTicks % TimeSpan.TicksPerSecond), TimeSpan.Zero);

        Go(address, true);
    }

    /// <summary>
    /// Receives the width of a chart and reads MariaDB's data.
    /// </summary>
    /// <param name="width">The width in CSS pixels</param>
    /// <returns>A task that completes when the reads have ended</returns>
    private Task OnWidthAsync(double width)
    {
        _width = width;
        _widthKnown = true;

        return _address.Error.Length > 0 ? Task.CompletedTask : UpdateAsync();
    }

    /// <summary>
    /// Receives a selection of a chart and writes it as a fixed range.
    /// </summary>
    /// <param name="range">The selected range</param>
    /// <returns>A task that completes when the range is written</returns>
    private Task OnSelectAsync(HistoryRange range)
    {
        var address = Changed();

        address.Times.Preset = null;
        address.Times.From = range.Start;
        address.Times.To = range.End;

        Go(address, false);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Receives a click of a chart and writes it as the moment, replacing the current entry of the history.
    /// </summary>
    /// <param name="pick">The click</param>
    /// <returns>A task that completes when the moment is written</returns>
    private Task OnPickAsync(HistoryPick pick)
    {
        var address = Changed();

        address.Times.At = HistoryView.Pick([], pick);

        Go(address, true);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Handles a change of the location.
    /// </summary>
    /// <param name="sender">The navigation manager</param>
    /// <param name="arguments">The new location</param>
    private void OnLocationChanged(object? sender, LocationChangedEventArgs arguments)
    {
        if (PageLocation.IsPage(Navigation, arguments.Location, PagePath))
        {
            _ = InvokeAsync(HandleLocationAsync);
        }
    }

    /// <summary>
    /// Reads the new address and renders the page.
    /// </summary>
    /// <returns>A task that completes when the reads have ended</returns>
    private async Task HandleLocationAsync()
    {
        await LoadAddressAsync().ConfigureAwait(true);

        StateHasChanged();
    }

    #endregion // Methods

    #region ComponentBase

    /// <inheritdoc />
    protected override void OnInitialized()
    {
        _servicesRunner = new PageRunner(Clock, LogFailure, ServicesView.ServicesDeadlineText, ServicesView.ServicesFailedText, FailServices);
        _mariaDbRunner = new PageRunner(Clock, LogFailure, ServicesView.MariaDbDeadlineText, ServicesView.MariaDbFailedText, FailMariaDb);
        _threadsRunner = new PageRunner(Clock, LogFailure, ServicesView.ThreadsDeadlineText, ServicesView.ThreadsFailedText, FailThreads);

        if (RendererInfo.IsInteractive)
        {
            Navigation.LocationChanged += OnLocationChanged;
        }
    }

    /// <inheritdoc />
    protected override Task OnParametersSetAsync()
    {
        return LoadAddressAsync();
    }

    #endregion // ComponentBase

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        Navigation.LocationChanged -= OnLocationChanged;
        _servicesRunner.Dispose();
        _mariaDbRunner.Dispose();
        _threadsRunner.Dispose();
    }

    #endregion // IDisposable
}