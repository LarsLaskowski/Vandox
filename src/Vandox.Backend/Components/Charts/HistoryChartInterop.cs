using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

using Vandox.Backend.History;

namespace Vandox.Backend.Components.Charts;

// Skeleton: removed with the implementation
#pragma warning disable RH2003, S2325, S4487

/// <summary>
/// Draws the history chart through the JavaScript module <see cref="ChartInterop.ModulePath"/>.
/// </summary>
public sealed class HistoryChartInterop : IAsyncDisposable
{
    #region Fields

    /// <summary>
    /// The JavaScript runtime.
    /// </summary>
    private readonly IJSRuntime _jsRuntime;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="HistoryChartInterop"/> class.
    /// </summary>
    /// <param name="jsRuntime">The JavaScript runtime</param>
    public HistoryChartInterop(IJSRuntime jsRuntime)
    {
        _jsRuntime = jsRuntime;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Reads the width of the element that holds the chart.
    /// </summary>
    /// <param name="host">The element that holds the chart</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that returns the width in CSS pixels</returns>
    public Task<double> WidthAsync(ElementReference host, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Draws a view, replacing the chart drawn before.
    /// </summary>
    /// <param name="host">The element that holds the chart</param>
    /// <param name="view">The view to draw</param>
    /// <param name="events">The reference that receives the selection and the click</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that returns whether the chart was drawn</returns>
    /// <exception cref="ArgumentException">The view holds times that are not ascending, lines of another length or a value that is not finite</exception>
    public Task<bool> RenderAsync(ElementReference host, HistoryChartView view, DotNetObjectReference<HistoryChartEvents> events, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Marks the moment in the chart drawn, or removes the mark.
    /// </summary>
    /// <param name="moment">The moment, or <see langword="null"/> for none</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that completes when the mark is set</returns>
    public Task MarkAsync(DateTimeOffset? moment, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods

    #region IAsyncDisposable

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        throw new NotImplementedException();
    }

    #endregion // IAsyncDisposable
}

#pragma warning restore RH2003, S2325, S4487