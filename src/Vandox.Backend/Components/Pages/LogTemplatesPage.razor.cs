using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;

using Vandox.Backend.Components.Charts;
using Vandox.Backend.Logs;
using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.Backend.Components.Pages;

/// <summary>
/// The page <c>/logs/templates</c> of the web UI.
/// </summary>
public sealed partial class LogTemplatesPage : ComponentBase, IDisposable
{
    #region Constants

    private const string PagePath = "logs/templates";

    private const string LinesPath = "logs";

    #endregion // Constants

    #region Fields

    private PageRunner _runner = null!;

    private LogsAddress _address = new();

    private string _source = string.Empty;

    private string _program = string.Empty;

    private string _from = string.Empty;

    private string _to = string.Empty;

    private string _error = string.Empty;

    private string _failure = string.Empty;

    private string? _handledQuery;

    private bool _searching = true;

    private IReadOnlyList<string>? _sources;

    private IReadOnlyList<TemplateRowView> _rows = [];

    private TemplateRowView? _selected;

    private bool _selectedUnknown;

    private ChartData? _chart;

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets the log explorer.
    /// </summary>
    [Inject]
    private ILogExplorer Explorer { get; set; } = null!;

    /// <summary>
    /// Gets or sets the navigation manager.
    /// </summary>
    [Inject]
    private NavigationManager Navigation { get; set; } = null!;

    /// <summary>
    /// Gets or sets the clock.
    /// </summary>
    [Inject]
    private TimeProvider Clock { get; set; } = null!;

    /// <summary>
    /// Gets or sets the logger.
    /// </summary>
    [Inject]
    private ILogger<LogTemplatesPage> Logger { get; set; } = null!;

    /// <summary>
    /// Gets the sources the source field offers.
    /// </summary>
    private IReadOnlyList<string> SourceOptions => LogsView.SourceOptions(_sources ?? [], _source);

    /// <summary>
    /// Gets the address of the search page for the selected template, with the times of this page.
    /// </summary>
    private string ShowLinesHref => LinesPath + QueryOf(new LogsAddress
                                                        {
                                                            TemplateId = _address.TemplateId,
                                                            From = _address.From,
                                                            To = _address.To
                                                        });

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Writes the query of an address, or nothing when the address is not valid.
    /// </summary>
    /// <param name="address">The address</param>
    /// <returns>The query string</returns>
    private static string QueryOf(LogsAddress address)
    {
        return address.Validate().Length == 0 ? address.ToQuery() : string.Empty;
    }

    /// <summary>
    /// Gets the address that selects a template of the list.
    /// </summary>
    /// <param name="id">The template ID</param>
    /// <returns>The address of this page</returns>
    private string SelectHref(long id)
    {
        return PagePath + QueryOf(new LogsAddress
                                  {
                                      Source = _address.Source,
                                      Program = _address.Program,
                                      From = _address.From,
                                      To = _address.To,
                                      TemplateId = id
                                  });
    }

