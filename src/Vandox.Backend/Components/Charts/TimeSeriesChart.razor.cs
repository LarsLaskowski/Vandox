using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Vandox.Backend.Components.Charts;

#pragma warning disable RH2003, S2325, S4487

/// <summary>
/// Draws a time series chart for the given data.
/// </summary>
public sealed partial class TimeSeriesChart : IAsyncDisposable
{
    #region Fields

    private ElementReference _host;

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
    protected override Task OnAfterRenderAsync(bool firstRender)
    {
        throw new NotImplementedException();
    }

    #endregion // ComponentBase

    #region IAsyncDisposable

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        throw new NotImplementedException();
    }

    #endregion // IAsyncDisposable
}