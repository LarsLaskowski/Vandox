using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Vandox.Backend.Components.Charts;

/// <summary>
/// Draws a chart through the JavaScript module <see cref="ModulePath"/>.
/// </summary>
public sealed class ChartInterop : IAsyncDisposable
{
    #region Constants

    /// <summary>
    /// The path of the chart module.
    /// </summary>
    public const string ModulePath = "./js/charts.js";

    #endregion // Constants

    #region Fields

    private readonly IJSRuntime _jsRuntime;

    private IJSObjectReference? _module;

    private IJSObjectReference? _chart;

    private bool _disposed;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ChartInterop"/> class.
    /// </summary>
    /// <param name="jsRuntime">The JavaScript runtime</param>
    public ChartInterop(IJSRuntime jsRuntime)
    {
        ArgumentNullException.ThrowIfNull(jsRuntime);

        _jsRuntime = jsRuntime;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Creates the chart on the first call and updates it on later calls.
    /// </summary>
    /// <param name="host">The element that holds the chart</param>
    /// <param name="title">The title of the chart</param>
    /// <param name="data">The data to draw</param>
    /// <param name="cancellationToken">The token to cancel the calls</param>
    /// <returns>A task that completes when the chart is drawn</returns>
    /// <exception cref="ObjectDisposedException">The interop is disposed</exception>
    public async Task RenderAsync(ElementReference host, string title, ChartData data, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(data);

        var columns = data.ToColumns();

        if (_chart is not null)
        {
            await _chart.InvokeVoidAsync("update", cancellationToken, [columns]).ConfigureAwait(false);

            return;
        }

        _module ??= await _jsRuntime.InvokeAsync<IJSObjectReference>("import", cancellationToken, [ModulePath]).ConfigureAwait(false);

        var labels = data.Series.Select(series => series.Label).ToArray();

        _chart = await _module.InvokeAsync<IJSObjectReference>("create", cancellationToken, [host, title, labels, columns]).ConfigureAwait(false);
    }

    #endregion // Methods

    #region IAsyncDisposable

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        var chart = _chart;
        var module = _module;

        _chart = null;
        _module = null;

        try
        {
            if (chart is not null)
            {
                await chart.InvokeVoidAsync("destroy").ConfigureAwait(false);
                await chart.DisposeAsync().ConfigureAwait(false);
            }

            if (module is not null)
            {
                await module.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (JSDisconnectedException)
        {
            // The circuit is gone, and the browser has dropped the chart with it.
        }
    }

    #endregion // IAsyncDisposable
}