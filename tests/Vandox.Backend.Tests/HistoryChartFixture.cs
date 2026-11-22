using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

using Vandox.Backend.Components.Charts;
using Vandox.Backend.History;

namespace Vandox.Backend.Tests;

/// <summary>
/// Renders a <see cref="HistoryChart"/> over a <see cref="RecordingJsRuntime"/> that answers a width of 640 and records the callbacks of the component.
/// </summary>
internal sealed class HistoryChartFixture : IAsyncDisposable
{
    #region Fields

    /// <summary>
    /// The services of the component.
    /// </summary>
    private readonly ServiceProvider _services;

    /// <summary>
    /// The view the component starts with.
    /// </summary>
    private readonly HistoryChartView? _view;

    /// <summary>
    /// The label of the host, or null for the default.
    /// </summary>
    private readonly string? _label;

    /// <summary>
    /// Whether the renderer is disposed.
    /// </summary>
    private bool _rendererDisposed;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="HistoryChartFixture"/> class.
    /// </summary>
    /// <param name="view">The view the component starts with</param>
    /// <param name="label">The label of the host, or null for the default</param>
    internal HistoryChartFixture(HistoryChartView? view, string? label = null)
    {
        _view = view;
        _label = label;
        Runtime = new RecordingJsRuntime();

        Runtime.Answer("module", "width", 640.0);

        _services = new ServiceCollection().AddLogging().AddSingleton<IJSRuntime>(Runtime).BuildServiceProvider();
        Renderer = new ComponentTestRenderer(_services, _services.GetRequiredService<ILoggerFactory>());
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the script runtime.
    /// </summary>
    internal RecordingJsRuntime Runtime { get; }

    /// <summary>
    /// Gets the renderer.
    /// </summary>
    internal ComponentTestRenderer Renderer { get; }

    /// <summary>
    /// Gets the widths that were reported.
    /// </summary>
    internal List<double> Widths { get; } = [];

    /// <summary>
    /// Gets the selections that were reported.
    /// </summary>
    internal List<HistoryRange> Ranges { get; } = [];

    /// <summary>
    /// Gets the clicks that were reported.
    /// </summary>
    internal List<HistoryPick> Picks { get; } = [];

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Records a value.
    /// </summary>
    /// <typeparam name="TValue">The type of the value</typeparam>
    /// <param name="list">The list</param>
    /// <param name="value">The value</param>
    /// <returns>A completed task</returns>
    internal static Task Record<TValue>(List<TValue> list, TValue value)
    {
        list.Add(value);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Renders the component.
    /// </summary>
    /// <returns>A task that returns the ID of the component</returns>
    internal Task<int> AddAsync()
    {
        return Renderer.AddAsync<HistoryChart>(Parameters(_view));
    }

    /// <summary>
    /// Sets the view of the component again.
    /// </summary>
    /// <param name="id">The ID of the component</param>
    /// <param name="view">The view</param>
    /// <returns>A task that completes when the component is rendered</returns>
    internal Task SetViewAsync(int id, HistoryChartView view)
    {
        return Renderer.UpdateAsync(id, Parameters(view));
    }

    /// <summary>
    /// Disposes the renderer and with it the component, as leaving the page does.
    /// </summary>
    /// <returns>A task that completes when the component is disposed</returns>
    internal Task DisposeRendererAsync()
    {
        if (_rendererDisposed)
        {
            return Task.CompletedTask;
        }

        _rendererDisposed = true;

        return Renderer.DisposeAllAsync();
    }

    /// <summary>
    /// Creates the parameters of the component.
    /// </summary>
    /// <param name="view">The view</param>
    /// <returns>The parameters</returns>
    private Dictionary<string, object?> Parameters(HistoryChartView? view)
    {
        var parameters = new Dictionary<string, object?>
                         {
                             [nameof(HistoryChart.View)] = view,
                             [nameof(HistoryChart.OnWidth)] = EventCallback.Factory.Create<double>(this, (Func<double, Task>)(width => Record(Widths, width))),
                             [nameof(HistoryChart.OnSelect)] = EventCallback.Factory.Create<HistoryRange>(this, (Func<HistoryRange, Task>)(range => Record(Ranges, range))),
                             [nameof(HistoryChart.OnPick)] = EventCallback.Factory.Create<HistoryPick>(this, (Func<HistoryPick, Task>)(pick => Record(Picks, pick)))
                         };

        if (_label is not null)
        {
            parameters[nameof(HistoryChart.Label)] = _label;
        }

        return parameters;
    }

    #endregion // Methods

    #region IAsyncDisposable

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await DisposeRendererAsync();
        await _services.DisposeAsync();
    }

    #endregion // IAsyncDisposable
}