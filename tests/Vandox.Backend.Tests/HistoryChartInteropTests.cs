using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

using Vandox.Backend.Components.Charts;
using Vandox.Backend.History;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="HistoryChartInterop"/>. The call that draws a chart is <c>createHistory(host, setup, columns, events)</c> on the module. The columns are
/// the times in Unix seconds followed by the values of every line; the setup is an object whose properties, written as the script runtime writes them (camel case),
/// are <c>axes</c> (the texts of the drawn axes), <c>lines</c> (per line <c>label</c>, <c>scale</c>, <c>color</c>, <c>dashed</c>, <c>bridges</c> with the indexes
/// of the bridged points, <c>minimums</c> and <c>maximums</c>, empty at raw resolution), <c>marks</c> (per mark <c>kind</c>, the name of the marker kind or its
/// number, <c>at</c> and <c>endsAt</c> in Unix seconds, <c>endsAt</c> null without an end) and <c>moment</c> in Unix seconds or null.
/// </summary>
[TestClass]
public partial class HistoryChartInteropTests
{
    #region Fields

    private static readonly DateTimeOffset _start = new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);

    private static readonly string[] _axes = ["system mem_available_bytes", "system cpu_percent"];

    private static readonly string[] _firstDrawSequence = ["runtime.import", "module.createHistory"];

    private static readonly string[] _redrawSequence = ["runtime.import", "module.createHistory", "chart.destroy", "chart.DisposeAsync", "module.createHistory"];

    private static readonly string[] _disposeSequence = ["runtime.import", "module.createHistory", "chart.destroy", "chart.DisposeAsync", "module.DisposeAsync"];

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// The width is read from the module with the host element, after the module was imported once.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC3")]
    public async Task WidthAsyncImportsTheModuleAndReadsTheWidth()
    {
        // Arrange
        var runtime = new RecordingJsRuntime();
        var host = new ElementReference("host-1");

        runtime.Answer("module", "width", 412.5);

        await using var interop = new HistoryChartInterop(runtime);

        // Act
        var width = await interop.WidthAsync(host, TestContext.CancellationToken);
        var again = await interop.WidthAsync(host, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(412.5, width, "the width of the script");
        Assert.AreEqual(412.5, again, "the width of a second read");
        Assert.AreSequenceEqual(["runtime.import", "module.width", "module.width"], runtime.Calls.Select(call => $"{call.Target}.{call.Identifier}").ToArray(), "one import, then the reads");
        Assert.AreSequenceEqual<object?>([ChartInterop.ModulePath], runtime.Calls[0].Arguments, "the module path of the other charts");
        Assert.AreEqual(host, runtime.Calls[1].Arguments[0], "the host element");
        Assert.AreEqual(TestContext.CancellationToken, runtime.Calls[1].CancellationToken, "the token");
    }

    /// <summary>
    /// A view is drawn with the host, the setup with scales, axes, styles and marks, the columns of times and values, and the reference that receives the callbacks.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC3")]
    public async Task RenderAsyncPassesScalesAxesStylesAndMarks()
    {
        // Arrange
        var runtime = new RecordingJsRuntime();
        var host = new ElementReference("host-1");
        var view = View();
        var events = Events();

        await using var interop = new HistoryChartInterop(runtime);

        // Act
        var drawn = await interop.RenderAsync(host, view, events, TestContext.CancellationToken);

        // Assert
        var draw = runtime.Calls[1];
        var setup = JsonSerializer.SerializeToElement(draw.Arguments[1], JsonSerializerOptions.Web);
        var lines = setup.GetProperty("lines");
        var marks = setup.GetProperty("marks");

        Assert.IsTrue(drawn, "the chart was drawn");
        Assert.AreSequenceEqual(_firstDrawSequence, runtime.Calls.Select(call => $"{call.Target}.{call.Identifier}").ToArray(), "import, then one draw");
        Assert.HasCount(4, draw.Arguments, "host, setup, columns and events");
        Assert.AreEqual(host, draw.Arguments[0], "the host element");
        Assert.AreEqual(TestContext.CancellationToken, draw.CancellationToken, "the token");
        Assert.AreSame(events, draw.Arguments[3], "the reference that receives the callbacks");
        Assert.AreSequenceEqual(_axes, setup.GetProperty("axes").EnumerateArray().Select(axis => axis.GetString()).ToArray(), "the axes");
        Assert.AreEqual(2, lines.GetArrayLength(), "two lines");
        Assert.AreEqual("system mem_available_bytes", lines[0].GetProperty("label").GetString(), "the label of the first line");
        Assert.AreEqual(0, lines[0].GetProperty("scale").GetInt32(), "the scale of the first line");
        Assert.AreEqual(0, lines[0].GetProperty("color").GetInt32(), "the color of the first line");
        Assert.IsFalse(lines[0].GetProperty("dashed").GetBoolean(), "the first line is solid");
        Assert.AreEqual("system cpu_percent user", lines[1].GetProperty("label").GetString(), "the label of the second line");
        Assert.AreEqual(1, lines[1].GetProperty("scale").GetInt32(), "the scale of the second line");
        Assert.AreEqual(5, lines[1].GetProperty("color").GetInt32(), "the color of the second line");
        Assert.IsTrue(lines[1].GetProperty("dashed").GetBoolean(), "the second line is dashed");
        Assert.AreSequenceEqual([2], lines[0].GetProperty("bridges").EnumerateArray().Select(index => index.GetInt32()).ToArray(), "the bridged point of the first line");
        Assert.AreEqual(0, lines[1].GetProperty("bridges").GetArrayLength(), "the second line bridges nothing");
        Assert.AreSequenceEqual([1.0, 2.0, 2.5], lines[0].GetProperty("minimums").EnumerateArray().Select(value => value.GetDouble()).ToArray(), "the minimums of the first line at a step");
        Assert.AreSequenceEqual([3.0, 4.0, 4.5], lines[0].GetProperty("maximums").EnumerateArray().Select(value => value.GetDouble()).ToArray(), "the maximums of the first line at a step");
        Assert.AreEqual(3, marks.GetArrayLength(), "three marks");
        Assert.IsTrue(KindIs(marks[0], MarkerKind.OomKill), "the first mark is a kill");
        Assert.AreEqual(Seconds(_start.AddMinutes(1)), marks[0].GetProperty("at").GetDouble(), "the time of the kill");
        Assert.AreEqual(JsonValueKind.Null, KindOf(marks[0], "endsAt"), "a kill has no end");
        Assert.IsTrue(KindIs(marks[1], MarkerKind.Boot), "the second mark is a boot");
        Assert.IsTrue(KindIs(marks[2], MarkerKind.BackupRun), "the third mark is a backup run");
        Assert.AreEqual(Seconds(_start.AddMinutes(1.5)), marks[2].GetProperty("at").GetDouble(), "the start of the backup run");
        Assert.AreEqual(Seconds(_start.AddMinutes(2)), marks[2].GetProperty("endsAt").GetDouble(), "the end of the backup run");
        Assert.AreEqual(Seconds(_start.AddMinutes(1)), setup.GetProperty("moment").GetDouble(), "the moment");
        Assert.IsInstanceOfType<double?[][]>(draw.Arguments[2], "the columns are arrays of nullable numbers");

        var columns = (double?[][])draw.Arguments[2]!;

        Assert.HasCount(3, columns, "the times and two lines");
        Assert.AreSequenceEqual([Seconds(_start), Seconds(_start.AddMinutes(1)), Seconds(_start.AddMinutes(2))], columns[0].Select(time => time!.Value).ToArray(), "the times in Unix seconds");
        Assert.AreSequenceEqual<double?>([2.0, 3.0, null], columns[1], "the values of the first line, null for the gap");
        Assert.AreSequenceEqual<double?>([null, 7.0, 8.0], columns[2], "the values of the second line");
    }

    /// <summary>
    /// A view at raw resolution passes no extremes and no moment, and a line without gaps no bridges.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task RenderAsyncPassesNoExtremesAtRawResolution()
    {
        // Arrange
        var runtime = new RecordingJsRuntime();
        var view = new HistoryChartView
                   {
                       Times = [_start, _start.AddMinutes(1)],
                       Lines = [
                                   new HistoryLine
                                   {
                                       Label = "system cpu_percent",
                                       Values = [1.0, 2.0],
                                       Bridged = [false, false]
                                   }
                               ],
                       Axes = ["system cpu_percent"]
                   };

        await using var interop = new HistoryChartInterop(runtime);

        // Act
        await interop.RenderAsync(new ElementReference("host-1"), view, Events(), TestContext.CancellationToken);

        // Assert
        var setup = JsonSerializer.SerializeToElement(runtime.Calls[1].Arguments[1], JsonSerializerOptions.Web);
        var line = setup.GetProperty("lines")[0];

        Assert.AreEqual(0, line.GetProperty("minimums").GetArrayLength(), "no minimums at raw resolution");
        Assert.AreEqual(0, line.GetProperty("maximums").GetArrayLength(), "no maximums at raw resolution");
        Assert.AreEqual(0, line.GetProperty("bridges").GetArrayLength(), "no bridges");
        Assert.AreEqual(0, setup.GetProperty("marks").GetArrayLength(), "no marks");
        Assert.AreEqual(JsonValueKind.Null, KindOf(setup, "moment"), "no moment");
    }

    /// <summary>
    /// A later view destroys the chart drawn before and draws the new one; the module is imported once.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC3")]
    public async Task RenderAsyncDestroysTheOldChartBeforeDrawingAgain()
    {
        // Arrange
        var runtime = new RecordingJsRuntime();
        var host = new ElementReference("host-1");
        var events = Events();

        await using var interop = new HistoryChartInterop(runtime);

        // Act
        await interop.RenderAsync(host, View(), events, TestContext.CancellationToken);
        await interop.RenderAsync(host, View(), events, TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual(_redrawSequence, runtime.Calls.Select(call => $"{call.Target}.{call.Identifier}").ToArray(), "the old chart is destroyed and disposed before the next draw");
    }

    /// <summary>
    /// A view with times that do not ascend strictly, a line of another length or a value that is not finite is refused before any call into the browser; the
    /// message names the index of the line and never its label.
    /// </summary>
    /// <param name="problem">The problem of the view</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC3")]
    [DataRow("TimesDescend")]
    [DataRow("TimesEqual")]
    [DataRow("LengthShort")]
    [DataRow("LengthLong")]
    [DataRow("ValueNaN")]
    [DataRow("ValuePositiveInfinity")]
    [DataRow("ValueNegativeInfinity")]
    [DataRow("MinimumNaN")]
    [DataRow("MaximumInfinity")]
    public async Task RenderAsyncRefusesInvalidData(string problem)
    {
        // Arrange
        var runtime = new RecordingJsRuntime();

        await using var interop = new HistoryChartInterop(runtime);

        var view = Invalid(problem);

        // Act
        var exception = await Assert.ThrowsExactlyAsync<ArgumentException>(() => interop.RenderAsync(new ElementReference("host-1"), view, Events(), TestContext.CancellationToken));

        // Assert
        Assert.IsEmpty(runtime.Calls, "nothing was called in the browser");
        Assert.DoesNotContain("SECRET-LABEL", exception.Message, "the message never holds a label");

        if (problem.StartsWith("Times", StringComparison.Ordinal) == false)
        {
            Assert.IsTrue(LineOne().IsMatch(exception.Message), "the message names the line index 1: " + exception.Message);
        }
    }

    /// <summary>
    /// The moment is marked in the chart drawn, and a null removes the mark; without a chart nothing is called.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC8")]
    public async Task MarkAsyncMarksTheMomentInTheChartDrawn()
    {
        // Arrange
        var runtime = new RecordingJsRuntime();
        var moment = _start.AddMinutes(1).AddSeconds(30);

        await using var interop = new HistoryChartInterop(runtime);

        await interop.MarkAsync(moment, TestContext.CancellationToken);

        var before = runtime.Calls.Count;

        await interop.RenderAsync(new ElementReference("host-1"), View(), Events(), TestContext.CancellationToken);

        // Act
        await interop.MarkAsync(moment, TestContext.CancellationToken);
        await interop.MarkAsync(null, TestContext.CancellationToken);

        // Assert
        var marks = runtime.Calls.Where(call => call.Identifier == "mark").ToArray();

        Assert.AreEqual(0, before, "no chart, no call");
        Assert.HasCount(2, marks, "two calls");
        Assert.AreEqual("chart", marks[0].Target, "the mark goes to the chart object");
        Assert.AreSequenceEqual<object?>([Seconds(moment)], marks[0].Arguments, "the moment in Unix seconds");
        Assert.AreSequenceEqual<object?>([null], marks[1].Arguments, "null removes the mark");
        Assert.AreEqual(TestContext.CancellationToken, marks[0].CancellationToken, "the token");
    }

    /// <summary>
    /// A lost connection ends a draw without an error and leaves the chart unset, so that a later draw tries again.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC11")]
    public async Task RenderAsyncReturnsFalseWhenTheCircuitIsGone()
    {
        // Arrange
        var runtime = new RecordingJsRuntime();
        var host = new ElementReference("host-1");

        runtime.Fail("module", "createHistory", new JSDisconnectedException("gone"));

        await using var interop = new HistoryChartInterop(runtime);

        // Act
        var lost = await interop.RenderAsync(host, View(), Events(), TestContext.CancellationToken);

        runtime.Recover("module", "createHistory");

        var again = await interop.RenderAsync(host, View(), Events(), TestContext.CancellationToken);

        // Assert
        Assert.IsFalse(lost, "a lost connection draws nothing");
        Assert.IsTrue(again, "a later draw tries again");
        Assert.AreEqual(0, runtime.Calls.Count(call => call.Identifier == "destroy"), "nothing was drawn that needed to be destroyed");
    }

    /// <summary>
    /// Disposing destroys and disposes the chart and disposes the module, and a second disposal does nothing.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC11")]
    public async Task DisposeAsyncDestroysTheChartAndDisposesTheModule()
    {
        // Arrange
        var runtime = new RecordingJsRuntime();
        var interop = new HistoryChartInterop(runtime);

        await interop.RenderAsync(new ElementReference("host-1"), View(), Events(), TestContext.CancellationToken);

        // Act
        await interop.DisposeAsync();

        var after = runtime.Calls.Count;

        await interop.DisposeAsync();

        // Assert
        Assert.AreSequenceEqual(_disposeSequence, runtime.Calls.Select(call => $"{call.Target}.{call.Identifier}").ToArray(), "the chart is destroyed, then the chart and the module are disposed");
        Assert.HasCount(after, runtime.Calls, "a second disposal calls nothing");
    }

    /// <summary>
    /// Disposing when the connection is gone does not throw.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC11")]
    public async Task DisposeAsyncIgnoresALostConnection()
    {
        // Arrange
        var runtime = new RecordingJsRuntime();
        var interop = new HistoryChartInterop(runtime);

        await interop.RenderAsync(new ElementReference("host-1"), View(), Events(), TestContext.CancellationToken);

        runtime.Fail("chart", "destroy", new JSDisconnectedException("gone"));

        // Act
        await interop.DisposeAsync();

        // Assert
        Assert.Contains("chart.destroy", runtime.Calls.Select(call => $"{call.Target}.{call.Identifier}").ToArray(), "the chart was to be destroyed");
    }

    /// <summary>
    /// Creates the reference that receives the callbacks.
    /// </summary>
    /// <returns>The reference</returns>
    private static DotNetObjectReference<HistoryChartEvents> Events()
    {
        return DotNetObjectReference.Create(new HistoryChartEvents(_ => Task.CompletedTask, _ => Task.CompletedTask));
    }

    /// <summary>
    /// Converts a time to Unix seconds as the chart columns do.
    /// </summary>
    /// <param name="time">The time</param>
    /// <returns>The seconds</returns>
    private static double Seconds(DateTimeOffset time)
    {
        return time.ToUnixTimeMilliseconds() / 1000.0;
    }

    /// <summary>
    /// Gets the kind of a property that may be written as null or left out.
    /// </summary>
    /// <param name="element">The object</param>
    /// <param name="name">The name of the property</param>
    /// <returns>The kind of the value, null for a missing property</returns>
    private static JsonValueKind KindOf(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) ? value.ValueKind : JsonValueKind.Null;
    }

    /// <summary>
    /// Tells whether a mark has a kind, written as its name or as its number.
    /// </summary>
    /// <param name="mark">The mark in the setup</param>
    /// <param name="kind">The kind</param>
    /// <returns><see langword="true"/> when the mark has the kind</returns>
    private static bool KindIs(JsonElement mark, MarkerKind kind)
    {
        var value = mark.GetProperty("kind");

        return value.ValueKind == JsonValueKind.String ? string.Equals(value.GetString(), kind.ToString(), StringComparison.OrdinalIgnoreCase) : value.GetInt32() == (int)kind;
    }

    /// <summary>
    /// Creates a valid view at a step with two metrics, two lines, three marks and a moment.
    /// </summary>
    /// <returns>The view</returns>
    private static HistoryChartView View()
    {
        return new HistoryChartView
               {
                   Times = [_start, _start.AddMinutes(1), _start.AddMinutes(2)],
                   Lines = [
                               new HistoryLine
                               {
                                   Label = "system mem_available_bytes",
                                   Scale = 0,
                                   Color = 0,
                                   Dashed = false,
                                   Values = [2.0, 3.0, null],
                                   Bridged = [false, false, true],
                                   Minimums = [1.0, 2.0, 2.5],
                                   Maximums = [3.0, 4.0, 4.5]
                               },
                               new HistoryLine
                               {
                                   Label = "system cpu_percent user",
                                   Scale = 1,
                                   Color = 5,
                                   Dashed = true,
                                   Values = [null, 7.0, 8.0],
                                   Bridged = [false, false, false],
                                   Minimums = [null, 6.0, 7.0],
                                   Maximums = [null, 8.0, 9.0]
                               }
                           ],
                   Axes = _axes,
                   Marks = [
                               new HistoryMark
                               {
                                   Kind = MarkerKind.OomKill,
                                   At = _start.AddMinutes(1)
                               },
                               new HistoryMark
                               {
                                   Kind = MarkerKind.Boot,
                                   At = _start.AddMinutes(1)
                               },
                               new HistoryMark
                               {
                                   Kind = MarkerKind.BackupRun,
                                   At = _start.AddMinutes(1.5),
                                   EndsAt = _start.AddMinutes(2)
                               }
                           ],
                   Moment = _start.AddMinutes(1)
               };
    }

    /// <summary>
    /// Creates a view that breaks one rule of the data; the line with the problem is the second one, whose label is secret.
    /// </summary>
    /// <param name="problem">The problem</param>
    /// <returns>The view</returns>
    private static HistoryChartView Invalid(string problem)
    {
        var times = new[] { _start, _start.AddMinutes(1), _start.AddMinutes(2) };
        var good = new HistoryLine
                   {
                       Label = "system fine",
                       Values = [1.0, 2.0, 3.0],
                       Bridged = [false, false, false]
                   };
        var bad = new HistoryLine
                  {
                      Label = "SECRET-LABEL",
                      Values = problem switch
                               {
                                   "LengthShort" => [1.0, 2.0],
                                   "LengthLong" => [1.0, 2.0, 3.0, 4.0],
                                   "ValueNaN" => [1.0, double.NaN, 3.0],
                                   "ValuePositiveInfinity" => [1.0, double.PositiveInfinity, 3.0],
                                   "ValueNegativeInfinity" => [1.0, double.NegativeInfinity, 3.0],
                                   _ => [1.0, 2.0, 3.0]
                               },
                      Minimums = problem == "MinimumNaN" ? [1.0, double.NaN, 3.0] : [],
                      Maximums = problem == "MaximumInfinity" ? [1.0, double.PositiveInfinity, 3.0] : []
                  };

        return new HistoryChartView
               {
                   Times = problem switch
                           {
                               "TimesDescend" => [times[0], times[2], times[1]],
                               "TimesEqual" => [times[0], times[1], times[1]],
                               _ => times
                           },
                   Lines = [good, bad],
                   Axes = ["system fine"]
               };
    }

    #endregion // Methods

    #region Patterns

    [GeneratedRegex(@"(^|\D)1(\D|$)")]
    private static partial Regex LineOne();

    #endregion // Patterns
}