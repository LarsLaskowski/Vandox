using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;

using Vandox.Backend.Hosting;
using Vandox.Backend.Logs;
using Vandox.Backend.Reports;

namespace Vandox.Backend.Components.Pages;

/// <summary>
/// The page <c>/report</c> of the web UI.
/// </summary>
public sealed partial class ReportPage : ComponentBase, IDisposable
{
    #region Constants

    private const string PagePath = "report";

    #endregion // Constants

    #region Fields

    private PageRunner _runner = null!;

    private ReportAddress _address = ReportAddress.Parse(string.Empty);

    private ReportDocument? _document;

    private string _failure = string.Empty;

    private bool _running = true;

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets the report service.
    /// </summary>
    [Inject]
    private ReportService Reports { get; set; } = null!;

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
    private ILogger<ReportPage> Logger { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Gets the style class of the link to a language.
    /// </summary>
    /// <param name="language">The language of the link</param>
    /// <returns>The class, or <c>null</c> so that no attribute is written</returns>
    private string? LanguageClass(string language)
    {
        return _address.Error.Length == 0 && _address.Language == language ? "active" : null;
    }

    /// <summary>
    /// Reads the address and, when it is valid and the page is interactive, makes the report from the first outage.
    /// </summary>
    /// <returns>A task that completes when the run has ended</returns>
    private async Task LoadAddressAsync()
    {
        _address = ReportAddress.Parse(new Uri(Navigation.Uri).Query);

        if (_address.Error.Length > 0)
        {
            _runner.CancelCurrent();
            _document = null;
            _failure = string.Empty;
            _running = false;

            return;
        }

        if (RendererInfo.IsInteractive)
        {
            await RunAsync(_address.Language, 0).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Makes the report and keeps the outages of one page.
    /// </summary>
    /// <param name="language">The language of the report</param>
    /// <param name="firstOutage">The zero-based index of the first outage to show</param>
    /// <returns>A task that completes when the run has ended</returns>
    private Task RunAsync(string language, long firstOutage)
    {
        return _runner.RunAsync(async token =>
                                {
                                    _running = true;
                                    _failure = string.Empty;
                                    _document = null;
                                    StateHasChanged();

                                    var text = ReportText.For(language) ?? throw new InvalidOperationException("the language has no report text");
                                    var builder = new ReportViewBuilder(text, firstOutage, ReportsView.PageOutages);

                                    await Reports.RunAsync(builder, token).ConfigureAwait(true);

                                    token.ThrowIfCancellationRequested();

                                    _document = builder.Document;
                                    _running = false;
                                });
    }

    /// <summary>
    /// Shows the next outages.
    /// </summary>
    /// <returns>A task that completes when the run has ended</returns>
    private Task NextAsync()
    {
        return _document is { } document ? RunAsync(_address.Language, document.FirstOutage + ReportsView.PageOutages) : Task.CompletedTask;
    }

    /// <summary>
    /// Shows the first outages again.
    /// </summary>
    /// <returns>A task that completes when the run has ended</returns>
    private Task FirstAsync()
    {
        return RunAsync(_address.Language, 0);
    }

    /// <summary>
    /// Shows a text instead of the report.
    /// </summary>
    /// <param name="text">The text</param>
    private void Fail(string text)
    {
        _failure = text;
        _running = false;
    }

    /// <summary>
    /// Tells whether an address is the one of this page.
    /// </summary>
    /// <param name="location">The absolute address</param>
    /// <returns><c>true</c> when it is the page <c>/report</c>, whatever its query</returns>
    private bool IsThisPage(string location)
    {
        var relative = Navigation.ToBaseRelativePath(location);
        var end = relative.IndexOfAny(['?', '#']);

        return string.Equals(end < 0 ? relative : relative[..end], PagePath, StringComparison.Ordinal);
    }

    /// <summary>
    /// Makes the report of the new address, unless the page was left.
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
    /// Reads the address, makes the report and shows it.
    /// </summary>
    /// <returns>A task that completes when the run has ended</returns>
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
        _runner = new PageRunner(Clock, Logger.ReportPageFailed, ReportsView.Deadline, ReportsView.RunDeadline, ReportsView.RunFailure, Fail);

        if (RendererInfo.IsInteractive)
        {
            Navigation.LocationChanged += OnLocationChanged;
        }
    }

    /// <inheritdoc />
    protected override Task OnInitializedAsync()
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
        GC.SuppressFinalize(this);
    }

    #endregion // IDisposable
}