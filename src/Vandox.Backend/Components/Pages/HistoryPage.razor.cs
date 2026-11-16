using System.Globalization;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;

using Vandox.Backend.Components.Charts;
using Vandox.Backend.History;
using Vandox.Backend.Hosting;
using Vandox.Backend.Logs;
using Vandox.Storage;

namespace Vandox.Backend.Components.Pages;

/// <summary>
/// The page <c>/history</c> of the web UI: chosen metrics over a chosen range, and the snapshots of a moment.
/// </summary>
public sealed partial class HistoryPage : ComponentBase, IDisposable
{
    #region Constants

    private const string PagePath = "history";

    private const string ApplyText = "Apply";

    private const string AddText = "Add";

    #endregion // Constants

    #region Fields

    /// <summary>
    /// The presets in the order of the buttons.
    /// </summary>
    private static readonly HistoryPreset[] _presets = [HistoryPreset.OneHour, HistoryPreset.OneDay, HistoryPreset.SevenDays, HistoryPreset.ThirtyDays, HistoryPreset.OneYear];

    /// <summary>
    /// The runs of the chart reads and of the metric list read.
    /// </summary>
    private PageRunner _chartRunner = null!;

    /// <summary>
    /// The runs of the snapshot reads.
    /// </summary>
    private PageRunner _momentRunner = null!;

    /// <summary>
    /// The run of the metric list read.
    /// </summary>
    private PageRunner _listRunner = null!;

    /// <summary>
    /// The address read from the location.
    /// </summary>
    private HistoryAddress _address = new();

    /// <summary>
    /// The address the chart was read for, or <see langword="null"/> when none was.
    /// </summary>
    private HistoryAddress? _chartKey;

    /// <summary>
    /// The text of the start field.
    /// </summary>
    private string _from = string.Empty;

    /// <summary>
    /// The text of the end field.
    /// </summary>
    private string _to = string.Empty;

    /// <summary>
    /// The text of the metric choice, the index into the options.
    /// </summary>
    private string _choice = "0";

    /// <summary>
    /// The text of an error of the address or of a control.
    /// </summary>
    private string _error = string.Empty;

    /// <summary>
    /// The text of a failed chart or metric list read.
    /// </summary>
    private string _failure = string.Empty;

    /// <summary>
    /// The text of a failed snapshot read.
    /// </summary>
    private string _momentFailure = string.Empty;

    /// <summary>
    /// The metrics the address may name, from the stored list.
    /// </summary>
    private IReadOnlyList<SeriesKey> _options = [];

    /// <summary>
    /// Whether the stored list was cut.
    /// </summary>
    private bool _listCut;

    /// <summary>
    /// Whether the metric list was requested.
    /// </summary>
    private bool _listRequested;

    /// <summary>
    /// The width of the chart in CSS pixels, once known.
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
    /// The result of the last chart read.
    /// </summary>
    private TimeSeriesResult? _result;

    /// <summary>
    /// The range of the last chart read.
    /// </summary>
    private HistoryRange? _range;

    /// <summary>
    /// The view shown, with the moment of the address.
    /// </summary>
    private HistoryChartView? _view;

    /// <summary>
    /// The moment the snapshots were requested for.
    /// </summary>
    private DateTimeOffset? _momentAt;

    /// <summary>
    /// The snapshots of the moment.
    /// </summary>
    private HistoryMoment? _moment;

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets the presets in the order of the buttons.
    /// </summary>
    private static IReadOnlyList<HistoryPreset> Presets => _presets;

    /// <summary>
    /// Gets or sets the time-series service.
    /// </summary>
    [Inject]
    private TimeSeriesService TimeSeries { get; set; } = null!;

    /// <summary>
    /// Gets or sets the history reader.
    /// </summary>
    [Inject]
    private IHistoryReader Reader { get; set; } = null!;

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
    private ILogger<HistoryPage> Logger { get; set; } = null!;

