using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;

using Vandox.Backend.Components.Charts;
using Vandox.Backend.History;
using Vandox.Backend.Hosting;
using Vandox.Backend.Logs;
using Vandox.Backend.Network;

namespace Vandox.Backend.Components.Pages;

/// <summary>
/// The page <c>/network</c> of the web UI: connections per state and sockets per program over a chosen range, the top remote addresses and the listeners of a snapshot.
/// </summary>
public sealed partial class NetworkPage : ComponentBase, IDisposable
{
    #region Constants

    private const string PagePath = "network";

    #endregion // Constants

    #region Fields

    /// <summary>
    /// The runs of the chart reads.
    /// </summary>
    private PageRunner _chartRunner = null!;

    /// <summary>
    /// The runs of the address reads.
    /// </summary>
    private PageRunner _addressRunner = null!;

    /// <summary>
    /// The runs of the panel reads.
    /// </summary>
    private PageRunner _panelRunner = null!;

    /// <summary>
    /// The address read from the location.
    /// </summary>
    private NetworkAddress _address = new();

    /// <summary>
    /// The address the charts were read for, or <see langword="null"/> when none were.
    /// </summary>
    private NetworkAddress? _chartKey;

    /// <summary>
    /// The source, the range parameters and the moment the panel was read for, or <see langword="null"/> when none was.
    /// </summary>
    private (string Source, HistoryPreset? Preset, DateTimeOffset? From, DateTimeOffset? To, DateTimeOffset? At)? _panelKey;

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
    /// The text of a failed chart read.
    /// </summary>
    private string _failure = string.Empty;

    /// <summary>
    /// The text of a failed address read.
    /// </summary>
    private string _addressFailure = string.Empty;

    /// <summary>
    /// The text of a failed panel read.
    /// </summary>
    private string _panelFailure = string.Empty;

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
    /// Whether the last chart action failed, so that the source of the charts is unknown.
    /// </summary>
    private bool _sourceFailed;

    /// <summary>
    /// The result of the last chart read.
    /// </summary>
    private NetworkCharts? _charts;

    /// <summary>
    /// The range of the last chart read.
    /// </summary>
    private HistoryRange? _range;

    /// <summary>
    /// The chart of the states shown, with the moment of the address.
    /// </summary>
    private HistoryChartView? _statesView;

    /// <summary>
    /// The chart of the programs shown, with the moment of the address.
    /// </summary>
    private HistoryChartView? _programsView;

    /// <summary>
    /// The moment the charts mark.
    /// </summary>
    private DateTimeOffset? _marked;

    /// <summary>
    /// The table of the remote addresses.
    /// </summary>
    private NetworkAddresses? _addresses;

    /// <summary>
    /// The panel of the listeners.
    /// </summary>
    private NetworkListenerView? _panel;

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets the reader of the charts.
    /// </summary>
    [Inject]
    private NetworkChartReader ChartReader { get; set; } = null!;

    /// <summary>
    /// Gets or sets the reader of the remote addresses.
    /// </summary>
    [Inject]
    private NetworkAddressReader AddressReader { get; set; } = null!;

    /// <summary>
    /// Gets or sets the reader of the listeners.
    /// </summary>
    [Inject]
    private NetworkListenerReader ListenerReader { get; set; } = null!;

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
    private ILogger<NetworkPage> Logger { get; set; } = null!;

    /// <summary>
    /// Gets the source the page shows: the one of the address, else the one the charts chose; empty while none is known.
    /// </summary>
    private string ShownSource => _address.Source ?? (_sourceFailed ? string.Empty : _charts?.Source ?? string.Empty);

    /// <summary>
    /// Gets the sources the choice offers: those of the range, and the one of the address when it is not among them.
    /// </summary>
    private IReadOnlyList<string> Options
    {
        get
        {
            var sources = _charts?.Sources ?? [];

            return _address.Source is { } source && sources.Contains(source, StringComparer.Ordinal) == false ? [.. sources, source] : sources;
        }
    }

    /// <summary>
    /// Gets the text of the panel while the source is not known; empty when it is.
    /// </summary>
    private string SourceText
    {
        get
        {
            if (ShownSource.Length > 0)
            {
                return string.Empty;
            }

            if (_sourceFailed)
            {
                return NetworkView.SourceFailedText;
            }

            return _charts is null ? NetworkView.LoadingText : NetworkView.NoSourceText;
        }
    }

    /// <summary>
    /// Gets the two charts: the label, the note and the view of each.
    /// </summary>
    private IReadOnlyList<(string Label, string Note, HistoryChartView? View)> ChartHosts => [(NetworkView.StatesTitle, string.Empty, _statesView), (NetworkView.ProgramsTitle, NetworkView.ProgramNoteText, _programsView)];

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
        var address = NetworkAddress.Parse(new Uri(Navigation.Uri).Query);

