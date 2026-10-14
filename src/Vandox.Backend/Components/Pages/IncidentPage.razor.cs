using System.Globalization;
using System.Text;

using Microsoft.AspNetCore.Components;

using Vandox.Backend.Hosting;
using Vandox.Backend.Incidents;
using Vandox.Backend.Logs;
using Vandox.Core.Incidents;
using Vandox.Storage;

namespace Vandox.Backend.Components.Pages;

/// <summary>
/// The page <c>/incidents/{Id}</c> of the web UI.
/// </summary>
public sealed partial class IncidentPage : ComponentBase, IDisposable
{
    #region Fields

    private static readonly CompositeFormat _chartCut = CompositeFormat.Parse(IncidentsView.ChartCut);

    private PageRunner _runner = null!;

    private PageRunner _windowRunner = null!;

    private long? _loadedId;

    private Incident? _stored;

    private IncidentDetailView? _detail;

    private IncidentWindowData? _window;

    private IReadOnlyList<IncidentNote> _moved = [];

    private string _ownText = string.Empty;

    private DateTimeOffset? _ownUpdatedAt;

    private bool _moreNotes;

    private string _failure = string.Empty;

    private string _windowFailure = string.Empty;

    private bool _loading = true;

    private bool _unknown;

    private bool _windowLoading;

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets the number of the incident.
    /// </summary>
    [Parameter]
    public long Id { get; set; }

    /// <summary>
    /// Gets or sets the incident browser.
    /// </summary>
    [Inject]
    private IIncidentBrowser Browser { get; set; } = null!;

    /// <summary>
    /// Gets or sets the reader of the data of the charts.
    /// </summary>
    [Inject]
    private IncidentWindowReader Window { get; set; } = null!;

    /// <summary>
    /// Gets or sets the clock.
    /// </summary>
    [Inject]
    private TimeProvider Clock { get; set; } = null!;

    /// <summary>
    /// Gets or sets the logger.
    /// </summary>
    [Inject]
    private ILogger<IncidentPage> Logger { get; set; } = null!;

    /// <summary>
    /// Gets the text that names the time the memory chart was cut at, or an empty text.
    /// </summary>
    private string MemoryCut => Cut(IncidentsView.MaxChartSnapshots, _window?.MemoryCutAt);

    /// <summary>
    /// Gets the text that names the time the connection chart was cut at, or an empty text.
    /// </summary>
    private string ConnectionsCut => Cut(IncidentsView.MaxChartConnections, _window?.ConnectionsCutAt);

    /// <summary>
    /// Gets a value indicating whether the window was read and holds no data for any chart.
    /// </summary>
    private bool NoWindowData => _window is { Memory: null, MemoryAndSwap: null, Connections: null };

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Writes the text for a chart that was cut.
    /// </summary>
    /// <param name="snapshots">The number of snapshots the chart holds</param>
    /// <param name="cutAt">The time of the last snapshot, or <c>null</c> when the chart was not cut</param>
    /// <returns>The text, or an empty text</returns>
    private static string Cut(int snapshots, DateTimeOffset? cutAt)
    {
        return cutAt is null ? string.Empty : string.Format(CultureInfo.InvariantCulture, _chartCut, snapshots, LogsView.ShowSeconds(cutAt.Value));
    }

    /// <summary>
    /// Reads the incident and its notes, then the data of the charts of its window.
    /// </summary>
    /// <returns>A task that completes when both reads have ended</returns>
    private async Task LoadAsync()
    {
        var id = Id;

        _loadedId = id;
        Reset();

        if (id <= 0)
        {
            _unknown = true;
            _loading = false;

            return;
        }

        await _runner.RunAsync(async token =>
                               {
                                   var stored = await Browser.IncidentAsync(id, token).ConfigureAwait(true);

                                   token.ThrowIfCancellationRequested();

                                   if (stored is null)
                                   {
                                       _unknown = true;
                                       _loading = false;

                                       return;
                                   }

                                   var notes = await Browser.IncidentNotesAsync(id, token).ConfigureAwait(true);

                                   token.ThrowIfCancellationRequested();
                                   Apply(stored, notes);
                               })
                     .ConfigureAwait(true);

        if (_stored is { } incident && _loadedId == id)
        {
            await ReadWindowAsync(incident).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Reads the data of the charts of the window of an incident.
    /// </summary>
    /// <param name="incident">The incident</param>
    /// <returns>A task that completes when the read has ended</returns>
    private Task ReadWindowAsync(Incident incident)
    {
        return _windowRunner.RunAsync(async token =>
                                      {
                                          _windowLoading = true;
                                          StateHasChanged();

                                          var data = await Window.ReadAsync(incident, token).ConfigureAwait(true);

                                          token.ThrowIfCancellationRequested();

                                          _window = data;
                                          _windowLoading = false;
                                      });
    }

    /// <summary>
    /// Shows the incident and its notes.
    /// </summary>
    /// <param name="stored">The incident</param>
    /// <param name="notes">The notes</param>
    private void Apply(StoredIncident stored, IncidentNotePage notes)
    {
        var own = notes.Notes.FirstOrDefault(note => note.WrittenFor == stored.Id);

        _stored = stored.Incident;
        _detail = IncidentDetailView.From(stored);
        _ownText = own?.Text ?? string.Empty;
        _ownUpdatedAt = own?.UpdatedAt;
        _moved = notes.Notes.Where(note => note.WrittenFor != stored.Id).ToList();
        _moreNotes = notes.More;
        _loading = false;
    }

    /// <summary>
    /// Clears what an earlier incident left on the page.
    /// </summary>
    private void Reset()
    {
        _stored = null;
        _detail = null;
        _window = null;
        _moved = [];
        _ownText = string.Empty;
        _ownUpdatedAt = null;
        _moreNotes = false;
        _failure = string.Empty;
        _windowFailure = string.Empty;
        _loading = true;
        _unknown = false;
        _windowLoading = false;
    }

    /// <summary>
    /// Shows a text instead of the incident.
    /// </summary>
    /// <param name="text">The text</param>
    private void Fail(string text)
    {
        _failure = text;
        _loading = false;
    }

    /// <summary>
    /// Shows a text instead of the charts.
    /// </summary>
    /// <param name="text">The text</param>
    private void FailWindow(string text)
    {
        _windowFailure = text;
        _windowLoading = false;
    }

    #endregion // Methods

    #region ComponentBase

    /// <inheritdoc />
    protected override void OnInitialized()
    {
        _runner = new PageRunner(Clock, Logger.IncidentsReadFailed, IncidentsView.ListDeadline, IncidentsView.ListFailure, Fail);
        _windowRunner = new PageRunner(Clock, Logger.IncidentsReadFailed, IncidentsView.WindowDeadline, IncidentsView.WindowFailure, FailWindow);
    }

    /// <inheritdoc />
    protected override Task OnParametersSetAsync()
    {
        return RendererInfo.IsInteractive && _loadedId != Id ? LoadAsync() : Task.CompletedTask;
    }

    #endregion // ComponentBase

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        _runner.Dispose();
        _windowRunner.Dispose();
    }

    #endregion // IDisposable
}