    /// <summary>
    /// Reads the address and lists what it says. A page that is not interactive shows its filters only.
    /// </summary>
    /// <returns>A task that completes when the query has ended</returns>
    private async Task LoadAddressAsync()
    {
        var query = new Uri(Navigation.Uri).Query;
        var address = LogsAddress.Parse(query);

        _address = address;
        FillFields(address);

        if (address.Error.Length > 0)
        {
            _runner.CancelCurrent();
            _handledQuery = null;
            _error = address.Error;
            _searching = false;
            _failure = string.Empty;
            _rows = [];
            _selected = null;
            _selectedUnknown = false;
            _chart = null;

            return;
        }

        _error = string.Empty;

        if (RendererInfo.IsInteractive && query != _handledQuery)
        {
            _handledQuery = query;

            await ListAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Sets the fields of the form to the values of the address.
    /// </summary>
    /// <param name="address">The address</param>
    private void FillFields(LogsAddress address)
    {
        _source = address.Source;
        _program = address.Program;
        _from = address.From is null ? string.Empty : LogsAddress.FormatTime(address.From.Value);
        _to = address.To is null ? string.Empty : LogsAddress.FormatTime(address.To.Value);
    }

    /// <summary>
    /// Lists the templates of the address and, with a template selected, reads its hourly counts.
    /// </summary>
    /// <returns>A task that completes when the queries have ended</returns>
    private Task ListAsync()
    {
        var address = _address;

        return _runner.RunAsync(async token =>
                                {
                                    _searching = true;
                                    _failure = string.Empty;
                                    _rows = [];
                                    _selected = null;
                                    _selectedUnknown = false;
                                    _chart = null;
                                    StateHasChanged();

                                    if (_sources is null)
                                    {
                                        var sources = await Explorer.LogSourcesAsync(token).ConfigureAwait(true);

                                        token.ThrowIfCancellationRequested();

                                        _sources = sources;
                                    }

                                    var query = new TemplateListQuery
                                                {
                                                    Source = address.Source,
                                                    Program = address.Program,
                                                    From = address.From ?? StorableTime.Min,
                                                    To = address.To ?? StorableTime.Max,
                                                    Limit = LogsView.TemplateRows
                                                };
                                    var rows = await Explorer.TemplatesAsync(query, token).ConfigureAwait(true);

                                    token.ThrowIfCancellationRequested();

                                    _rows = rows.Select(row => TemplateRowView.From(row.Template, row.CountInRange)).ToList();

                                    if (address.TemplateId > 0)
                                    {
                                        await SelectAsync(address, token).ConfigureAwait(true);
                                    }

                                    _searching = false;
                                });
    }

    /// <summary>
    /// Reads the selected template and the hourly counts of the last hours of the range.
    /// </summary>
    /// <param name="address">The address</param>
    /// <param name="token">Ends the calls</param>
    /// <returns>A task that completes when the calls have ended</returns>
    private async Task SelectAsync(LogsAddress address, CancellationToken token)
    {
        var template = await Explorer.TemplateAsync(address.TemplateId, token).ConfigureAwait(true);

        token.ThrowIfCancellationRequested();

        if (template is null)
        {
            _selectedUnknown = true;

            return;
        }

        _selected = TemplateRowView.From(template, 0);

        var now = Clock.GetUtcNow().UtcDateTime;
        var to = address.To ?? new DateTimeOffset(now.Date.AddHours(now.Hour + 1), TimeSpan.Zero);
        var from = address.From is null || address.From.Value < to.AddHours(-TemplateHourSeries.MaxHours) ? to.AddHours(-TemplateHourSeries.MaxHours) : address.From.Value;

        if (from >= to)
        {
            return;
        }

        var hourQuery = new TemplateHourQuery
                        {
                            TemplateId = address.TemplateId,
                            From = from,
                            To = to,
                            Limit = TemplateHourSeries.MaxHours + 1
                        };
        var hours = await Explorer.TemplateHoursAsync(hourQuery, token).ConfigureAwait(true);

        token.ThrowIfCancellationRequested();

        var sourceQuery = new SourceHourQuery
                          {
                              Source = template.Source,
                              From = from,
                              To = to,
                              Limit = TemplateHourSeries.MaxHours + 1
                          };
        var covered = await Explorer.SourceHoursAsync(sourceQuery, token).ConfigureAwait(true);

        token.ThrowIfCancellationRequested();

        _chart = TemplateHourSeries.Build(hours, covered, from, to);
    }

    /// <summary>
    /// Shows a text instead of the result.
    /// </summary>
    /// <param name="text">The text</param>
    private void Fail(string text)
    {
        _failure = text;
        _searching = false;
    }

    /// <summary>
    /// Validates the entered values and navigates to the address they make.
    /// </summary>
    private void Submit()
    {
        var address = LogsAddress.FromFields(string.Empty, _source, _program, string.Empty, _from, _to);

        if (address.Error.Length > 0)
        {
            _error = address.Error;

            return;
        }

        _error = string.Empty;
        _handledQuery = null;
        Navigation.NavigateTo(PagePath + address.ToQuery());
    }

    /// <summary>
    /// Tells whether an address is the one of this page.
    /// </summary>
    /// <param name="location">The absolute address</param>
    /// <returns><c>true</c> when it is the page <c>/logs/templates</c>, whatever its query</returns>
    private bool IsThisPage(string location)
    {
        var relative = Navigation.ToBaseRelativePath(location);
        var end = relative.IndexOfAny(['?', '#']);

        return string.Equals(end < 0 ? relative : relative[..end], PagePath, StringComparison.Ordinal);
    }

    /// <summary>
    /// Lists what the new address says, unless the page was left.
    /// </summary>
    /// <param name="sender">The navigation manager</param>
    /// <param name="arguments">The new address</param>
    private void OnLocationChanged(object? sender, LocationChangedEventArgs arguments)
    {
        if (IsThisPage(arguments.Location))
        {
            _ = InvokeAsync(HandleLocationAsync);
        }
    }

    /// <summary>
    /// Lists the address and shows the result.
    /// </summary>
    /// <returns>A task that completes when the query has ended</returns>
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
        _runner = new PageRunner(Clock, Logger, LogsView.QueryDeadline, LogsView.TemplatesReadFailed, Fail);

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
        _runner.Dispose();
    }

    #endregion // IDisposable
}