using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Vandox.Backend.Components.Charts;

#pragma warning disable RH2003, S2325

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

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ChartInterop"/> class.
    /// </summary>
    /// <param name="jsRuntime">The JavaScript runtime</param>
    public ChartInterop(IJSRuntime jsRuntime)
    {
        throw new NotImplementedException();
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
    public Task RenderAsync(ElementReference host, string title, ChartData data, CancellationToken cancellationToken)
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