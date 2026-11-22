using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;

using Vandox.Backend.Components.Charts;
using Vandox.Backend.History;
using Vandox.Backend.Hosting;
using Vandox.Backend.Logs;
using Vandox.Backend.Processes;

namespace Vandox.Backend.Components.Pages;

/// <summary>
/// The page <c>/processes</c> of the web UI: memory and CPU per program over a chosen range, and the top processes of a moment.
/// </summary>
public sealed partial class ProcessesPage : ComponentBase, IDisposable
{
    #region Constants

    private const string PagePath = "processes";

    private const string ViewName = "processes view";

    private const string MemoryLabel = "Memory per program";

    private const string CpuLabel = "CPU per program";

    private const string ArmedText = "Click a second point in a chart to compare.";

    #endregion // Constants

    #region Fields

    /// <summary>
    /// The runs of the chart reads.
    /// </summary>
    private PageRunner _chartRunner = null!;

    /// <summary>
    /// The runs of the snapshot reads.
    /// </summary>
    private PageRunner _momentRunner = null!;

    /// <summary>
    /// The address read from the location.
    /// </summary>
    private ProcessesAddress _address = new();

    /// <summary>
    /// The address the charts were read for, or <see langword="null"/> when none were.
    /// </summary>
    private ProcessesAddress? _chartKey;

    /// <summary>
    /// The source, the moment and the second point the panel was read for, or <see langword="null"/> when none was.
    /// </summary>
    private (string Source, DateTimeOffset At, DateTimeOffset? Compare)? _momentKey;

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
    /// The text of a failed snapshot read.
    /// </summary>
    private string _momentFailure = string.Empty;

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
    /// Whether the next click in a chart writes the second point of a comparison.
    /// </summary>
    private bool _armed;

    /// <summary>
    /// The result of the last chart read.
    /// </summary>
    private ProcessesCharts? _charts;

    /// <summary>
    /// The range of the last chart read.
    /// </summary>
    private HistoryRange? _range;

    /// <summary>
    /// The memory chart shown, with the moment of the address.
    /// </summary>
    private HistoryChartView? _memoryView;

    /// <summary>
    /// The CPU chart shown, with the moment of the address.
    /// </summary>
    private HistoryChartView? _cpuView;

    /// <summary>
    /// The moment the charts mark.
    /// </summary>
    private DateTimeOffset? _marked;

    /// <summary>
    /// The snapshot of the moment.
    /// </summary>
    private HistoryMoment? _moment;

    /// <summary>
    /// The comparison of the two points.
    /// </summary>
    private ProgramComparisonView? _comparison;

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets the reader of the charts.
    /// </summary>
    [Inject]
    private ProcessesChartReader ChartReader { get; set; } = null!;