        _address = address;
        _from = address.From is null ? string.Empty : LogsAddress.FormatTime(address.From.Value);
        _to = address.To is null ? string.Empty : LogsAddress.FormatTime(address.To.Value);

        if (address.Error.Length > 0)
        {
            ClearCharts();
            _error = address.Error;
            _future = false;

            return UpdatePanelAsync();
        }

        _error = string.Empty;
        _controlError = string.Empty;

        if (RendererInfo.IsInteractive == false)
        {
            return Task.CompletedTask;
        }

        MarkMoment();

        return Task.WhenAll(UpdateChartsAsync(), UpdatePanelAsync());
    }

    /// <summary>
    /// Forgets the charts and everything that depends on them.
    /// </summary>
    private void ClearCharts()
    {
        _chartRunner.CancelCurrent();
        _addressRunner.CancelCurrent();
        _chartKey = null;
        _charts = null;
        _range = null;
        _statesView = null;
        _programsView = null;
        _marked = null;
        _addresses = null;
        _failure = string.Empty;
        _addressFailure = string.Empty;
        _sourceFailed = false;
    }

    /// <summary>
    /// Starts the read of the charts when the address calls for other ones and the width is known.
    /// </summary>
    /// <returns>A task that completes when the read has ended</returns>
    private Task UpdateChartsAsync()
    {
        var now = Clock.GetUtcNow();

        _future = _address.Range(now).Start >= now;

        if (_future)
        {
            ClearCharts();

            return UpdatePanelAsync();
        }

        if (_widthKnown == false || _chartKey?.SameChart(_address) == true)
        {
            return Task.CompletedTask;
        }

        _chartKey = _address.Copy();

        return ReadChartsAsync(_chartKey, now);
    }

    /// <summary>
    /// Reads the charts of an address, then the table of the remote addresses and the panel of the source they chose.
    /// </summary>
    /// <param name="address">The address</param>
    /// <param name="now">The time of the read</param>
    /// <returns>A task that completes when the reads have ended</returns>
    private async Task ReadChartsAsync(NetworkAddress address, DateTimeOffset now)
    {
        var range = address.Range(now);
        var width = _width;
        NetworkCharts? loaded = null;

        _addressRunner.CancelCurrent();
        _addresses = null;
        _addressFailure = string.Empty;

        await _chartRunner.RunAsync(async token =>
                                    {
                                        _failure = string.Empty;
                                        _sourceFailed = false;
                                        StateHasChanged();

                                        var charts = await StoreReads.ReadingAsync(nameof(NetworkPage), () => ChartReader.ReadAsync(address.Source, range, width, token)).ConfigureAwait(true);

                                        token.ThrowIfCancellationRequested();

                                        _charts = charts;
                                        _range = range;
                                        _marked = null;
                                        MarkMoment();
                                        loaded = charts;
                                        StateHasChanged();
                                    }).ConfigureAwait(true);

        var addresses = loaded is { States: not null } && ReferenceEquals(_chartKey, address) ? ReadAddressesAsync(loaded, range) : Task.CompletedTask;

        await Task.WhenAll(addresses, UpdatePanelAsync()).ConfigureAwait(true);
    }

    /// <summary>
    /// Reads the table of the remote addresses at the range and the step of the charts.
    /// </summary>
    /// <param name="charts">The charts that were read</param>
    /// <param name="range">The range of the charts</param>
    /// <returns>A task that completes when the read has ended</returns>
    private Task ReadAddressesAsync(NetworkCharts charts, HistoryRange range)
    {
        return _addressRunner.RunAsync(async token =>
                                       {
                                           _addresses = null;
                                           _addressFailure = string.Empty;
                                           StateHasChanged();

                                           var addresses = await StoreReads.ReadingAsync(nameof(NetworkPage), () => AddressReader.ReadAsync(charts.Source, range, charts.Step, token)).ConfigureAwait(true);

                                           token.ThrowIfCancellationRequested();

                                           _addresses = addresses;
                                           StateHasChanged();
                                       });
    }

    /// <summary>
    /// Gives the charts the moment of the address when it is not the one they mark.
    /// </summary>
    private void MarkMoment()
    {
        var at = _address.At;

        if (_charts is null || _range is null || (_marked == at && _statesView is not null))
        {
            return;
        }

        _marked = at;
        _statesView = _charts.States?.WithMoment(at, _range);
        _programsView = _charts.Programs?.WithMoment(at, _range);
    }

    /// <summary>
    /// Starts the read of the panel when the source, the range or the moment changed, and shows no panel when there is nothing to read.
    /// </summary>
    /// <returns>A task that completes when the read has ended</returns>
    private Task UpdatePanelAsync()
    {
        if (_address.Error.Length == 0 && _future == false && ShownSource is { Length: > 0 } source)
        {
            var key = (source, _address.Preset, _address.From, _address.To, _address.At);

            if (key == _panelKey)
            {
                return Task.CompletedTask;
            }

            _panelKey = key;
            _panelFailure = string.Empty;

            return ReadPanelAsync(key, _address.Range(Clock.GetUtcNow()));
        }

        _panelRunner.CancelCurrent();
        _panelKey = null;
        _panelFailure = string.Empty;
        _panel = null;

        return Task.CompletedTask;
    }

    /// <summary>
    /// Reads the panel of the listeners.
    /// </summary>
    /// <param name="key">The source, the range parameters and the moment</param>
    /// <param name="range">The range</param>
    /// <returns>A task that completes when the read has ended</returns>
    private Task ReadPanelAsync((string Source, HistoryPreset? Preset, DateTimeOffset? From, DateTimeOffset? To, DateTimeOffset? At) key, HistoryRange range)
    {
        return _panelRunner.RunAsync(async token =>
                                     {
                                         _panel = null;
                                         StateHasChanged();

                                         var panel = await StoreReads.ReadingAsync(nameof(NetworkPage), () => ListenerReader.ReadAsync(key.Source, range, key.At, token)).ConfigureAwait(true);

                                         token.ThrowIfCancellationRequested();

                                         _panel = panel;
                                         StateHasChanged();
                                     });
    }

    /// <summary>
    /// Shows the text of a failed chart read.
    /// </summary>
    /// <param name="text">The text</param>
    private void FailChart(string text)
    {
        _failure = text;
        _sourceFailed = true;
    }

    /// <summary>
    /// Shows the text of a failed address read.
    /// </summary>
    /// <param name="text">The text</param>
    private void FailAddresses(string text)
    {
        _addressFailure = text;
    }

    /// <summary>
    /// Shows the text of a failed panel read.
    /// </summary>
    /// <param name="text">The text</param>
    private void FailPanel(string text)
    {
        _panelFailure = text;
    }

    /// <summary>
    /// Writes an address as the location.
    /// </summary>
    /// <param name="address">The address</param>
    /// <param name="replace">Whether the current entry of the history is replaced</param>
    private void Go(NetworkAddress address, bool replace)
    {
        _controlError = PageLocation.Go(Navigation, () => NetworkView.NetworkHref(address), replace);
    }

    /// <summary>
    /// Gives a copy of the address for a change.
    /// </summary>
    /// <returns>The copy without error</returns>
    private NetworkAddress Changed()
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

        address.Preset = preset;
        address.From = null;
        address.To = null;

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

        address.Preset = null;
        address.From = parsed.From;
        address.To = parsed.To;

        Go(address, false);
    }

    /// <summary>
    /// Writes the source the user chose, without the moment, which belongs to the former source; a value that is no listed source is ignored.
    /// </summary>
    /// <param name="arguments">The change of the choice</param>
    private void OnSourceChanged(ChangeEventArgs arguments)
    {
        var value = arguments.Value?.ToString() ?? string.Empty;

        if (_charts?.Sources.Contains(value, StringComparer.Ordinal) != true || string.Equals(value, _address.Source, StringComparison.Ordinal))
        {
            return;
        }

        var address = Changed();

        address.Source = value;
        address.At = null;

        Go(address, false);
    }

    /// <summary>
    /// Receives the width of a chart and reads the charts.
    /// </summary>
    /// <param name="width">The width in CSS pixels</param>
    /// <returns>A task that completes when the read has ended</returns>
    private Task OnWidthAsync(double width)
    {
        _width = width;
        _widthKnown = true;

        return _address.Error.Length > 0 ? Task.CompletedTask : UpdateChartsAsync();
    }

    /// <summary>
    /// Receives a selection of a chart and writes it as a fixed range.
    /// </summary>
    /// <param name="range">The selected range</param>
    /// <returns>A task that completes when the range is written</returns>
    private Task OnSelectAsync(HistoryRange range)
    {
        var address = Changed();

        address.Preset = null;
        address.From = range.Start;
        address.To = range.End;

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

        address.At = HistoryView.Pick([], pick);

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
        _chartRunner = new PageRunner(Clock, Logger.NetworkReadFailed, NetworkView.DeadlineText, NetworkView.FailedText, FailChart);
        _addressRunner = new PageRunner(Clock, Logger.NetworkReadFailed, NetworkView.DeadlineText, NetworkView.FailedText, FailAddresses);
        _panelRunner = new PageRunner(Clock, Logger.NetworkReadFailed, NetworkView.DeadlineText, NetworkView.FailedText, FailPanel);

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
        _chartRunner.Dispose();
        _addressRunner.Dispose();
        _panelRunner.Dispose();
    }

    #endregion // IDisposable
}