    /// <summary>
    /// Gets the parameters of the chart: the view and the callbacks of the width, the selection and the click.
    /// </summary>
    private Dictionary<string, object?> ChartParameters => new()
                                                           {
                                                               [nameof(HistoryChart.View)] = _view,
                                                               [nameof(HistoryChart.OnWidth)] = EventCallback.Factory.Create<double>(this, OnWidthAsync),
                                                               [nameof(HistoryChart.OnSelect)] = EventCallback.Factory.Create<HistoryRange>(this, OnSelectAsync),
                                                               [nameof(HistoryChart.OnPick)] = EventCallback.Factory.Create<HistoryPick>(this, OnPickAsync)
                                                           };

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Runs a read; a refusal of the store is a failure of the view, not a text for the user.
    /// </summary>
    /// <typeparam name="TResult">The type of the result</typeparam>
    /// <param name="read">The read</param>
    /// <returns>A task that returns the result</returns>
    private static async Task<TResult> ReadingAsync<TResult>(Func<Task<TResult>> read)
    {
        try
        {
            return await read().ConfigureAwait(true);
        }
        catch (InvalidQueryException exception)
        {
            throw new InvalidOperationException("history view: a read was refused", exception);
        }
    }

    /// <summary>
    /// Gives the class of the button of a preset.
    /// </summary>
    /// <param name="preset">The preset</param>
    /// <returns>The class, <see langword="null"/> for none</returns>
    private string? PresetClass(HistoryPreset preset)
    {
        var current = _address.Preset ?? (_address.From is null && _address.To is null ? HistoryPreset.OneDay : (HistoryPreset?)null);

        return current == preset ? "active" : null;
    }

    /// <summary>
    /// Reads the address of the location and starts the reads it calls for.
    /// </summary>
    /// <returns>A task that completes when the reads have ended</returns>
    private Task LoadAddressAsync()
    {
        var address = HistoryAddress.Parse(new Uri(Navigation.Uri).Query);

        _address = address;
        _from = address.From is null ? string.Empty : LogsAddress.FormatTime(address.From.Value);
        _to = address.To is null ? string.Empty : LogsAddress.FormatTime(address.To.Value);

        if (address.Error.Length > 0)
        {
            _chartRunner.CancelCurrent();
            _momentRunner.CancelCurrent();
            _error = address.Error;
            _failure = string.Empty;
            _momentFailure = string.Empty;
            _chartKey = null;
            _momentAt = null;
            _result = null;
            _view = null;
            _moment = null;
            _future = false;

            return Task.CompletedTask;
        }

        _error = string.Empty;

        return RendererInfo.IsInteractive ? Task.WhenAll(ReadListAsync(), UpdateMomentAsync(), UpdateChartAsync()) : Task.CompletedTask;
    }

    /// <summary>
    /// Reads the list of the stored metrics once.
    /// </summary>
    /// <returns>A task that completes when the list is read</returns>
    private Task ReadListAsync()
    {
        if (_listRequested)
        {
            return Task.CompletedTask;
        }

        _listRequested = true;

        return _listRunner.RunAsync(async token =>
                                    {
                                        var list = await ReadingAsync(() => Reader.StoredMetricsAsync(StorageLimits.MaxStoredMetrics, token)).ConfigureAwait(true);

                                        token.ThrowIfCancellationRequested();

                                        _options = HistoryView.MetricOptions(list);
                                        _listCut = list.More;
                                    });
    }

    /// <summary>
    /// Starts the read of the chart when the address calls for another one and the width is known.
    /// </summary>
    /// <returns>A task that completes when the read has ended</returns>
    private Task UpdateChartAsync()
    {
        var now = Clock.GetUtcNow();

        if (_address.Error.Length > 0 || _address.Metrics.Count == 0)
        {
            _chartRunner.CancelCurrent();
            _chartKey = null;
            _result = null;
            _view = null;
            _future = false;

            return Task.CompletedTask;
        }

        _future = _address.Range(now).Start >= now;

        if (_future)
        {
            _chartRunner.CancelCurrent();
            _chartKey = null;
            _result = null;
            _view = null;

            return Task.CompletedTask;
        }

        if (_widthKnown == false || _chartKey?.SameChart(_address) == true)
        {
            return Task.CompletedTask;
        }

        _chartKey = _address.Copy();

        return ReadChartAsync(_chartKey, now);
    }

