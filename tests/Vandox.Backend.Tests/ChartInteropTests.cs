using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

using Vandox.Backend.Components.Charts;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="ChartInterop"/>
/// </summary>
[TestClass]
public class ChartInteropTests
{
    #region Fields

    private static readonly DateTimeOffset _start = new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);

    private static readonly string[] _labels = ["load", "memory"];

    private static readonly string[] _createSequence = ["import", "create", "update"];

    private static readonly string[] _disposeSequence = ["chart.destroy", "chart.DisposeAsync", "module.DisposeAsync"];

    private static readonly string[] _singleCreateSequence = ["runtime.import", "module.create", "chart.update"];

    private static readonly string[] _lateDisposeSequence = ["runtime.import", "module.create", "chart.destroy", "chart.DisposeAsync", "module.DisposeAsync"];

    private static readonly object?[] _importArguments = [ChartInterop.ModulePath];

    #endregion // Fields

    #region Methods

    /// <summary>
    /// The first render imports the module once and creates the chart with the host, the title, the labels and the columns.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ChartInteropCreatesChartOnFirstRender()
    {
        // Arrange
        var runtime = new RecordingJsRuntime();
        var host = new ElementReference("host-1");
        var data = CreateData(1.0);
        using var source = new CancellationTokenSource();

        await using var interop = new ChartInterop(runtime);

        // Act
        await interop.RenderAsync(host, "Load", data, source.Token);

        // Assert
        Assert.HasCount(2, runtime.Calls, "one import and one create");
        Assert.AreEqual("runtime", runtime.Calls[0].Target, "import goes to the runtime");
        Assert.AreEqual("import", runtime.Calls[0].Identifier, "import identifier");
        Assert.AreSequenceEqual(_importArguments, runtime.Calls[0].Arguments, "import path");
        Assert.AreEqual(source.Token, runtime.Calls[0].CancellationToken, "import token");
        Assert.AreEqual("module", runtime.Calls[1].Target, "create goes to the module");
        Assert.AreEqual("create", runtime.Calls[1].Identifier, "create identifier");
        Assert.AreEqual(source.Token, runtime.Calls[1].CancellationToken, "create token");
        Assert.HasCount(4, runtime.Calls[1].Arguments, "create arguments");
        Assert.AreEqual(host, runtime.Calls[1].Arguments[0], "host element");
        Assert.AreEqual("Load", runtime.Calls[1].Arguments[1], "title");
        Assert.IsInstanceOfType<string[]>(runtime.Calls[1].Arguments[2], "labels are an array of strings");
        Assert.AreSequenceEqual(_labels, (string[])runtime.Calls[1].Arguments[2]!, "labels");
        Assert.IsInstanceOfType<double?[][]>(runtime.Calls[1].Arguments[3], "columns are arrays of nullable numbers");
        AssertColumns(data.ToColumns(), (double?[][])runtime.Calls[1].Arguments[3]!);
    }

    /// <summary>
    /// A later render updates the chart object with the new columns: no second import and no second create.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ChartInteropUpdatesChartOnLaterRender()
    {
        // Arrange
        var runtime = new RecordingJsRuntime();
        var host = new ElementReference("host-1");
        var first = CreateData(1.0);
        var second = CreateData(2.0);
        using var firstSource = new CancellationTokenSource();
        using var secondSource = new CancellationTokenSource();

        await using var interop = new ChartInterop(runtime);

        await interop.RenderAsync(host, "Load", first, firstSource.Token);

        // Act
        await interop.RenderAsync(host, "Load", second, secondSource.Token);

        // Assert
        Assert.AreSequenceEqual(_createSequence, runtime.Calls.Select(call => call.Identifier).ToArray(), "calls in order");
        Assert.AreEqual("chart", runtime.Calls[2].Target, "update goes to the chart object");
        Assert.AreEqual(secondSource.Token, runtime.Calls[2].CancellationToken, "update token");
        Assert.HasCount(1, runtime.Calls[2].Arguments, "update arguments");
        Assert.IsInstanceOfType<double?[][]>(runtime.Calls[2].Arguments[0], "columns are arrays of nullable numbers");
        AssertColumns(second.ToColumns(), (double?[][])runtime.Calls[2].Arguments[0]!);
    }

    /// <summary>
    /// Disposing destroys the chart and disposes the chart and the module reference.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ChartInteropDisposeDestroysChartAndModule()
    {
        // Arrange
        var runtime = new RecordingJsRuntime();
        var interop = new ChartInterop(runtime);

        await interop.RenderAsync(new ElementReference("host-1"), "Load", CreateData(1.0), CancellationToken.None);

        // Act
        await interop.DisposeAsync();

        // Assert
        var tail = runtime.Calls.Skip(2).Select(call => $"{call.Target}.{call.Identifier}").ToArray();

        Assert.AreSequenceEqual(_disposeSequence, tail, "destroy, then dispose the chart, then dispose the module");
    }

    /// <summary>
    /// Disposing a second time calls nothing more.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ChartInteropDisposeTwiceDestroysOnce()
    {
        // Arrange
        var runtime = new RecordingJsRuntime();
        var interop = new ChartInterop(runtime);

        await interop.RenderAsync(new ElementReference("host-1"), "Load", CreateData(1.0), CancellationToken.None);
        await interop.DisposeAsync();

        var callsAfterFirst = runtime.Calls.Count;

        // Act
        await interop.DisposeAsync();

        // Assert
        Assert.HasCount(callsAfterFirst, runtime.Calls, "the second disposal calls nothing");
    }

    /// <summary>
    /// Disposing before the first render calls nothing.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ChartInteropDisposeBeforeRenderDoesNothing()
    {
        // Arrange
        var runtime = new RecordingJsRuntime();
        var interop = new ChartInterop(runtime);

        // Act
        await interop.DisposeAsync();

        // Assert
        Assert.IsEmpty(runtime.Calls, "no call before the first render");
    }

    /// <summary>
    /// Disposing completes without an exception when the circuit is gone, whichever call notices it.
    /// </summary>
    /// <param name="target">The target of the failing call</param>
    /// <param name="identifier">The identifier of the failing call</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("chart", "destroy")]
    [DataRow("chart", "DisposeAsync")]
    [DataRow("module", "DisposeAsync")]
    public async Task ChartInteropDisposeToleratesDisconnectedCircuit(string target, string identifier)
    {
        // Arrange
        var runtime = new RecordingJsRuntime();
        var interop = new ChartInterop(runtime);

        await interop.RenderAsync(new ElementReference("host-1"), "Load", CreateData(1.0), CancellationToken.None);

        runtime.Fail(target, identifier, new JSDisconnectedException("the circuit is gone"));

        // Act
        await interop.DisposeAsync();

        // Assert
        Assert.IsNotEmpty(runtime.Calls.Where(call => call.Target == target && call.Identifier == identifier).ToArray(), "the failing call was made");
    }

    /// <summary>
    /// Any other exception from the disposal is not swallowed.
    /// </summary>
    /// <param name="target">The target of the failing call</param>
    /// <param name="identifier">The identifier of the failing call</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("chart", "destroy")]
    [DataRow("chart", "DisposeAsync")]
    [DataRow("module", "DisposeAsync")]
    public async Task ChartInteropDisposeLetsOtherExceptionsThrough(string target, string identifier)
    {
        // Arrange
        var runtime = new RecordingJsRuntime();
        var interop = new ChartInterop(runtime);

        await interop.RenderAsync(new ElementReference("host-1"), "Load", CreateData(1.0), CancellationToken.None);

        runtime.Fail(target, identifier, new InvalidOperationException("broken"));

        // Act
        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await interop.DisposeAsync(), "the exception passes");

        // Assert
        Assert.AreEqual("broken", exception.Message, "the original exception");
    }

    /// <summary>
    /// Rendering after the disposal is refused.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ChartInteropRenderAfterDisposeThrows()
    {
        // Arrange
        var runtime = new RecordingJsRuntime();
        var interop = new ChartInterop(runtime);

        await interop.DisposeAsync();

        // Act
        var exception = await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => interop.RenderAsync(new ElementReference("host-1"), "Load", CreateData(1.0), CancellationToken.None), "render after dispose");

        // Assert
        Assert.IsNotNull(exception, "the refusal is an ObjectDisposedException");
        Assert.IsEmpty(runtime.Calls, "nothing was called");
    }

    /// <summary>
    /// A render without a title or without data is refused before anything is called.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ChartInteropRenderWithoutTitleOrDataThrows()
    {
        // Arrange
        var runtime = new RecordingJsRuntime();
        var host = new ElementReference("host-1");

        await using var interop = new ChartInterop(runtime);

        // Act
        var titleException = await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => interop.RenderAsync(host, null!, CreateData(1.0), CancellationToken.None), "render without title");
        var dataException = await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => interop.RenderAsync(host, "Load", null!, CancellationToken.None), "render without data");

        // Assert
        Assert.AreEqual("title", titleException.ParamName, "the title is named");
        Assert.AreEqual("data", dataException.ParamName, "the data is named");
        Assert.IsEmpty(runtime.Calls, "nothing was called");
    }

    /// <summary>
    /// Disposing while <c>create</c> is pending waits for it, then destroys and disposes the chart that arrives and the module.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ChartInteropDisposeDuringPendingCreateDestroysTheLateChart()
    {
        // Arrange
        var runtime = new RecordingJsRuntime
                      {
                          CreateGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
                      };
        var interop = new ChartInterop(runtime);
        var render = interop.RenderAsync(new ElementReference("host-1"), "Load", CreateData(1.0), CancellationToken.None);

        // Act
        var dispose = interop.DisposeAsync().AsTask();

        runtime.CreateGate.SetResult();

        await render;
        await dispose;

        // Assert
        var sequence = runtime.Calls.Select(call => $"{call.Target}.{call.Identifier}").ToArray();

        Assert.AreSequenceEqual(_lateDisposeSequence, sequence, "import, create, then destroy and dispose the late chart, then dispose the module");
    }

    /// <summary>
    /// A render queued behind a pending <c>create</c> before the disposal ends quietly: no exception, no update, and the chart is torn down once.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ChartInteropQueuedRenderBeforeDisposeCompletesWithoutException()
    {
        // Arrange
        var runtime = new RecordingJsRuntime
                      {
                          CreateGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
                      };
        var interop = new ChartInterop(runtime);
        var host = new ElementReference("host-1");
        var first = interop.RenderAsync(host, "Load", CreateData(1.0), CancellationToken.None);
        var second = interop.RenderAsync(host, "Load", CreateData(2.0), CancellationToken.None);
        var dispose = interop.DisposeAsync().AsTask();

        // Act
        runtime.CreateGate.SetResult();

        await first;
        await second;
        await dispose;

        // Assert
        var sequence = runtime.Calls.Select(call => $"{call.Target}.{call.Identifier}").ToArray();

        Assert.AreSequenceEqual(_lateDisposeSequence, sequence, "import, create, destroy, dispose the chart, dispose the module; no update");
    }

    /// <summary>
    /// Two renders while <c>create</c> is pending create the chart once and update it once.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ChartInteropRendersDuringPendingCreateCreateOnce()
    {
        // Arrange
        var runtime = new RecordingJsRuntime
                      {
                          CreateGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
                      };

        await using var interop = new ChartInterop(runtime);

        var host = new ElementReference("host-1");
        var first = interop.RenderAsync(host, "Load", CreateData(1.0), CancellationToken.None);
        var second = interop.RenderAsync(host, "Load", CreateData(2.0), CancellationToken.None);

        // Act
        runtime.CreateGate.SetResult();

        await first;
        await second;

        // Assert
        var sequence = runtime.Calls.Select(call => $"{call.Target}.{call.Identifier}").ToArray();

        Assert.AreSequenceEqual(_singleCreateSequence, sequence, "one import, one create, one update");
    }

    /// <summary>
    /// Creates chart data with two series; the offset tells the data of two renders apart.
    /// </summary>
    /// <param name="offset">The offset added to the values</param>
    /// <returns>The data</returns>
    private static ChartData CreateData(double offset)
    {
        return new ChartData([_start, _start.AddSeconds(60)], [new ChartSeries("load", [offset, null]), new ChartSeries("memory", [offset + 10.0, offset + 20.0])]);
    }

    /// <summary>
    /// Asserts that two column sets hold the same values.
    /// </summary>
    /// <param name="expected">The expected columns</param>
    /// <param name="actual">The actual columns</param>
    private static void AssertColumns(double?[][] expected, double?[][] actual)
    {
        Assert.HasCount(expected.Length, actual, "column count");

        for (var index = 0; index < expected.Length; index++)
        {
            Assert.AreSequenceEqual(expected[index], actual[index], $"column {index}");
        }
    }

    #endregion // Methods
}