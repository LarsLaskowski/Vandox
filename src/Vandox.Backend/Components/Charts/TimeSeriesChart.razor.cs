using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Vandox.Backend.Components.Charts;

/// <summary>
/// Draws a time series chart for the given data.
/// </summary>
public sealed partial class TimeSeriesChart : IAsyncDisposable
{
    #region Fields

    private readonly CancellationTokenSource _cancellation = new();

    private ElementReference _host;

    private ChartInterop? _interop;

    private ChartData? _rendered;

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets the title of the chart; it is also its accessible label.
    /// </summary>
    [Parameter]
    [EditorRequired]
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the data to draw; nothing is drawn while it is <c>null</c>.
    /// </summary>
    [Parameter]
    [EditorRequired]
    public ChartData? Data { get; set; }

    /// <summary>
    /// Gets or sets the JavaScript runtime.
    /// </summary>
    [Inject]
    private IJSRuntime JSRuntime { get; set; } = null!;

    #endregion // Properties

    #region ComponentBase

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        var data = Data;

        if (data is null || ReferenceEquals(data, _rendered))
        {
            return;
        }

        _interop ??= new ChartInterop(JSRuntime);

        await _interop.RenderAsync(_host, Title, data, _cancellation.Token).ConfigureAwait(false);

        _rendered = data;
    }

    #endregion // ComponentBase

    #region IAsyncDisposable

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync().ConfigureAwait(false);

        if (_interop is not null)
        {
            await _interop.DisposeAsync().ConfigureAwait(false);
        }

        _cancellation.Dispose();
    }

    #endregion // IAsyncDisposable
}