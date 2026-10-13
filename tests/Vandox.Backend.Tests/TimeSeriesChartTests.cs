using System.Text.Encodings.Web;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

using Vandox.Backend.Components.Charts;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="TimeSeriesChart"/>
/// </summary>
[TestClass]
public class TimeSeriesChartTests
{
    #region Fields

    private static readonly DateTimeOffset _start = new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);

    private static readonly string[] _afterFirstRender = ["runtime.import", "module.create"];

    private static readonly string[] _afterNewData = ["runtime.import", "module.create", "chart.update"];

    private static readonly string[] _destroyed = ["chart.destroy"];

    #endregion // Fields

    #region Methods

    /// <summary>
    /// The component renders a labelled host element for the chart, and the title is escaped like any Razor text.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task TimeSeriesChartRendersLabelledHost()
    {
        // Arrange
        const string title = "Load <b>\"1\"</b> & more";

        var runtime = new RecordingJsRuntime();

        await using var services = new ServiceCollection().AddLogging().AddSingleton<IJSRuntime>(runtime).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
                                                      {
                                                          [nameof(TimeSeriesChart.Title)] = title,
                                                          [nameof(TimeSeriesChart.Data)] = null
                                                      });

        // Act
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<TimeSeriesChart>(parameters)).ToHtmlString());

        // Assert
        var encoded = HtmlEncoder.Default.Encode(title);

        Assert.AreNotEqual(title, encoded, "the title needs escaping");
        Assert.Contains($"<div class=\"chart\" role=\"img\" aria-label=\"{encoded}\"></div>", html, "labelled host element");
        Assert.DoesNotContain("<b>", html, "the title is not rendered as markup");
        Assert.IsEmpty(runtime.Calls, "rendering the markup calls no script");
    }

    /// <summary>
    /// After the first render with data the chart is created, the same data renders nothing further, new data updates the chart and disposing destroys it.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task TimeSeriesChartCreatesChartAndUpdatesOnNewData()
    {
        // Arrange
        var runtime = new RecordingJsRuntime();
        var first = CreateData(1.0);
        var second = CreateData(2.0);

        await using var services = new ServiceCollection().AddLogging().AddSingleton<IJSRuntime>(runtime).BuildServiceProvider();

        var renderer = new ComponentTestRenderer(services, services.GetRequiredService<ILoggerFactory>());

        // Act
        var id = await renderer.AddAsync<TimeSeriesChart>(Parameters("Load", first));

        var afterFirst = runtime.Calls.Select(call => $"{call.Target}.{call.Identifier}").ToArray();

        await renderer.UpdateAsync(id, Parameters("Load", first));

        var afterSame = runtime.Calls.Select(call => $"{call.Target}.{call.Identifier}").ToArray();

        await renderer.UpdateAsync(id, Parameters("Load", second));

        var afterNew = runtime.Calls.Select(call => $"{call.Target}.{call.Identifier}").ToArray();

        await renderer.DisposeAllAsync();

        var afterDispose = runtime.Calls.Select(call => $"{call.Target}.{call.Identifier}").ToArray();

        // Assert
        Assert.IsEmpty(renderer.Exceptions, "no exception reached the renderer");
        Assert.AreSequenceEqual(_afterFirstRender, afterFirst, "the first render creates the chart");
        Assert.AreSequenceEqual(afterFirst, afterSame, "the same data instance calls nothing further");
        Assert.AreSequenceEqual(_afterNewData, afterNew, "a new data instance updates the chart");
        Assert.AreSequenceEqual(_destroyed, afterDispose.Skip(afterNew.Length).Take(1).ToArray(), "disposing destroys the chart");
    }

    /// <summary>
    /// Without data nothing is created, and disposing calls nothing.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task TimeSeriesChartWithoutDataCreatesNothing()
    {
        // Arrange
        var runtime = new RecordingJsRuntime();

        await using var services = new ServiceCollection().AddLogging().AddSingleton<IJSRuntime>(runtime).BuildServiceProvider();

        var renderer = new ComponentTestRenderer(services, services.GetRequiredService<ILoggerFactory>());

        // Act
        await renderer.AddAsync<TimeSeriesChart>(Parameters("Load", null));
        await renderer.DisposeAllAsync();

        // Assert
        Assert.IsEmpty(renderer.Exceptions, "no exception reached the renderer");
        Assert.IsEmpty(runtime.Calls, "no script call without data");
    }

    /// <summary>
    /// Creates the parameters of the component.
    /// </summary>
    /// <param name="title">The title</param>
    /// <param name="data">The data</param>
    /// <returns>The parameters</returns>
    private static Dictionary<string, object?> Parameters(string title, ChartData? data)
    {
        return new Dictionary<string, object?>
               {
                   [nameof(TimeSeriesChart.Title)] = title,
                   [nameof(TimeSeriesChart.Data)] = data
               };
    }

    /// <summary>
    /// Creates chart data; the offset tells two instances apart.
    /// </summary>
    /// <param name="offset">The offset added to the values</param>
    /// <returns>The data</returns>
    private static ChartData CreateData(double offset)
    {
        return new ChartData([_start, _start.AddSeconds(60)], [new ChartSeries("load", [offset, null])]);
    }

    #endregion // Methods
}