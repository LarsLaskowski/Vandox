using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

using Vandox.Backend.Components.Charts;
using Vandox.Backend.History;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="HistoryChart"/>
/// </summary>
[TestClass]
public class HistoryChartTests
{
    #region Fields

    private static readonly DateTimeOffset _start = new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);

    private static readonly string[] _firstRenderSequence = ["runtime.import", "module.width", "module.createHistory"];

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// The first render writes the labelled host, reports the width of the host and then draws the view with a reference that receives the callbacks.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC3")]
    public async Task HistoryChartReportsTheWidthAndDrawsTheView()
    {
        // Arrange
        await using var fixture = new HistoryChartFixture(View(1));

        // Act
        var id = await fixture.AddAsync();

        // Assert
        var host = fixture.Renderer.Elements(id).Single(element => element.Tag == "div");

        Assert.Contains("history-chart", host.Classes, "the host has the class of the history chart");
        Assert.AreEqual("img", host.Attributes["role"], "the host has a role");
        Assert.AreEqual("History chart", host.Attributes["aria-label"], "the host has a label");
        Assert.AreSequenceEqual([640.0], fixture.Widths, "the width is reported once");
        Assert.AreSequenceEqual(_firstRenderSequence, fixture.Runtime.Calls.Select(call => $"{call.Target}.{call.Identifier}").ToArray(), "one import, the width, then the draw");
        Assert.IsInstanceOfType<ElementReference>(fixture.Runtime.Calls[1].Arguments[0], "the width is read from the host element");
        Assert.IsInstanceOfType<DotNetObjectReference<HistoryChartEvents>>(fixture.Runtime.Calls[2].Arguments[3], "the draw gets the reference for the callbacks");
    }

    /// <summary>
    /// Without a view the chart reports its width and draws nothing.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC3")]
    public async Task HistoryChartWithoutViewDrawsNothing()
    {
        // Arrange
        await using var fixture = new HistoryChartFixture(null);

        // Act
        await fixture.AddAsync();

        // Assert
        Assert.AreSequenceEqual([640.0], fixture.Widths, "the width is reported");
        Assert.DoesNotContain(call => call.Identifier == "createHistory", fixture.Runtime.Calls, "nothing is drawn");
    }

    /// <summary>
    /// The same view is drawn once; a new view destroys the chart and draws again.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC3")]
    public async Task HistoryChartDrawsOnlyANewView()
    {
        // Arrange
        var first = View(1);
        var second = View(2);

        await using var fixture = new HistoryChartFixture(first);

        var id = await fixture.AddAsync();

        // Act
        await fixture.SetViewAsync(id, first);

        var afterSame = fixture.Runtime.Calls.Count(call => call.Identifier == "createHistory");

        await fixture.SetViewAsync(id, second);

        // Assert
        var identifiers = fixture.Runtime.Calls.Select(call => call.Identifier).ToArray();

        Assert.AreEqual(1, afterSame, "the same view is not drawn again");
        Assert.AreEqual(2, identifiers.Count(identifier => identifier == "createHistory"), "the new view is drawn");
        Assert.IsLessThan(Array.LastIndexOf(identifiers, "createHistory"), Array.IndexOf(identifiers, "destroy"), "the old chart is destroyed before the new one is drawn");
        Assert.HasCount(1, fixture.Widths, "the width is reported on the first render only");
    }

    /// <summary>
    /// A selection and a click that the browser reports reach the callbacks of the component.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC2")]
    [TestCategory("AC8")]
    public async Task HistoryChartForwardsSelectionAndPick()
    {
        // Arrange
        await using var fixture = new HistoryChartFixture(View(1));

        await fixture.AddAsync();

        var events = HistoryPageHarness.EventsOf(fixture.Runtime.Calls.Single(call => call.Identifier == "createHistory"));

        // Act
        await fixture.Renderer.Dispatcher.InvokeAsync(() => events.Select(1_800_000_000.5, 1_800_000_010));
        await fixture.Renderer.Dispatcher.InvokeAsync(() => events.Pick(1_800_000_004, 8));

        // Assert
        Assert.HasCount(1, fixture.Ranges, "one selection");
        Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000), fixture.Ranges[0].Start, "the start of the selection");
        Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(1_800_000_010), fixture.Ranges[0].End, "the end of the selection");
        Assert.HasCount(1, fixture.Picks, "one click");
        Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(1_800_000_004), fixture.Picks[0].At, "the time of the click");
        Assert.AreEqual(TimeSpan.FromSeconds(8), fixture.Picks[0].Tolerance, "the tolerance of the click");
    }

    /// <summary>
    /// Disposing the chart destroys it, disposes the reference that the browser calls and makes a callback that arrives late do nothing.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC11")]
    public async Task HistoryChartDisposalDisposesTheReferenceAndLateCallbacksDoNothing()
    {
        // Arrange
        await using var fixture = new HistoryChartFixture(View(1));

        await fixture.AddAsync();

        var draw = fixture.Runtime.Calls.Single(call => call.Identifier == "createHistory");
        var reference = (DotNetObjectReference<HistoryChartEvents>)draw.Arguments[3]!;
        var events = reference.Value;

        // Act
        await fixture.DisposeRendererAsync();
        await fixture.Renderer.Dispatcher.InvokeAsync(() => events.Select(1_800_000_000, 1_800_000_010));
        await fixture.Renderer.Dispatcher.InvokeAsync(() => events.Pick(1_800_000_004, 8));

        // Assert
        var identifiers = fixture.Runtime.Calls.Select(call => $"{call.Target}.{call.Identifier}").ToArray();

        Assert.Throws<ObjectDisposedException>(() => reference.Value, "the reference the browser holds is disposed");
        Assert.Contains("chart.destroy", identifiers, "the chart is destroyed");
        Assert.Contains("module.DisposeAsync", identifiers, "the module is disposed");
        Assert.IsEmpty(fixture.Ranges, "a late selection does nothing");
        Assert.IsEmpty(fixture.Picks, "a late click does nothing");
    }

    /// <summary>
    /// Creates a view whose first value tells it from another view.
    /// </summary>
    /// <param name="value">The first value</param>
    /// <returns>The view</returns>
    private static HistoryChartView View(double value)
    {
        return new HistoryChartView
               {
                   Times = [_start, _start.AddMinutes(1)],
                   Lines = [
                               new HistoryLine
                               {
                                   Label = "system cpu_percent",
                                   Values = [value, 2.0],
                                   Bridged = [false, false]
                               }
                           ],
                   Axes = ["system cpu_percent"]
               };
    }

    #endregion // Methods
}