using Microsoft.AspNetCore.Components;

using Vandox.Backend.Hosting;
using Vandox.Backend.Incidents;
using Vandox.Backend.Logs;
using Vandox.Backend.Overview;
using Vandox.Backend.Security;
using Vandox.Storage;

namespace Vandox.Backend.Components.Pages;

/// <summary>
/// The page <c>/</c> of the web UI: the overview of the monitored server. Prerendered it shows its heading and a waiting text and reads nothing; once it is
/// interactive it refreshes every 30 seconds and pauses the extension of the session by the activity of its circuit.
/// </summary>
public sealed partial class OverviewPage : ComponentBase, IAsyncDisposable
{
    #region Constants

    private const string Loading = "Loading…";

    private const string StaleSuffix = " stale";

    /// <summary>
    /// The allowance for a capture time after now, so a record of the live stream is never left out.
    /// </summary>
    private static readonly TimeSpan _liveAllowance = TimeSpan.FromSeconds(90);

    #endregion // Constants

    #region Fields

    private OverviewRefresher? _refresher;

    private IDisposable? _pause;

    private OverviewModel? _model;

    private DateTimeOffset _updated;

    private string _failure = string.Empty;

    private volatile bool _disposed;

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets the overview reader.
    /// </summary>
    [Inject]
    private IOverviewReader Reader { get; set; } = null!;

    /// <summary>
    /// Gets or sets the incident browser.
    /// </summary>
    [Inject]
    private IIncidentBrowser Incidents { get; set; } = null!;

    /// <summary>
    /// Gets or sets the clock.
    /// </summary>
    [Inject]
    private TimeProvider Clock { get; set; } = null!;

    /// <summary>
    /// Gets or sets the connection of the circuit.
    /// </summary>
    [Inject]
    private CircuitConnection Connection { get; set; } = null!;

    /// <summary>
    /// Gets or sets the activity of the circuit.
    /// </summary>
    [Inject]
    private SessionActivity Activity { get; set; } = null!;

    /// <summary>
    /// Gets or sets the logger.
    /// </summary>
    [Inject]
    private ILogger<OverviewPage> Logger { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Tells whether a text ends in the stale mark of the view.
    /// </summary>
    /// <param name="text">The text</param>
    /// <returns><c>true</c> when the text is marked</returns>
    private static bool IsMarked(string text)
    {
        return text.EndsWith(StaleSuffix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Removes the stale mark of the view from a text.
    /// </summary>
    /// <param name="text">The text</param>
    /// <returns>The text without the mark</returns>
    private static string Unmarked(string text)
    {
        return IsMarked(text) ? text[..^StaleSuffix.Length] : text;
    }

    /// <summary>
    /// Makes one refresh: the five store reads in turn, then the model is applied on the dispatcher. A deadline, a store error or an invalid query keeps the shown
    /// values and sets the failure line.
    /// </summary>
    /// <param name="cancellationToken">Cancelled at the deadline or when the page is left</param>
    /// <returns>A task that completes when the refresh has ended</returns>
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            var model = await ReadModelAsync(cancellationToken).ConfigureAwait(false);

            await ApplyAsync(model, string.Empty).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && _disposed == false)
        {
            await ApplyAsync(null, FailureLine()).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is StoreException or InvalidQueryException)
        {
            Logger.OverviewReadFailed(exception);
            await ApplyAsync(null, FailureLine()).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads the data of the overview and builds its model.
    /// </summary>
    /// <param name="cancellationToken">Cancels the reads</param>
    /// <returns>A task that returns the model</returns>
    private async Task<OverviewModel> ReadModelAsync(CancellationToken cancellationToken)
    {
        var until = Clock.GetUtcNow() + _liveAllowance;
        var data = new OverviewData
                   {
                       Freshness = await Reader.FreshnessAsync(OverviewView.LiveScanLimit, until, cancellationToken).ConfigureAwait(false),
                       Values = await Reader.LatestValuesAsync(new LatestValueQuery
                                                               {
                                                                   Series = OverviewView.Series,
                                                                   Window = OverviewView.MountWindow,
                                                                   MaxLabelSets = OverviewView.MaxMounts,
                                                                   Until = until
                                                               },
                                                               cancellationToken)
                                            .ConfigureAwait(false),
                       MariaDb = await Reader.LatestMariaDbStatusAsync(until, cancellationToken).ConfigureAwait(false),
                       Services = await Reader.LatestServiceStatesAsync(new LatestServiceQuery
                                                                        {
                                                                            Retirement = OverviewView.UnitRetirement,
                                                                            Limit = OverviewView.MaxUnits,
                                                                            Until = until
                                                                        },
                                                                        cancellationToken)
                                              .ConfigureAwait(false),
                       Incidents = await Incidents.IncidentSummariesAsync(new IncidentListQuery
                                                                          {
                                                                              Limit = OverviewView.IncidentSummaries
                                                                          },
                                                                          cancellationToken)
                                                  .ConfigureAwait(false)
                   };

        return OverviewView.Build(data, Clock.GetUtcNow());
    }

    /// <summary>
    /// Creates the failure line from the clock and the time of the shown values.
    /// </summary>
    /// <returns>The line</returns>
    private string FailureLine()
    {
        var at = LogsView.ShowSeconds(Clock.GetUtcNow());

        return _model is null ? $"Refresh failed at {at}; no values have been read yet." : $"Refresh failed at {at}; the values are from {LogsView.ShowSeconds(_updated)}.";
    }

    /// <summary>
    /// Applies the result of a refresh on the dispatcher and renders it; a page that was left shows nothing more.
    /// </summary>
    /// <param name="model">The new model, or <c>null</c> to keep the shown one</param>
    /// <param name="failure">The failure line, empty after a success</param>
    /// <returns>A task that completes when the result is applied</returns>
    private Task ApplyAsync(OverviewModel? model, string failure)
    {
        if (_disposed)
        {
            return Task.CompletedTask;
        }

        return InvokeAsync(() =>
                           {
                               if (_disposed)
                               {
                                   return;
                               }

                               if (model is not null)
                               {
                                   _model = model;
                                   _updated = Clock.GetUtcNow();
                               }

                               _failure = failure;
                               StateHasChanged();
                           });
    }

    #endregion // Methods

    #region ComponentBase

    /// <inheritdoc/>
    protected override void OnAfterRender(bool firstRender)
    {
        if (firstRender == false)
        {
            return;
        }

        _pause = Activity.Pause();
        _refresher = new OverviewRefresher(Clock, Connection, RefreshAsync, exception => _ = InvokeAsync(() => DispatchExceptionAsync(exception)));
        _refresher.Start();
    }

    #endregion // ComponentBase

    #region IAsyncDisposable

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _pause?.Dispose();

        if (_refresher is not null)
        {
            await _refresher.DisposeAsync().ConfigureAwait(false);
        }
    }

    #endregion // IAsyncDisposable
}