using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;

using Vandox.Backend.Logs;
using Vandox.Storage;

namespace Vandox.Backend.Components.Pages;

/// <summary>
/// The page <c>/logs</c> of the web UI.
/// </summary>
public sealed partial class LogsPage : ComponentBase, IDisposable
{
    #region Constants

    private const string PagePath = "logs";

    private const string TemplatesPath = "logs/templates";

    #endregion // Constants

    #region Fields

    private PageRunner _runner = null!;

    private LogsAddress _address = new();

    private string _text = string.Empty;

    private string _source = string.Empty;

    private string _program = string.Empty;

    private string _severity = string.Empty;

    private string _from = string.Empty;

    private string _to = string.Empty;

    private string _error = string.Empty;

    private string _failure = string.Empty;

    private string? _handledQuery;

    private bool _searching = true;

    private bool _empty;

    private IReadOnlyList<string>? _sources;

    private LogTemplate? _template;

    private bool _templateUnknown;

    private IReadOnlyList<TextRun> _templateText = [];

    private IReadOnlyList<TextRun> _templateSource = [];

    private IReadOnlyList<TextRun> _templateProgram = [];

    private IReadOnlyList<LogLineView> _lines = [];

    private LogPosition? _position;

    private LogPosition? _nextPage;

    private LogPosition? _continueFrom;

    private bool _stepwise;

    private long _contextId;

    private IReadOnlyList<LogLineView> _contextBefore = [];

    private IReadOnlyList<LogLineView> _contextAfter = [];

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
    private ILogger<LogsPage> Logger { get; set; } = null!;

    /// <summary>
    /// Gets the sources the source field offers.
    /// </summary>
    private IReadOnlyList<string> SourceOptions => LogsView.SourceOptions(_sources ?? [], _source);

    /// <summary>
    /// Gets the address of the templates page with the filters of this page that it understands.
    /// </summary>
    private string TemplatesHref => TemplatesPath + QueryOf(new LogsAddress
                                                            {
                                                                Source = _address.Source,
                                                                Program = _address.Program,
                                                                From = _address.From,
                                                                To = _address.To
                                                            });

