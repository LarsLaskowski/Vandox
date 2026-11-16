using Microsoft.AspNetCore.Components;

using Vandox.Backend.History;

namespace Vandox.Backend.Components.Charts;

// Skeleton: removed with the implementation
#pragma warning disable RH2003, S4487

/// <summary>
/// Draws the history chart, reports its width and the drag selection and click of the user.
/// </summary>
public sealed partial class HistoryChart : IAsyncDisposable
{
    #region Fields

    /// <summary>
    /// The element that holds the chart.
    /// </summary>
    private ElementReference _host;

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

    #endregion // Properties

    #region IAsyncDisposable

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        throw new NotImplementedException();
    }

    #endregion // IAsyncDisposable
}

#pragma warning restore RH2003, S4487