using Microsoft.AspNetCore.Components;

using Vandox.Backend.Hosting;
using Vandox.Backend.Incidents;
using Vandox.Backend.Logs;
using Vandox.Storage;

namespace Vandox.Backend.Components.Pages;

/// <summary>
/// The page <c>/incidents</c> of the web UI.
/// </summary>
public sealed partial class IncidentsPage : ComponentBase, IDisposable
{
    #region Fields

    private PageRunner _runner = null!;

    private IReadOnlyList<IncidentRowView> _rows = [];

    private IncidentSummary? _last;

    private IncidentPosition? _before;

    private string _failure = string.Empty;

    private bool _loading = true;

    private bool _empty;

    private bool _noOlder;

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets the incident browser.
    /// </summary>
    [Inject]
    private IIncidentBrowser Browser { get; set; } = null!;

    /// <summary>
    /// Gets or sets the clock.
    /// </summary>
    [Inject]
    private TimeProvider Clock { get; set; } = null!;

    /// <summary>
    /// Gets or sets the logger.
    /// </summary>
    [Inject]
    private ILogger<IncidentsPage> Logger { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Reads one page of incidents.
    /// </summary>
    /// <param name="before">The position the page continues after, or <c>null</c> for the newest incidents</param>
    /// <returns>A task that completes when the read has ended</returns>
    private Task LoadAsync(IncidentPosition? before)
    {
        return _runner.RunAsync(async token =>
                                {
                                    _loading = true;
                                    _failure = string.Empty;
                                    _empty = false;
                                    _noOlder = false;
                                    StateHasChanged();

                                    var summaries = await Browser.IncidentSummariesAsync(new IncidentListQuery
                                                                                         {
                                                                                             Before = before,
                                                                                             Limit = IncidentsView.PageIncidents
                                                                                         },
                                                                                         token)
                                                                 .ConfigureAwait(true);

                                    token.ThrowIfCancellationRequested();

                                    _rows = summaries.Select(IncidentRowView.From).ToList();
                                    _last = summaries.Count > 0 ? summaries[^1] : null;
                                    _before = before;
                                    _empty = summaries.Count == 0 && before is null;
                                    _noOlder = summaries.Count == 0 && before is not null;
                                    _loading = false;
                                });
    }

    /// <summary>
    /// Shows the page after the current one.
    /// </summary>
    /// <returns>A task that completes when the read has ended</returns>
    private Task OlderAsync()
    {
        var last = _last;

        return last is null
                   ? Task.CompletedTask
                   : LoadAsync(new IncidentPosition
                               {
                                   StartedAt = last.Start,
                                   Id = last.Id
                               });
    }

    /// <summary>
    /// Shows the newest incidents again.
    /// </summary>
    /// <returns>A task that completes when the read has ended</returns>
    private Task NewestAsync()
    {
        return LoadAsync(null);
    }

    /// <summary>
    /// Shows a text instead of the result.
    /// </summary>
    /// <param name="text">The text</param>
    private void Fail(string text)
    {
        _failure = text;
        _loading = false;
    }

    #endregion // Methods

    #region ComponentBase

    /// <inheritdoc />
    protected override void OnInitialized()
    {
        _runner = new PageRunner(Clock, Logger.IncidentsReadFailed, IncidentsView.ListDeadline, IncidentsView.ListFailure, Fail);
    }

    /// <inheritdoc />
    protected override Task OnInitializedAsync()
    {
        return RendererInfo.IsInteractive ? LoadAsync(null) : Task.CompletedTask;
    }

    #endregion // ComponentBase

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        _runner.Dispose();
    }

    #endregion // IDisposable
}