    /// <summary>
    /// Gets the address of this page without the template.
    /// </summary>
    private string WithoutTemplateHref => PagePath + QueryOf(new LogsAddress
                                                             {
                                                                 Text = _address.Text,
                                                                 Source = _address.Source,
                                                                 Program = _address.Program,
                                                                 Severity = _address.Severity,
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
    /// Gets the style class of a severity.
    /// </summary>
    /// <param name="severity">The severity name</param>
    /// <returns>The class</returns>
    private static string SeverityClass(string severity)
    {
        return severity switch
               {
                   "emerg" or "alert" or "crit" or "err" => "severe",
                   "warning" => "warning",
                   _ => string.Empty
               };
    }

    /// <summary>
    /// Reads the address and searches what it says. A page that is not interactive shows its filters only.
    /// </summary>
    /// <returns>A task that completes when the search has ended</returns>
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
            _empty = false;
            _failure = string.Empty;
            _lines = [];
            _template = null;
            _templateUnknown = false;
            _nextPage = null;
            _continueFrom = null;
            _stepwise = false;

            return;
        }

        _error = string.Empty;

        if (RendererInfo.IsInteractive && query != _handledQuery)
        {
            _handledQuery = query;

            await SearchAsync(null, true).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Sets the fields of the form to the values of the address.
    /// </summary>
    /// <param name="address">The address</param>
    private void FillFields(LogsAddress address)
    {
        _text = address.Text;
        _source = address.Source;
        _program = address.Program;
        _severity = address.Severity is null ? string.Empty : Severities.Name(address.Severity.Value);
        _from = address.From is null ? string.Empty : LogsAddress.FormatTime(address.From.Value);
        _to = address.To is null ? string.Empty : LogsAddress.FormatTime(address.To.Value);
    }

    /// <summary>
    /// Searches one page of lines.
    /// </summary>
    /// <param name="after">The position the page starts after, or <c>null</c> for the first page</param>
    /// <param name="reload">Whether to read the sources and the template again: the address is new</param>
    /// <returns>A task that completes when the search has ended</returns>
    private Task SearchAsync(LogPosition? after, bool reload)
    {
        var address = _address;

        return _runner.RunAsync(async token =>
                                {
                                    _searching = true;
                                    _empty = false;
                                    _failure = string.Empty;
                                    _lines = [];
                                    _nextPage = null;
                                    _continueFrom = null;
                                    _stepwise = false;
                                    _contextId = 0;
                                    StateHasChanged();

                                    if (reload)
                                    {
                                        await LoadHeaderAsync(address, token).ConfigureAwait(true);
                                    }

                                    if (_templateUnknown)
                                    {
                                        return;
                                    }

                                    var page = await Explorer.FindLogsAsync(address.ToFilter(LogsView.PageLines, LogsView.ScanRows, after), token).ConfigureAwait(true);

                                    token.ThrowIfCancellationRequested();
                                    Apply(page, after);
                                });
    }

    /// <summary>
    /// Reads the sources once and the template of the address.
    /// </summary>
    /// <param name="address">The address</param>
    /// <param name="token">Ends the calls</param>
    /// <returns>A task that completes when both are read</returns>
    private async Task LoadHeaderAsync(LogsAddress address, CancellationToken token)
    {
        if (_sources is null)
        {
            var sources = await Explorer.LogSourcesAsync(token).ConfigureAwait(true);

            token.ThrowIfCancellationRequested();

            _sources = sources;
        }

        await LoadTemplateAsync(address, token).ConfigureAwait(true);
    }

    /// <summary>
    /// Reads the template of the address.
    /// </summary>
    /// <param name="address">The address</param>
    /// <param name="token">Ends the call</param>
    /// <returns>A task that completes when the template is read</returns>
    private async Task LoadTemplateAsync(LogsAddress address, CancellationToken token)
    {
        _template = null;
        _templateUnknown = false;

        if (address.TemplateId <= 0)
        {
            return;
        }

        var template = await Explorer.TemplateAsync(address.TemplateId, token).ConfigureAwait(true);

        token.ThrowIfCancellationRequested();

        if (template is null)
        {
            _templateUnknown = true;
            _searching = false;

            return;
        }

        _template = template;
        _templateText = LogText.Runs(template.Template, LogText.MaxShortChars);
        _templateSource = LogText.Runs(template.Source, LogText.MaxShortChars);
        _templateProgram = LogText.Runs(template.Program, LogText.MaxShortChars);
    }

    /// <summary>
    /// Shows a page of lines.
    /// </summary>
    /// <param name="page">The page</param>
    /// <param name="after">The position the page started after</param>
    private void Apply(LogPage page, LogPosition? after)
    {
        _lines = page.Lines.Select(LogLineView.From).ToList();
        _position = after;
        _searching = false;
        _empty = page.Lines.Count == 0 && page.Next is null;
        _stepwise = page.IndexCheckStopped && page.ScanLimitReached;

        if (page.ScanLimitReached)
        {
            _nextPage = null;
            _continueFrom = page.Next;
        }
        else
        {
            _nextPage = page.Next;
            _continueFrom = null;
        }
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
        var address = LogsAddress.FromFields(_text, _source, _program, _severity, _from, _to);

        if (address.Error.Length > 0)
        {
            _error = address.Error;

            return;
        }

        address.TemplateId = _address.TemplateId;
        _error = string.Empty;
        _handledQuery = null;
        Navigation.NavigateTo(PagePath + address.ToQuery());
    }

    /// <summary>
    /// Shows the page after the current one.
    /// </summary>
    /// <returns>A task that completes when the search has ended</returns>
    private Task NextPageAsync()
    {
        return SearchAsync(_nextPage, false);
    }

    /// <summary>
    /// Continues a search that the scan bound ended.
    /// </summary>
    /// <returns>A task that completes when the search has ended</returns>
    private Task ContinueAsync()
    {
        return SearchAsync(_continueFrom, false);
    }

    /// <summary>
    /// Shows the first page again.
    /// </summary>
    /// <returns>A task that completes when the search has ended</returns>
    private Task FirstPageAsync()
    {
        return SearchAsync(null, false);
    }

    /// <summary>
    /// Shows the lines around a line, or hides them when they are shown.
    /// </summary>
    /// <param name="id">The record ID of the line</param>
    /// <returns>A task that completes when the lines are read</returns>
    private Task ToggleContextAsync(long id)
    {
        if (_contextId == id)
        {
            _contextId = 0;

            return Task.CompletedTask;
        }

        return _runner.RunAsync(async token =>
                                {
                                    _failure = string.Empty;

                                    var query = new LogContextQuery
                                                {
                                                    RecordId = id,
                                                    Before = LogsView.ContextLines,
                                                    After = LogsView.ContextLines
                                                };
                                    var context = await Explorer.LogContextAsync(query, token).ConfigureAwait(true);

                                    token.ThrowIfCancellationRequested();

                                    _contextBefore = context.Before.Select(LogLineView.From).ToList();
                                    _contextAfter = context.After.Select(LogLineView.From).ToList();
                                    _contextId = id;
                                });
    }

    /// <summary>
    /// Tells whether an address is the one of this page.
    /// </summary>
    /// <param name="location">The absolute address</param>
    /// <returns><c>true</c> when it is the page <c>/logs</c>, whatever its query</returns>
    private bool IsThisPage(string location)
    {
        var relative = Navigation.ToBaseRelativePath(location);
        var end = relative.IndexOfAny(['?', '#']);

        return string.Equals(end < 0 ? relative : relative[..end], PagePath, StringComparison.Ordinal);
    }

    /// <summary>
    /// Searches what the new address says, unless the page was left.
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
    /// Searches the address and shows the result.
    /// </summary>
    /// <returns>A task that completes when the search has ended</returns>
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
        _runner = new PageRunner(Clock, Logger, LogsView.SearchDeadline, LogsView.LogsReadFailed, Fail);

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