    /// <summary>
    /// Reads the chart of an address.
    /// </summary>
    /// <param name="address">The address</param>
    /// <param name="now">The time of the read</param>
    /// <returns>A task that completes when the read has ended</returns>
    private Task ReadChartAsync(HistoryAddress address, DateTimeOffset now)
    {
        var range = address.Range(now);
        var query = new TimeSeriesQuery
                    {
                        Series = address.Metrics,
                        From = range.Start,
                        To = range.End,
                        Target = HistoryView.Target(_width)
                    };

        return _chartRunner.RunAsync(async token =>
                                     {
                                         _failure = string.Empty;
                                         StateHasChanged();

                                         var result = await ReadingAsync(() => TimeSeries.QueryAsync(query, token)).ConfigureAwait(true);

                                         token.ThrowIfCancellationRequested();

                                         _result = result;
                                         _range = range;
                                         _view = HistoryChartView.From(result, address.Metrics, range, _address.At);
                                     });
    }

    /// <summary>
    /// Starts the read of the snapshots when the moment of the address changed, and moves the mark in the chart.
    /// </summary>
    /// <returns>A task that completes when the read has ended</returns>
    private Task UpdateMomentAsync()
    {
        var at = _address.At;

        if (at == _momentAt)
        {
            return Task.CompletedTask;
        }

        _momentAt = at;
        _momentFailure = string.Empty;

        if (_view is not null && _range is not null)
        {
            _view = _view.WithMoment(at, _range);
        }

        if (at is null)
        {
            _momentRunner.CancelCurrent();
            _moment = null;

            return Task.CompletedTask;
        }

        return ReadMomentAsync(at.Value);
    }

    /// <summary>
    /// Reads the snapshots of a moment.
    /// </summary>
    /// <param name="at">The moment</param>
    /// <returns>A task that completes when the read has ended</returns>
    private Task ReadMomentAsync(DateTimeOffset at)
    {
        return _momentRunner.RunAsync(async token =>
                                      {
                                          _moment = null;
                                          StateHasChanged();

                                          var moment = await ReadingAsync(() => new HistoryMomentReader(Reader).ReadAsync(at, token)).ConfigureAwait(true);

                                          token.ThrowIfCancellationRequested();

                                          _moment = moment;
                                      });
    }

    /// <summary>
    /// Shows the text of a failed chart or metric list read.
    /// </summary>
    /// <param name="text">The text</param>
    private void FailChart(string text)
    {
        _failure = text;
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
    private void Go(HistoryAddress address, bool replace)
    {
        string href;

        try
        {
            href = HistoryView.HistoryHref(address);
        }
        catch (InvalidOperationException exception)
        {
            _error = exception.Message;

            return;
        }

        _error = string.Empty;
        Navigation.NavigateTo(href,
                              new NavigationOptions
                              {
                                  ReplaceHistoryEntry = replace
                              });
    }

    /// <summary>
    /// Gives a copy of the address for a change.
    /// </summary>
    /// <returns>The copy without error</returns>
    private HistoryAddress Changed()
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
        var parsed = HistoryAddress.Parse($"?from={Uri.EscapeDataString(_from)}&to={Uri.EscapeDataString(_to)}");

        if (parsed.Error.Length > 0)
        {
            _error = parsed.Error;

            return;
        }

        var address = Changed();

        address.Preset = null;
        address.From = parsed.From;
        address.To = parsed.To;

        Go(address, false);
    }

