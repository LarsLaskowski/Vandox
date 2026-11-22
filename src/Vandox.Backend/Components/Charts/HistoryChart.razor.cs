using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

using Vandox.Backend.History;

namespace Vandox.Backend.Components.Charts;

/// <summary>
/// Draws the history chart, reports its width and the drag selection and click of the user.
/// </summary>
public sealed partial class HistoryChart : IAsyncDisposable
{
    #region Fields

    /// <summary>
    /// Ends the calls into the browser when the component is disposed.
    /// </summary>
    private readonly CancellationTokenSource _cancellation = new();

#pragma warning disable CA2213 // The gate never creates its wait handle; disposing it would fail a draw still waiting when the component is disposed

    /// <summary>
    /// Lets one draw at a time run.
    /// </summary>
    private readonly SemaphoreSlim _drawGate = new(1, 1);
#pragma warning restore CA2213

    /// <summary>
    /// The element that holds the chart.
    /// </summary>
    private ElementReference _host;

    /// <summary>
    /// The calls into the browser.
    /// </summary>
    private HistoryChartInterop? _interop;

    /// <summary>
    /// The events the browser calls.
    /// </summary>
    private HistoryChartEvents? _events;

    /// <summary>
    /// The reference the browser holds to the events.
    /// </summary>
    private DotNetObjectReference<HistoryChartEvents>? _reference;

    /// <summary>
    /// The view drawn, or marked.
    /// </summary>
    private HistoryChartView? _rendered;

    /// <summary>
    /// Whether the component was disposed.
    /// </summary>
    private bool _disposed;

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets the view to draw; nothing is drawn while it is <see langword="null"/>.
    /// </summary>
    [Parameter]
    public HistoryChartView? View { get; set; }

    /// <summary>
    /// Gets or sets the callback that receives the width of the chart in CSS pixels.
    /// </summary>
    [Parameter]
    public EventCallback<double> OnWidth { get; set; }

    /// <summary>
    /// Gets or sets the callback that receives a selected range.
    /// </summary>
    [Parameter]
    public EventCallback<HistoryRange> OnSelect { get; set; }

    /// <summary>
    /// Gets or sets the callback that receives a click.
    /// </summary>
    [Parameter]
    public EventCallback<HistoryPick> OnPick { get; set; }

    /// <summary>
    /// Gets or sets the accessible name of the chart.
    /// </summary>
    [Parameter]
    public string Label { get; set; } = "History chart";

    /// <summary>
    /// Gets or sets the JavaScript runtime.
    /// </summary>
    [Inject]
    private IJSRuntime JSRuntime { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Draws the view, or only moves the mark of the moment when the view differs from the one drawn in the moment alone.
    /// </summary>
    /// <returns>A task that completes when the chart is up to date</returns>
    private async Task DrawAsync()
    {
        await _drawGate.WaitAsync(_cancellation.Token).ConfigureAwait(true);

        try
        {
            var view = View;

            if (view is null || ReferenceEquals(view, _rendered) || _disposed || _interop is null || _reference is null)
            {
                return;
            }

            var drawn = _rendered;

            if (drawn is not null && ReferenceEquals(drawn.Lines, view.Lines) && ReferenceEquals(drawn.Times, view.Times))
            {
                await _interop.MarkAsync(view.Moment, _cancellation.Token).ConfigureAwait(true);
                _rendered = view;

                return;
            }

            if (await _interop.RenderAsync(_host, view, _reference, _cancellation.Token).ConfigureAwait(true))
            {
                _rendered = view;
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            // The component was disposed while a call was pending.
        }
        finally
        {
            _drawGate.Release();
        }
    }

    #endregion // Methods

    #region ComponentBase

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _interop = new HistoryChartInterop(JSRuntime);
            _events = new HistoryChartEvents(range => OnSelect.InvokeAsync(range), pick => OnPick.InvokeAsync(pick));
            _reference = DotNetObjectReference.Create(_events);

            var width = await _interop.WidthAsync(_host, _cancellation.Token).ConfigureAwait(true);

            await OnWidth.InvokeAsync(width).ConfigureAwait(true);
        }

        await DrawAsync().ConfigureAwait(true);
    }

    #endregion // ComponentBase

    #region IAsyncDisposable

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _events?.Stop();

        await _cancellation.CancelAsync().ConfigureAwait(false);

        _reference?.Dispose();

        if (_interop is not null)
        {
            await _interop.DisposeAsync().ConfigureAwait(false);
        }

        _cancellation.Dispose();
    }

    #endregion // IAsyncDisposable
}