    /// <summary>
    /// Gets or sets the reader of the snapshots.
    /// </summary>
    [Inject]
    private ProcessesMomentReader MomentReader { get; set; } = null!;

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
    private ILogger<ProcessesPage> Logger { get; set; } = null!;

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
                return ProcessesView.SourceFailureText;
            }

            return _charts is null ? HistoryView.LoadingText : ProcessesView.NoSourceText;
        }
    }

    /// <summary>
    /// Gets the two charts: the label, the note and the view of each.
    /// </summary>
    private IReadOnlyList<(string Label, string Note, HistoryChartView? View)> ChartHosts => [(MemoryLabel, _charts?.MemoryNote ?? string.Empty, _memoryView), (CpuLabel, string.Empty, _cpuView)];

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Gives the link to the history view of a moment.
    /// </summary>
    /// <param name="at">The moment</param>
    /// <returns>The link</returns>
    private static string HistoryHref(DateTimeOffset at)
    {
        return HistoryView.HistoryHref(new HistoryAddress
                                       {
                                           At = at
                                       });
    }

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
        var address = ProcessesAddress.Parse(new Uri(Navigation.Uri).Query);

        _address = address;
        _from = address.From is null ? string.Empty : LogsAddress.FormatTime(address.From.Value);
        _to = address.To is null ? string.Empty : LogsAddress.FormatTime(address.To.Value);

        if (address.At is null || address.Compare is not null)
        {
            _armed = false;
        }

        if (address.Error.Length > 0)
        {
            _chartRunner.CancelCurrent();
            ClearCharts();
            _error = address.Error;
            _future = false;

            return UpdateMomentAsync();
        }

        _error = string.Empty;
        _controlError = string.Empty;

        if (RendererInfo.IsInteractive == false)
        {
            return Task.CompletedTask;
        }

        MarkMoment();

        return Task.WhenAll(UpdateChartsAsync(), UpdateMomentAsync());
    }

    /// <summary>
    /// Forgets the charts and everything that depends on them.
    /// </summary>
    private void ClearCharts()
    {
        _chartKey = null;
        _charts = null;
        _range = null;
        _memoryView = null;
        _cpuView = null;
        _marked = null;
        _failure = string.Empty;
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
            _chartRunner.CancelCurrent();
            ClearCharts();

            return UpdateMomentAsync();
        }

        if (_widthKnown == false || _chartKey?.SameChart(_address) == true)
        {
            return Task.CompletedTask;
        }

        _chartKey = _address.Copy();

        return ReadChartsAsync(_chartKey, now);
    }

    /// <summary>
    /// Reads the charts of an address, then the panel of the source they chose.
    /// </summary>
    /// <param name="address">The address</param>
    /// <param name="now">The time of the read</param>
    /// <returns>A task that completes when the reads have ended</returns>
    private async Task ReadChartsAsync(ProcessesAddress address, DateTimeOffset now)
    {
        var range = address.Range(now);
        var width = _width;

        await _chartRunner.RunAsync(async token =>
                                    {
                                        _failure = string.Empty;
                                        _sourceFailed = false;
                                        StateHasChanged();

                                        var charts = await StoreReads.ReadingAsync(ViewName, () => ChartReader.ReadAsync(address.Source, range, width, token)).ConfigureAwait(true);

                                        token.ThrowIfCancellationRequested();

                                        _charts = charts;
                                        _range = range;
                                        _marked = null;
                                        MarkMoment();
                                        StateHasChanged();
                                    }).ConfigureAwait(true);

        await UpdateMomentAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Gives the charts the moment of the address when it is not the one they mark.
    /// </summary>
    private void MarkMoment()
    {
        var at = _address.At;

        if (_charts is null || _range is null || (_marked == at && _memoryView is not null))
        {
            return;
        }

        _marked = at;
        _memoryView = _charts.Memory?.WithMoment(at, _range);
        _cpuView = _charts.Cpu?.WithMoment(at, _range);
    }

    /// <summary>
    /// Starts the read of the panel when the source, the moment or the second point changed, and shows no panel when there is nothing to read.
    /// </summary>
    /// <returns>A task that completes when the read has ended</returns>
    private Task UpdateMomentAsync()
    {
        if (_address.Error.Length == 0 && _future == false && _address.At is { } at && ShownSource is { Length: > 0 } source)
        {
            var key = (source, at, _address.Compare);

            if (key == _momentKey)
            {
                return Task.CompletedTask;
            }

            _momentKey = key;
            _momentFailure = string.Empty;

            return ReadMomentAsync(key);
        }

        _momentRunner.CancelCurrent();
        _momentKey = null;
        _momentFailure = string.Empty;
        _moment = null;
        _comparison = null;

        return Task.CompletedTask;
    }

    /// <summary>
    /// Reads the snapshot of a moment, or the comparison of two points.
    /// </summary>
    /// <param name="key">The source, the moment and the second point</param>
    /// <returns>A task that completes when the read has ended</returns>
    private Task ReadMomentAsync((string Source, DateTimeOffset At, DateTimeOffset? Compare) key)
    {
        return _momentRunner.RunAsync(async token =>
                                      {
                                          _moment = null;
                                          _comparison = null;
                                          StateHasChanged();

                                          if (key.Compare is { } second)
                                          {
                                              var comparison = await StoreReads.ReadingAsync(ViewName, () => MomentReader.CompareAsync(key.Source, key.At, second, token)).ConfigureAwait(true);

                                              token.ThrowIfCancellationRequested();

                                              _comparison = comparison;
                                          }
                                          else
                                          {
                                              var moment = await StoreReads.ReadingAsync(ViewName, () => MomentReader.MomentAsync(key.Source, key.At, token)).ConfigureAwait(true);

                                              token.ThrowIfCancellationRequested();

                                              _moment = moment;
                                          }

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
    /// Shows the text of a failed snapshot read.
    /// </summary>
    /// <param name="text">The text</param>
    private void FailMoment(string text)
    {
        _momentFailure = text;
    }

    /// <summary>
    /// Writes an address as the location.
    /// </summary>
    /// <param name="address">The address</param>
    /// <param name="replace">Whether the current entry of the history is replaced</param>
    private void Go(ProcessesAddress address, bool replace)
    {
        _controlError = PageLocation.Go(Navigation, () => ProcessesView.ProcessesHref(address), replace);
    }

    /// <summary>
    /// Gives a copy of the address for a change.
    /// </summary>
    /// <returns>The copy without error</returns>
    private ProcessesAddress Changed()
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
    /// Writes the source the user chose, without the moment and the second point, which belong to the former source; a value that is no listed source is ignored.
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
        address.Compare = null;

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
    /// Receives a click of a chart and writes it as the moment, or as the second point of a comparison when that was asked for, replacing the current entry of the history.
    /// </summary>
    /// <param name="pick">The click</param>
    /// <returns>A task that completes when the point is written</returns>
    private Task OnPickAsync(HistoryPick pick)
    {
        var at = HistoryView.Pick([], pick);
        var address = Changed();

        if (_armed && address.At is not null)
        {
            address.Compare = at;
            _armed = false;
        }
        else
        {
            address.At = at;
            address.Compare = null;
        }

        Go(address, true);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Moves the moment to the snapshot before it.
    /// </summary>
    /// <param name="before">The capture time of the snapshot</param>
    private void SnapshotBefore(DateTimeOffset before)
    {
        var address = Changed();

        address.At = HistoryView.RoundUpToSecond(before);
        address.Compare = null;

        Go(address, true);
    }

    /// <summary>
    /// Waits for the second point of a comparison.
    /// </summary>
    private void ArmCompare()
    {
        _armed = true;
    }

    /// <summary>
    /// Stops waiting for the second point of a comparison.
    /// </summary>
    private void CancelCompare()
    {
        _armed = false;
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
        _chartRunner = new PageRunner(Clock, Logger.ProcessesReadFailed, ProcessesView.ChartTimeoutText, ProcessesView.ChartFailureText, FailChart);
        _momentRunner = new PageRunner(Clock, Logger.ProcessesReadFailed, ProcessesView.ChartTimeoutText, ProcessesView.ChartFailureText, FailMoment);

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
        _momentRunner.Dispose();
    }

    #endregion // IDisposable
}