    /// <summary>
    /// Adds the chosen metric of the list.
    /// </summary>
    private void AddMetric()
    {
        if (int.TryParse(_choice, NumberStyles.None, CultureInfo.InvariantCulture, out var index) == false || index >= _options.Count)
        {
            return;
        }

        var metric = _options[index];

        if (_address.Metrics.Any(known => string.Equals(known.Source, metric.Source, StringComparison.Ordinal) && string.Equals(known.Name, metric.Name, StringComparison.Ordinal)))
        {
            return;
        }

        if (_address.Metrics.Count >= HistoryView.MaxMetrics)
        {
            _error = HistoryView.TooManyMetricsText;

            return;
        }

        var address = Changed();

        address.Metrics = [.. address.Metrics, metric];

        Go(address, false);
    }

    /// <summary>
    /// Removes a metric.
    /// </summary>
    /// <param name="metric">The metric</param>
    private void RemoveMetric(SeriesKey metric)
    {
        var address = Changed();

        address.Metrics = [.. address.Metrics.Where(known => ReferenceEquals(known, metric) == false)];

        Go(address, false);
    }

    /// <summary>
    /// Receives the width of the chart and reads it.
    /// </summary>
    /// <param name="width">The width in CSS pixels</param>
    /// <returns>A task that completes when the read has ended</returns>
    private Task OnWidthAsync(double width)
    {
        _width = width;
        _widthKnown = true;

        return _address.Error.Length > 0 ? Task.CompletedTask : UpdateChartAsync();
    }

    /// <summary>
    /// Receives a selection of the chart and writes it as a fixed range.
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
    /// Receives a click of the chart and writes the moment.
    /// </summary>
    /// <param name="pick">The click</param>
    /// <returns>A task that completes when the moment is written</returns>
    private Task OnPickAsync(HistoryPick pick)
    {
        SetMoment(HistoryView.Pick(_result?.Markers ?? [], pick));

        return Task.CompletedTask;
    }

    /// <summary>
    /// Writes a moment as the location, replacing the current entry of the history.
    /// </summary>
    /// <param name="at">The moment, floored to the second</param>
    private void SetMoment(DateTimeOffset at)
    {
        var address = Changed();

        address.At = HistoryView.Pick([], new HistoryPick(at, TimeSpan.Zero));

        Go(address, true);
    }

    /// <summary>
    /// Moves the moment to the snapshot before it.
    /// </summary>
    /// <param name="before">The capture time of the snapshot</param>
    private void SnapshotBefore(DateTimeOffset before)
    {
        var address = Changed();

        address.At = HistoryView.RoundUpToSecond(before);

        Go(address, true);
    }

    /// <summary>
    /// Tells whether a location is this page.
    /// </summary>
    /// <param name="location">The location</param>
    /// <returns><c>true</c> when the location is the page of the history view</returns>
    private bool IsThisPage(string location)
    {
        var relative = Navigation.ToBaseRelativePath(location);
        var end = relative.IndexOfAny(['?', '#']);

        return string.Equals(end < 0 ? relative : relative[..end], PagePath, StringComparison.Ordinal);
    }

    /// <summary>
    /// Handles a change of the location.
    /// </summary>
    /// <param name="sender">The navigation manager</param>
    /// <param name="arguments">The new location</param>
    private void OnLocationChanged(object? sender, LocationChangedEventArgs arguments)
    {
        if (IsThisPage(arguments.Location))
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
        _chartRunner = new PageRunner(Clock, Logger.HistoryReadFailed, HistoryView.ChartTimeoutText, HistoryView.ChartFailureText, FailChart);
        _listRunner = new PageRunner(Clock, Logger.HistoryReadFailed, HistoryView.ChartTimeoutText, HistoryView.ChartFailureText, FailChart);
        _momentRunner = new PageRunner(Clock, Logger.HistoryReadFailed, HistoryView.MomentTimeoutText, HistoryView.MomentFailureText, FailMoment);

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
        _listRunner.Dispose();
        _momentRunner.Dispose();
    }

    #endregion // IDisposable
}