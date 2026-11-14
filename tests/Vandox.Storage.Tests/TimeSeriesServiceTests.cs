using System.Globalization;

using Microsoft.Extensions.Time.Testing;

using Vandox.Core.Model;

namespace Vandox.Storage.Tests;

/// <summary>
/// Tests for <see cref="TimeSeriesService"/> on a real store and a fake clock
/// </summary>
[TestClass]
public class TimeSeriesServiceTests
{
    #region Constants

    private const string FirstBoot = TimelineSamples.BootId;

    private const string SecondBoot = TimelineSamples.OtherBootId;

    private const string Unit = "svc.service";

    #endregion // Constants

    #region Fields

    private static readonly SeriesKey _mem = new("system", "mem_available_bytes");

    private static readonly SeriesKey _free = new("system", "mem_free_bytes");

    private static readonly SeriesKey _rss = new("agent", "agent_rss_bytes");

    private static readonly SeriesKey _filesystem = new("system", "filesystem_free_bytes");

    private static readonly TimeSpan[] _ladder = [.. new[] { 5, 10, 15, 30 }.Select(minutes => TimeSpan.FromMinutes(minutes)), .. new[] { 1, 2, 3, 6, 12 }.Select(hours => TimeSpan.FromHours(hours)), TimeSpan.FromDays(1)];

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Gets the lengths of the range in days and the targets of the step table.
    /// </summary>
    /// <returns>The rows</returns>
    public static IEnumerable<object[]> StepChoices()
    {
        foreach (var days in new[] { 1 / 24.0, 1, 7, 30, 365, 1095 })
        {
            foreach (var target in new[] { 1, 100, 1000, 2000 })
            {
                yield return [days, target];
            }
        }
    }

    /// <summary>
    /// A range within the raw period whose values fall on at most the target distinct times is raw, with one point per time and no completeness time.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    [TestCategory("AC6")]
    public async Task QueryAsyncShortRangeWithinTargetIsRaw()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteSamplesAsync(store, _mem, At(0), TimeSpan.FromSeconds(30), 60);

        // Act
        var result = await RunAsync(store, At(1), At(0), At(0.5), 1000, null, _mem);

        // Assert
        Assert.AreEqual(TimeSeriesResolution.Raw, result.Resolution, "60 times are within the target");
        Assert.IsNull(result.Step, "raw has no step");
        Assert.HasCount(60, result.Times, "the axis holds the capture times");
        Assert.AreEqual(At(0), result.Times[0], "first time");
        Assert.AreEqual(At(0).AddMinutes(29.5), result.Times[59], "last time");
        Assert.HasCount(1, result.Lines, "one line");
        Assert.AreEqual("system", result.Lines[0].Source, "source of the line");
        Assert.AreEqual("mem_available_bytes", result.Lines[0].Name, "name of the line");
        Assert.AreEqual(string.Empty, result.Lines[0].Labels, "no labels");
        Assert.IsTrue(result.Lines[0].Points.All(point => point.State == PointState.Value), "every time holds a value");
        Assert.AreEqual(5, result.Lines[0].Points[5].Last, "value of the sixth sample");
        Assert.AreEqual(1, result.Lines[0].Points[5].Count, "one sample per raw point");
        Assert.IsNull(result.CompleteUntil, "raw has no completeness time");
        Assert.IsFalse(result.StartsBeforeRollups, "the range starts within the periods");
        Assert.IsEmpty(result.CutSeries, "nothing cut");
    }

    /// <summary>
    /// A range whose raw values fall on more distinct times than the target is merged into the smallest step that fits.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    public async Task QueryAsyncRawOverTargetTimesChoosesFiveMinuteStep()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteSamplesAsync(store, _mem, At(0), TimeSpan.FromSeconds(30), 120);
        await FoldAsync(store, At(2), null);

        // Act
        var result = await RunAsync(store, At(2), At(0), At(1), 100, null, _mem);

        // Assert
        Assert.AreEqual(TimeSeriesResolution.Step, result.Resolution, "120 times are over the target");
        Assert.AreEqual(TimeSpan.FromMinutes(5), result.Step, "12 steps of five minutes fit the target");
        Assert.HasCount(12, result.Times, "twelve steps");
        Assert.HasCount(1, result.Lines, "one line");
        Assert.IsTrue(result.Lines[0].Points.All(point => point.State == PointState.Value && point.Count == 10), "ten samples per step");
    }

    /// <summary>
    /// A year at 1,000 points is merged from the hourly rollups at a step of twelve hours.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    public async Task QueryAsyncYearChoosesTwelveHourStepFromHourlyRollups()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var now = At(0);
        var from = now.AddDays(-365);

        await TimeSeriesSamples.WriteAsync(store,
                                           string.Empty,
                                           string.Empty,
                                           TestContext.CancellationToken,
                                           Enumerable.Range(0, 365).Select(day => TimeSeriesSamples.Metric(_mem.Source, _mem.Name, from.AddDays(day).AddMinutes(30), day)).ToArray());
        await FoldAsync(store, now, null);

        // Act
        var result = await RunAsync(store, now, from, now, 1000, null, _mem);

        // Assert
        Assert.AreEqual(TimeSeriesResolution.Step, result.Resolution, "a year is not raw");
        Assert.AreEqual(TimeSpan.FromHours(12), result.Step, "731 steps of twelve hours fit 1,000, 1,461 of six hours do not");
        Assert.HasCount(731, result.Times, "steps overlapping the year");
        Assert.AreEqual(new DateTimeOffset(2025, 10, 1, 0, 0, 0, TimeSpan.Zero), result.Times[0], "the step holding the start");
        Assert.AreEqual(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), result.Times[^1], "the step holding the end");
        Assert.HasCount(1, result.Lines, "one line");
        Assert.HasCount(731, result.Lines[0].Points, "one point per step");
        Assert.AreEqual(365, result.Lines[0].Points.Count(point => point.State == PointState.Value), "a value in each step that holds a day's sample");
        Assert.AreEqual(366, result.Lines[0].Points.Count(point => point.State == PointState.Gap), "the other steps are gaps");
        Assert.IsFalse(result.StartsBeforeRollups, "the year starts within the hourly period");
    }

    /// <summary>
    /// The step is the smallest of the ladder whose steps overlapping the range are at most the target, also for a range and a target at the ends of the table.
    /// </summary>
    /// <param name="days">The length of the range in days</param>
    /// <param name="target">The target</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    [DynamicData(nameof(StepChoices))]
    public async Task QueryAsyncStepChoiceKeepsOverlappingStepsWithinTarget(double days, int target)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var now = At(0);
        var from = now.AddTicks(-(long)Math.Round(days * TimeSpan.TicksPerDay));
        var policy = TimeSeriesSamples.Policy(TimeSpan.FromSeconds(1));

        await TimeSeriesSamples.WriteAsync(store, string.Empty, string.Empty, TestContext.CancellationToken, TimeSeriesSamples.Metric(_mem.Source, _mem.Name, from.AddMinutes(1), 1));
        await FoldAsync(store, now, policy);

        // Act
        var result = await RunAsync(store, now, from, now, target, policy, _mem);

        // Assert
        AssertAxis(result, from, now, target);
        Assert.IsTrue(from >= now.AddDays(-365) || result.Step >= TimeSpan.FromHours(1), "a step under an hour needs the 5-minute rollups");
    }

    /// <summary>
    /// A range that starts before the raw period is never raw, even where few raw values remain.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    public async Task QueryAsyncRangeBeforeRawPeriodIsNeverRaw()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var now = At(0);
        var old = now.AddDays(-40);

        await TimeSeriesSamples.WriteAsync(store,
                                           string.Empty,
                                           string.Empty,
                                           TestContext.CancellationToken,
                                           TimeSeriesSamples.Metric(_mem.Source, _mem.Name, old, 1),
                                           TimeSeriesSamples.Metric(_mem.Source, _mem.Name, old.AddSeconds(30), 2),
                                           TimeSeriesSamples.Metric(_mem.Source, _mem.Name, old.AddSeconds(60), 3));
        await FoldAsync(store, now, null);

        // Act
        var result = await RunAsync(store, now, now.AddDays(-41), now.AddDays(-39), 1000, null, _mem);

        // Assert
        Assert.AreEqual(TimeSeriesResolution.Step, result.Resolution, "three times would fit the target, but the range starts before the raw period");
        Assert.AreEqual(TimeSpan.FromMinutes(5), result.Step, "576 steps of five minutes fit");
        Assert.HasCount(1, result.Lines, "one line");
        Assert.AreEqual(3, result.Lines[0].Points.Single(point => point.State == PointState.Value).Count, "the three values are one 5-minute step");
    }

    /// <summary>
    /// A range that starts before the 5-minute period uses steps of at least an hour from the hourly rollups.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    public async Task QueryAsyncRangeBeforeFiveMinutePeriodUsesHourSteps()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var now = At(0);
        var from = now.AddDays(-400);

        await TimeSeriesSamples.WriteAsync(store,
                                           string.Empty,
                                           string.Empty,
                                           TestContext.CancellationToken,
                                           TimeSeriesSamples.Metric(_mem.Source, _mem.Name, from.AddMinutes(30), 1),
                                           TimeSeriesSamples.Metric(_mem.Source, _mem.Name, from.AddMinutes(90), 2));
        await FoldAsync(store, now, null);

        // Act
        var result = await RunAsync(store, now, from, from.AddDays(1), 2000, null, _mem);

        // Assert
        Assert.AreEqual(TimeSpan.FromHours(1), result.Step, "five minutes would fit the target, but the 5-minute rollups no longer reach this range");
        Assert.HasCount(24, result.Times, "24 hourly steps");
        Assert.IsFalse(result.StartsBeforeRollups, "the hourly period still reaches the range");
        Assert.AreEqual("VV" + new string('G', 22), States(result.Lines[0]), "the two hours with a sample hold values");
    }

    /// <summary>
    /// A range before the hourly period has empty lines, and the result says so.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    public async Task QueryAsyncRangeBeforeHourlyPeriodFlagsEmptyLines()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var now = At(0);

        await TimeSeriesSamples.WriteAsync(store, string.Empty, string.Empty, TestContext.CancellationToken, TimeSeriesSamples.Metric(_mem.Source, _mem.Name, now.AddDays(-1100), 1));
        await FoldAsync(store, now, null);

        // Act
        var result = await RunAsync(store, now, now.AddDays(-1101), now.AddDays(-1099), 1000, null, _mem);

        // Assert
        Assert.IsTrue(result.StartsBeforeRollups, "the range starts before the hourly period");
        Assert.AreEqual(TimeSeriesResolution.Step, result.Resolution, "no raw fallback");
        Assert.IsEmpty(result.Lines, "no rollup holds the range");
    }

    /// <summary>
    /// A series gives one line per stored label set, in the byte order of the label text.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    public async Task QueryAsyncLabelledSeriesHasOneLinePerLabelSet()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var cpu = new SeriesKey("system", "cpu_percent");

        foreach (var mode in new[] { "user", "system", "idle" })
        {
            await WriteSamplesAsync(store, cpu, At(0), TimeSpan.FromSeconds(30), 3, TimeSeriesSamples.Mode(mode));
        }

        // Act
        var result = await RunAsync(store, At(1), At(0), At(0.5), 1000, null, cpu);

        // Assert
        Assert.AreEqual(TimeSeriesResolution.Raw, result.Resolution, "three times");
        Assert.AreSequenceEqual(["{\"mode\":\"idle\"}", "{\"mode\":\"system\"}", "{\"mode\":\"user\"}"], result.Lines.Select(line => line.Labels).ToList(), "one line per label set in label order");
        Assert.IsTrue(result.Lines.All(line => line.Name == "cpu_percent" && States(line) == "VVV"), "each line holds a value at each time");
    }

    /// <summary>
    /// A series without values has no line.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    public async Task QueryAsyncSeriesWithoutValuesHasNoLine()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteSamplesAsync(store, _mem, At(0), TimeSpan.FromSeconds(30), 3);

        // Act
        var result = await RunAsync(store, At(1), At(0), At(0.5), 1000, null, _mem, new SeriesKey("system", "ghost"));

        // Assert
        Assert.HasCount(1, result.Lines, "only the series with values has a line");
        Assert.AreEqual("mem_available_bytes", result.Lines[0].Name, "the line of the series with values");
        Assert.IsEmpty(result.CutSeries, "a series without values is not a cut series");
    }

    /// <summary>
    /// Series that share their capture times count each time once: 1,200 samples on 600 times stay raw at a target of 1,000.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    public async Task QueryAsyncSeriesOfOneCollectorSharingTimesStayRaw()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteSamplesAsync(store, _mem, At(0), TimeSpan.FromSeconds(30), 600);
        await WriteSamplesAsync(store, _free, At(0), TimeSpan.FromSeconds(30), 600);

        // Act
        var result = await RunAsync(store, At(6), At(0), At(5), 1000, null, _mem, _free);

        // Assert
        Assert.AreEqual(TimeSeriesResolution.Raw, result.Resolution, "the union of the times is 600, the sum would be 1,200");
        Assert.HasCount(600, result.Times, "the union");
        Assert.HasCount(2, result.Lines, "both lines");
        Assert.IsTrue(result.Lines.All(line => States(line) == new string('V', 600)), "both lines hold a value at every time");
    }

    /// <summary>
    /// A series listed twice is read and returned once.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC8")]
    public async Task QueryAsyncSeriesListedTwiceIsUsedOnce()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var reader = new CountingTimeSeriesReader(store);

        await WriteSamplesAsync(store, _mem, At(0), TimeSpan.FromSeconds(30), 3);

        // Act
        var result = await RunAsync(reader, At(1), At(0), At(0.5), 1000, null, _mem, new SeriesKey(_mem.Source, _mem.Name));

        // Assert
        Assert.HasCount(1, result.Lines, "one line");
        Assert.AreEqual(1, reader.CountOf(nameof(ITimeSeriesReader.RawValuesAsync)), "one raw read for the series");
    }

    /// <summary>
    /// The times of a step result are the multiples of the step since the epoch, from the step that holds the start of the range to the one that holds its end.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC2")]
    public async Task QueryAsyncStepTimesAreEpochMultiplesFromStepHoldingFrom()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await ArrangeMinuteSeriesAsync(store);

        // Act
        var result = await RunAsync(store, At(3), At(0).AddMinutes(7), At(0).AddMinutes(37), 1000, StepPolicy(), _mem);

        // Assert
        Assert.AreEqual(TimeSpan.FromMinutes(5), result.Step, "seven steps of five minutes fit");
        Assert.AreSequenceEqual(Enumerable.Range(1, 7).Select(index => At(0).AddMinutes(5 * index)).ToList(), result.Times, "10:05 holds the start 10:07, 10:35 holds the end 10:37");
    }

    /// <summary>
    /// A step point covers its whole step, also the part before the start and after the end of the range.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC2")]
    public async Task QueryAsyncStepPointCoversWholeStepBeyondRange()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await ArrangeMinuteSeriesAsync(store);

        // Act
        var result = await RunAsync(store, At(3), At(0).AddMinutes(7), At(0).AddMinutes(37), 1000, StepPolicy(), _mem);

        // Assert
        var first = result.Lines[0].Points[0];
        var last = result.Lines[0].Points[^1];

        Assert.AreEqual(5, first.Count, "the step 10:05 holds the values of 10:05 to 10:09, also those before the start of the range");
        Assert.AreEqual(5, first.Minimum, "minimum of the first step");
        Assert.AreEqual(9, first.Maximum, "maximum of the first step");
        Assert.AreEqual(7, first.Average, "average of the first step");
        Assert.AreEqual(9, first.Last, "last value of the first step");
        Assert.AreEqual(At(0).AddMinutes(9), first.LastAt, "time of the last value");
        Assert.AreEqual(5, last.Count, "the step 10:35 holds the values of 10:35 to 10:39, also those after the end of the range");
        Assert.AreEqual(37, last.Average, "average of the last step");
    }

    /// <summary>
    /// A step merges its 5-minute buckets weighted by their counts, not as the mean of the bucket means.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC2")]
    public async Task QueryAsyncStepPointMergesBucketsCountWeighted()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await TimeSeriesSamples.WriteAsync(store,
                                           string.Empty,
                                           string.Empty,
                                           TestContext.CancellationToken,
                                           TimeSeriesSamples.Metric(_mem.Source, _mem.Name, At(0).AddSeconds(10), 1),
                                           TimeSeriesSamples.Metric(_mem.Source, _mem.Name, At(0).AddSeconds(70), 2),
                                           TimeSeriesSamples.Metric(_mem.Source, _mem.Name, At(0).AddSeconds(130), 3),
                                           TimeSeriesSamples.Metric(_mem.Source, _mem.Name, At(0).AddMinutes(6), 10));
        await FoldAsync(store, At(1), StepPolicy());

        // Act
        var result = await RunAsync(store, At(1), At(0), At(0).AddMinutes(20), 2, StepPolicy(), _mem);

        // Assert
        Assert.AreEqual(TimeSpan.FromMinutes(10), result.Step, "four steps of five minutes exceed the target of two");
        Assert.AreEqual("VG", States(result.Lines[0]), "the second step has no bucket");

        var point = result.Lines[0].Points[0];

        Assert.AreEqual(4, point.Count, "count of both buckets");
        Assert.AreEqual(1, point.Minimum, "minimum of both buckets");
        Assert.AreEqual(10, point.Maximum, "maximum of both buckets");
        Assert.AreEqual(4, point.Average, "sum over count: 16 / 4, not the mean of the means 6");
        Assert.AreEqual(10, point.Last, "the last value of the later bucket");
        Assert.AreEqual(At(0).AddMinutes(6), point.LastAt, "its time");
    }

    /// <summary>
    /// The raw axis is the union of the capture times of all lines.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC2")]
    public async Task QueryAsyncRawAxisIsUnionOfLineTimes()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await ArrangeTwoLinesAsync(store);

        // Act
        var result = await RunAsync(store, At(1), At(0), At(0.5), 1000, null, _mem, _rss);

        // Assert
        Assert.AreEqual(TimeSeriesResolution.Raw, result.Resolution, "six times");
        Assert.AreEqual("0,10,30,40,60,70", Offsets(result), "the times of both lines, ascending");
    }

    /// <summary>
    /// Two samples of one line at one time are one point.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC2")]
    public async Task QueryAsyncRawDuplicateTimeMergesIntoOnePoint()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await TimeSeriesSamples.WriteAsync(store,
                                           string.Empty,
                                           string.Empty,
                                           TestContext.CancellationToken,
                                           TimeSeriesSamples.Metric(_mem.Source, _mem.Name, At(0), 2),
                                           TimeSeriesSamples.Metric(_mem.Source, _mem.Name, At(0), 6),
                                           TimeSeriesSamples.Metric(_mem.Source, _mem.Name, At(0).AddSeconds(30), 1));

        // Act
        var result = await RunAsync(store, At(1), At(0), At(0.5), 1000, null, _mem);

        // Assert
        Assert.HasCount(2, result.Times, "two distinct times");

        var point = result.Lines[0].Points[0];

        Assert.AreEqual(2, point.Count, "both samples");
        Assert.AreEqual(2, point.Minimum, "minimum");
        Assert.AreEqual(4, point.Average, "average");
        Assert.AreEqual(6, point.Maximum, "maximum");
        Assert.AreEqual(6, point.Last, "the value of the later record");
    }

    /// <summary>
    /// A step without a folded value is a gap.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC3")]
    public async Task QueryAsyncEmptyStepIsGap()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await TimeSeriesSamples.WriteAsync(store,
                                           string.Empty,
                                           string.Empty,
                                           TestContext.CancellationToken,
                                           TimeSeriesSamples.Metric(_mem.Source, _mem.Name, At(0).AddSeconds(30), 1),
                                           TimeSeriesSamples.Metric(_mem.Source, _mem.Name, At(0).AddMinutes(12), 2));
        await FoldAsync(store, At(1), StepPolicy());

        // Act
        var result = await RunAsync(store, At(1), At(0), At(0).AddMinutes(15), 1000, StepPolicy(), _mem);

        // Assert
        Assert.AreEqual(TimeSpan.FromMinutes(5), result.Step, "three steps");
        Assert.AreEqual("VGV", States(result.Lines[0]), "the middle step has no value");
    }

    /// <summary>
    /// At a time only another line was sampled, the line is between samples, so a chart draws through it.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC3")]
    public async Task QueryAsyncRawTimeOfOtherLineIsBetweenSamples()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await ArrangeTwoLinesAsync(store);

        // Act
        var result = await RunAsync(store, At(1), At(0), At(0.5), 1000, null, _mem, _rss);

        // Assert
        Assert.AreEqual("VBVBVB", States(result.Lines[0]), "the first line is sampled at the 1st, 3rd and 5th time");
        Assert.AreEqual("BVBVBV", States(result.Lines[1]), "the second line at the others");
        Assert.AreEqual("system", result.Lines[0].Source, "lines in series order");
        Assert.AreEqual("agent", result.Lines[1].Source, "second series");
        Assert.AreEqual(20, result.Lines[1].Points[3].Last, "the value of the second line at its second sample");
    }

    /// <summary>
    /// A time inside a returned gap that concerns the line breaks it, and a time is added at the start of the gap where the axis has none.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC3")]
    public async Task QueryAsyncRawConcerningGapBreaksLineAndAddsTime()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await TimeSeriesSamples.WriteAsync(store,
                                           string.Empty,
                                           string.Empty,
                                           TestContext.CancellationToken,
                                           TimeSeriesSamples.Metric(_mem.Source, _mem.Name, At(0), 1),
                                           TimeSeriesSamples.Metric(_mem.Source, _mem.Name, At(0).AddMinutes(3), 2));
        await InsertGapAsync(directory.Path, "agent-1", GapState.Final, 1, 2, 1);

        // Act
        var result = await RunAsync(store, At(1), At(0), At(0).AddMinutes(10), 1000, null, _mem);

        // Assert
        Assert.AreEqual(TimeSeriesResolution.Raw, result.Resolution, "two times");
        Assert.AreSequenceEqual([At(0), At(0).AddMinutes(1), At(0).AddMinutes(3)], result.Times, "the start of the gap is added to the axis");
        Assert.AreEqual("VGV", States(result.Lines[0]), "the line breaks over the gap");
        Assert.HasCount(1, result.Gaps, "the gap is listed");
    }

    /// <summary>
    /// A gap of another collector lists but does not break the line.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC3")]
    public async Task QueryAsyncRawGapOfOtherCollectorDoesNotBreakLine()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await ArrangeThreeMinuteSamplesAsync(store, _mem);
        await TimeSeriesSamples.WriteAsync(store, "agent-1", FirstBoot, TestContext.CancellationToken, FilesystemGap());

        // Act
        var result = await RunAsync(store, At(1), At(0), At(0).AddMinutes(10), 1000, null, _mem);

        // Assert
        Assert.AreSequenceEqual([At(0), At(0).AddMinutes(3)], result.Times, "no time is added");
        Assert.AreEqual("VV", States(result.Lines[0]), "the filesystem gap does not concern a memory line");
        Assert.HasCount(1, result.Gaps, "the gap is still listed");
        Assert.AreEqual("filesystem", result.Gaps[0].Collector, "its collector");
    }

    /// <summary>
    /// A gap of a collector breaks the lines it writes and leaves the others between samples at the added time.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC3")]
    [TestCategory("AC4")]
    public async Task QueryAsyncRawCollectorGapBreaksOnlyItsLines()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await ArrangeThreeMinuteSamplesAsync(store, _mem);
        await ArrangeThreeMinuteSamplesAsync(store, _filesystem);
        await TimeSeriesSamples.WriteAsync(store, "agent-1", FirstBoot, TestContext.CancellationToken, FilesystemGap());

        // Act
        var result = await RunAsync(store, At(1), At(0), At(0).AddMinutes(10), 1000, null, _mem, _filesystem);

        // Assert
        Assert.AreSequenceEqual([At(0), At(0).AddMinutes(1), At(0).AddMinutes(3)], result.Times, "one time is added at the start of the gap");
        Assert.AreEqual("VBV", States(result.Lines[0]), "the memory line is between samples at the added time");
        Assert.AreEqual("VGV", States(result.Lines[1]), "the filesystem line breaks");
    }

    /// <summary>
    /// A whole UTC-aligned five-minute span without a value between two values breaks the line, and a time is added at the start of the span.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC3")]
    public async Task QueryAsyncRawEmptyAlignedFiveMinutesBreaksLine()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await TimeSeriesSamples.WriteAsync(store,
                                           string.Empty,
                                           string.Empty,
                                           TestContext.CancellationToken,
                                           TimeSeriesSamples.Metric(_mem.Source, _mem.Name, At(0).AddSeconds(10), 1),
                                           TimeSeriesSamples.Metric(_mem.Source, _mem.Name, At(0).AddMinutes(11), 2));

        // Act
        var result = await RunAsync(store, At(1), At(0), At(0).AddMinutes(30), 1000, null, _mem);

        // Assert
        Assert.AreSequenceEqual([At(0).AddSeconds(10), At(0).AddMinutes(5), At(0).AddMinutes(11)], result.Times, "the start of the empty span 10:05 to 10:10 is added");
        Assert.AreEqual("VGV", States(result.Lines[0]), "the line breaks over the empty span");
        Assert.IsEmpty(result.Gaps, "no recorded gap");
    }

    /// <summary>
    /// Two values four minutes apart, with no whole aligned five-minute span between them, stay connected.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC3")]
    public async Task QueryAsyncRawFourMinuteSpacingStaysConnected()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await TimeSeriesSamples.WriteAsync(store,
                                           string.Empty,
                                           string.Empty,
                                           TestContext.CancellationToken,
                                           TimeSeriesSamples.Metric(_mem.Source, _mem.Name, At(0).AddMinutes(1), 1),
                                           TimeSeriesSamples.Metric(_mem.Source, _mem.Name, At(0).AddMinutes(5), 2));

        // Act
        var result = await RunAsync(store, At(1), At(0), At(0).AddMinutes(30), 1000, null, _mem);

        // Assert
        Assert.HasCount(2, result.Times, "no time is added");
        Assert.AreEqual("VV", States(result.Lines[0]), "the line stays connected");
    }

    /// <summary>
    /// One gap that breaks 64 lines in one interval adds one time, so the axis stays within 2 x target - 1 times.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC3")]
    [TestCategory("AC7")]
    public async Task QueryAsyncRawGapAcrossSixtyFourLinesAddsOneTime()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var cpu = new SeriesKey("system", "cpu_percent");

        for (var index = 0; index < 64; index++)
        {
            await TimeSeriesSamples.WriteAsync(store,
                                               string.Empty,
                                               string.Empty,
                                               TestContext.CancellationToken,
                                               TimeSeriesSamples.Metric(cpu.Source, cpu.Name, At(0), index, TimeSeriesSamples.Mode($"m{index:00}")),
                                               TimeSeriesSamples.Metric(cpu.Source, cpu.Name, At(0).AddMinutes(3), index, TimeSeriesSamples.Mode($"m{index:00}")));
        }

        await InsertGapAsync(directory.Path, "agent-1", GapState.Final, 1, 2, 1);

        // Act
        var result = await RunAsync(store, At(1), At(0), At(0).AddMinutes(10), 2, null, cpu);

        // Assert
        Assert.AreEqual(TimeSeriesResolution.Raw, result.Resolution, "two times fit the target of two");
        Assert.HasCount(3, result.Times, "one time added, 2 x 2 - 1");
        Assert.HasCount(64, result.Lines, "all 64 lines");
        Assert.IsEmpty(result.CutSeries, "64 lines are not cut");
        Assert.IsTrue(result.Lines.All(line => States(line) == "VGV"), "every line breaks over the gap");
    }

    /// <summary>
    /// The gaps of the range are the open and final ones of every agent, never a closed one, with span, cause, state and kind; a time gap stored open reads final when no sequence gap lies in it.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    public async Task QueryAsyncListsOpenAndFinalGapsWithoutClosed()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteSamplesAsync(store, _mem, At(0), TimeSpan.FromSeconds(30), 1);
        await InsertGapAsync(directory.Path, "agent-1", GapState.Open, 10, 20, 1);
        await InsertGapAsync(directory.Path, "agent-2", GapState.Final, 30, 40, 3);
        await InsertGapAsync(directory.Path, "agent-1", GapState.Closed, 15, 16, 5);
        await TimeSeriesSamples.InsertGapAsync(directory.Path,
                                               new SampleGap
                                               {
                                                   AgentId = "agent-3",
                                                   Kind = GapKind.Time,
                                                   FromSeq = 5,
                                                   ToSeq = 9,
                                                   From = At(0).AddMinutes(50),
                                                   To = At(0).AddMinutes(55)
                                               },
                                               TestContext.CancellationToken);

        // Act
        var result = await RunAsync(store, At(2), At(0), At(1), 1000, null, _mem);

        // Assert
        Assert.AreSequenceEqual(["agent-1|sequence|open|10-20|no_data|", "agent-2|sequence|final|30-40|no_data|", "agent-3|time|final|50-55|no_data|"], result.Gaps.Select(DescribeGap).ToList(), "open and final gaps by start; the closed gap is left out");
        Assert.IsNull(result.GapsCompleteUntil, "the list is complete");
    }

    /// <summary>
    /// An agent gap record is a final gap with its span, cause and collector; its end is clipped to the query time.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    public async Task QueryAsyncAgentGapRecordIsFinal()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteSamplesAsync(store, _mem, At(0), TimeSpan.FromSeconds(30), 1);
        await TimeSeriesSamples.WriteAsync(store,
                                           "agent-1",
                                           FirstBoot,
                                           TestContext.CancellationToken,
                                           TimeSeriesSamples.GapRecord(At(0).AddMinutes(25), At(0).AddMinutes(20), At(0).AddMinutes(25), GapCause.NoData, "systemd", 0, 0),
                                           TimeSeriesSamples.GapRecord(At(0).AddMinutes(55), At(0).AddMinutes(50), At(3), GapCause.NoData, string.Empty, 0, 0));

        // Act
        var result = await RunAsync(store, At(2), At(0), At(1), 1000, null, _mem);

        // Assert
        Assert.AreSequenceEqual(["agent-1|record|final|20-25|no_data|systemd", "agent-1|record|final|50-120|no_data|"], result.Gaps.Select(DescribeGap).ToList(), "records are final; the second span ends at the query time");
    }

    /// <summary>
    /// A gap record whose numbers lie inside a returned sequence gap of its agent and cause is listed once, as the sequence gap; other records stay.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    public async Task QueryAsyncGapRecordInsideSequenceGapOfSameCauseListedOnce()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteSamplesAsync(store, _mem, At(0), TimeSpan.FromSeconds(30), 1);
        await TimeSeriesSamples.InsertGapAsync(directory.Path,
                                               new SampleGap
                                               {
                                                   AgentId = "agent-1",
                                                   Cause = GapCause.SequenceMissing,
                                                   State = GapState.Final,
                                                   FirstSeq = 10,
                                                   LastSeq = 19,
                                                   From = At(0).AddMinutes(10),
                                                   To = At(0).AddMinutes(30)
                                               },
                                               TestContext.CancellationToken);
        await TimeSeriesSamples.WriteAsync(store,
                                           "agent-1",
                                           FirstBoot,
                                           TestContext.CancellationToken,
                                           TimeSeriesSamples.GapRecord(At(0).AddMinutes(13), At(0).AddMinutes(12), At(0).AddMinutes(13), GapCause.SequenceMissing, string.Empty, 12, 15),
                                           TimeSeriesSamples.GapRecord(At(0).AddMinutes(15), At(0).AddMinutes(14), At(0).AddMinutes(15), GapCause.SpoolDropped, string.Empty, 12, 15),
                                           TimeSeriesSamples.GapRecord(At(0).AddMinutes(41), At(0).AddMinutes(40), At(0).AddMinutes(41), GapCause.SequenceMissing, string.Empty, 20, 25));
        await TimeSeriesSamples.WriteAsync(store, "agent-2", SecondBoot, TestContext.CancellationToken, TimeSeriesSamples.GapRecord(At(0).AddMinutes(13), At(0).AddMinutes(12), At(0).AddMinutes(13), GapCause.SequenceMissing, string.Empty, 12, 15));

        // Act
        var result = await RunAsync(store, At(2), At(0), At(1), 1000, null, _mem);

        // Assert
        Assert.AreSequenceEqual([
                                    "agent-1|sequence|final|10-30|sequence_missing|",
                                    "agent-2|record|final|12-13|sequence_missing|",
                                    "agent-1|record|final|14-15|spool_dropped|",
                                    "agent-1|record|final|40-41|sequence_missing|"
                                ],
                                result.Gaps.Select(DescribeGap).ToList(),
                                "the record inside the sequence gap of its agent and cause is not listed again; a record of another cause, another agent or other numbers is");
    }

    /// <summary>
    /// Gap records are found by capture time from one hour before the range to seven days after it.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    public async Task QueryAsyncGapRecordFoundOnlyInsideCaptureWindow()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var end = At(1);

        await WriteSamplesAsync(store, _mem, At(0), TimeSpan.FromSeconds(30), 1);
        await TimeSeriesSamples.WriteAsync(store,
                                           "agent-1",
                                           FirstBoot,
                                           TestContext.CancellationToken,
                                           TimeSeriesSamples.GapRecord(At(-0.5), At(0).AddMinutes(-35), At(0).AddMinutes(5), GapCause.NoData, string.Empty, 0, 0),
                                           TimeSeriesSamples.GapRecord(At(-2), At(-3), At(0).AddMinutes(5), GapCause.NoData, "early", 0, 0),
                                           TimeSeriesSamples.GapRecord(end.AddDays(6), At(0).AddMinutes(10), end.AddDays(6), GapCause.NoData, "inside", 0, 0),
                                           TimeSeriesSamples.GapRecord(end.AddDays(8), At(0).AddMinutes(10), end.AddDays(8), GapCause.NoData, "late", 0, 0));

        // Act
        var result = await RunAsync(store, At(0).AddDays(30), At(0), end, 1000, null, _mem);

        // Assert
        Assert.AreSequenceEqual(["agent-1|record|final|-35-5|no_data|", $"agent-1|record|final|10-{(end.AddDays(6) - At(0)).TotalMinutes:0}|no_data|inside"], result.Gaps.Select(DescribeGap).ToList(), "a record captured half an hour before and one captured six days after the range are found, one captured two hours before and one eight days after are not");
    }

    /// <summary>
    /// Every kill is one marker per boot ID, kernel time and process ID, also when its record is stored twice; a kill without a kernel time is one marker per record.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task QueryAsyncOomKillsOncePerIdentity()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteSamplesAsync(store, _mem, At(0), TimeSpan.FromSeconds(30), 1);
        await TimeSeriesSamples.WriteAsync(store,
                                           string.Empty,
                                           FirstBoot,
                                           TestContext.CancellationToken,
                                           TimeSeriesSamples.Kill(At(1), "php-fpm", 4711, FirstBoot, 1000, "killed"),
                                           TimeSeriesSamples.Kill(At(1), "php-fpm", 4711, FirstBoot, 1000, "killed"),
                                           TimeSeriesSamples.Kill(At(1.2), "x", 7, FirstBoot, null, "killed"),
                                           TimeSeriesSamples.Kill(At(1.4), "x", 7, FirstBoot, null, "killed"),
                                           TimeSeriesSamples.Kill(At(2), "mysqld", 99, FirstBoot, 2000, "killed"),
                                           TimeSeriesSamples.Kill(At(3), "php-fpm", 4711, FirstBoot, 3000, "killed"),
                                           TimeSeriesSamples.Kill(At(3.5), "php-fpm", 4711, SecondBoot, 1000, "killed"));

        // Act
        var result = await RunAsync(store, At(5), At(0), At(4), 1000, null, _mem);

        // Assert
        var kills = result.Markers.Where(marker => marker.Kind == MarkerKind.OomKill).ToList();

        Assert.AreSequenceEqual([
                                    "1|php-fpm|4711|" + FirstBoot,
                                    "1.2|x|7|" + FirstBoot,
                                    "1.4|x|7|" + FirstBoot,
                                    "2|mysqld|99|" + FirstBoot,
                                    "3|php-fpm|4711|" + FirstBoot,
                                    "3.5|php-fpm|4711|" + SecondBoot
                                ],
                                kills.Select(marker => $"{Hours(marker.At)}|{marker.Text}|{marker.Pid}|{marker.BootId}").ToList(),
                                "the stored-twice kill is one marker; a kill that differs in kernel time or boot ID is another; a kill without a kernel time is one per record");
    }

    /// <summary>
    /// A boot is one marker per boot ID, at its first record.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task QueryAsyncBootsOncePerBootId()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteSamplesAsync(store, _mem, At(0), TimeSpan.FromSeconds(30), 1);
        await TimeSeriesSamples.WriteAsync(store,
                                           string.Empty,
                                           string.Empty,
                                           TestContext.CancellationToken,
                                           TimeSeriesSamples.Boot(At(1), FirstBoot),
                                           TimeSeriesSamples.Boot(At(2), FirstBoot),
                                           TimeSeriesSamples.Boot(At(3), SecondBoot));

        // Act
        var result = await RunAsync(store, At(5), At(0), At(4), 1000, null, _mem);

        // Assert
        Assert.AreSequenceEqual(["1|" + FirstBoot, "3|" + SecondBoot], result.Markers.Where(marker => marker.Kind == MarkerKind.Boot).Select(marker => $"{Hours(marker.At)}|{marker.BootId}").ToList(), "one boot marker per boot ID");
    }

    /// <summary>
    /// A restart is a rise of the restart counter, or a later or newly present enter time of a unit that was active, activating, reloading, deactivating or failed with no boot between; a re-record, a start from inactive, a falling counter and a boot are none.
    /// </summary>
    /// <param name="previousState">The state of the previous record</param>
    /// <param name="previousEnter">The enter time of the previous record in hours after the range start, NaN for none</param>
    /// <param name="previousRestarts">The counter of the previous record</param>
    /// <param name="previousBoot">The boot (1 or 2) of the previous record</param>
    /// <param name="enter">The enter time of the record in hours, NaN for none</param>
    /// <param name="restarts">The counter of the record</param>
    /// <param name="boot">The boot (1 or 2) of the record</param>
    /// <param name="bootBetween">Whether a boot record is stored between the two records</param>
    /// <param name="previousBeforeRange">Whether the previous record lies before the range</param>
    /// <param name="expected">The number of restart markers</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    [DataRow("active", -5.0, 0, 1, -5.0, 1, 1, false, false, 1)]
    [DataRow("active", -5.0, 0, 1, 1.5, 0, 1, false, false, 1)]
    [DataRow("failed", -5.0, 0, 1, 1.5, 0, 1, false, false, 1)]
    [DataRow("activating", -5.0, 0, 1, 1.5, 0, 1, false, false, 1)]
    [DataRow("reloading", -5.0, 0, 1, 1.5, 0, 1, false, false, 1)]
    [DataRow("deactivating", -5.0, 0, 1, 1.5, 0, 1, false, false, 1)]
    [DataRow("inactive", -5.0, 0, 1, 1.5, 0, 1, false, false, 0)]
    [DataRow("failed", double.NaN, 0, 1, 1.5, 0, 1, false, false, 1)]
    [DataRow("active", double.NaN, 0, 1, 1.5, 0, 1, false, false, 1)]
    [DataRow("inactive", double.NaN, 0, 1, 1.5, 0, 1, false, false, 0)]
    [DataRow("active", -5.0, 0, 1, double.NaN, 0, 1, false, false, 0)]
    [DataRow("active", -5.0, 0, 1, 1.5, 0, 2, false, false, 0)]
    [DataRow("active", -5.0, 0, 1, 1.5, 0, 1, true, false, 0)]
    [DataRow("active", -5.0, 0, 1, -5.0, 0, 1, false, false, 0)]
    [DataRow("active", -5.0, 5, 1, -5.0, 4, 1, false, false, 0)]
    [DataRow("active", -5.0, 0, 1, 1.5, 0, 1, false, true, 1)]
    [DataRow("active", -5.0, 0, 1, 1.5, 0, 2, false, true, 0)]
    public async Task QueryAsyncServiceRestartRules(string previousState, double previousEnter, int previousRestarts, int previousBoot, double enter, int restarts, int boot, bool bootBetween, bool previousBeforeRange, int expected)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteSamplesAsync(store, _mem, At(0), TimeSpan.FromSeconds(30), 1);
        await TimeSeriesSamples.WriteAsync(store, string.Empty, BootOf(previousBoot), TestContext.CancellationToken, TimeSeriesSamples.Service(Unit, previousState, EnterOf(previousEnter), (uint)previousRestarts, previousBeforeRange ? At(-1) : At(1)));

        if (bootBetween)
        {
            await TimeSeriesSamples.WriteAsync(store, string.Empty, BootOf(previousBoot), TestContext.CancellationToken, TimeSeriesSamples.Boot(At(1.5), "33333333-3333-3333-3333-333333333333"));
        }

        await TimeSeriesSamples.WriteAsync(store, string.Empty, BootOf(boot), TestContext.CancellationToken, TimeSeriesSamples.Service(Unit, "active", EnterOf(enter), (uint)restarts, At(2)));

        // Act
        var result = await RunAsync(store, At(12), At(0), At(10), 1000, null, _mem);

        // Assert
        var found = result.Markers.Where(marker => marker.Kind == MarkerKind.ServiceRestart).ToList();

        Assert.HasCount(expected, found, "the number of restart markers");
        Assert.IsTrue(found.All(marker => marker.Text == Unit), "the marker names the unit");
    }

    /// <summary>
    /// An enter time that cannot be parsed is no moved enter time.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task QueryAsyncServiceRestartIgnoresUnparseableEnterTime()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteSamplesAsync(store, _mem, At(0), TimeSpan.FromSeconds(30), 1);
        await TimeSeriesSamples.InsertRecordAsync(directory.Path, "service_state", At(1), ForgedState("2026-09-30T10:00:00+00:00"), TestContext.CancellationToken);
        await TimeSeriesSamples.InsertRecordAsync(directory.Path, "service_state", At(2), ForgedState("not a time"), TestContext.CancellationToken);

        // Act
        var result = await RunAsync(store, At(12), At(0), At(10), 1000, null, _mem);

        // Assert
        Assert.IsEmpty(result.Markers.Where(marker => marker.Kind == MarkerKind.ServiceRestart).ToList(), "a text that is no time is no moved enter time");
    }

    /// <summary>
    /// A previous record found among the 10,000 service records before the range makes the first record a restart; one beyond them does not.
    /// </summary>
    /// <param name="fillers">The number of records of another unit between the previous record and the range</param>
    /// <param name="expected">The number of restart markers</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    [DataRow(100, 1)]
    [DataRow(10000, 0)]
    public async Task QueryAsyncServiceRestartPreviousBeyondScanIsNoRestart(int fillers, int expected)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteSamplesAsync(store, _mem, At(0), TimeSpan.FromSeconds(30), 1);
        await TimeSeriesSamples.WriteAsync(store, string.Empty, FirstBoot, TestContext.CancellationToken, TimeSeriesSamples.Service(Unit, "active", At(-30), 0, At(-5)));
        await TimeSeriesSamples.InsertServiceStatesAsync(directory.Path, "filler.service", At(-4), fillers, TestContext.CancellationToken);
        await TimeSeriesSamples.WriteAsync(store, string.Empty, FirstBoot, TestContext.CancellationToken, TimeSeriesSamples.Service(Unit, "active", At(1.5), 0, At(2)));

        // Act
        var result = await RunAsync(store, At(12), At(0), At(10), 1000, null, _mem);

        // Assert
        Assert.AreEqual(expected, result.Markers.Count(marker => marker.Kind == MarkerKind.ServiceRestart && marker.Text == Unit), "the previous record counts only within the 10,000 most recent service records before the range");
    }

    /// <summary>
    /// A restart is timed at its new enter time when that lies in the range and not after its capture time, else at its capture time.
    /// </summary>
    /// <param name="enter">The new enter time in hours after the range start</param>
    /// <param name="restarts">The counter of the record</param>
    /// <param name="expected">The expected time of the marker in hours</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    [DataRow(1.5, 0, 1.5)]
    [DataRow(-3.0, 0, 2.0)]
    [DataRow(5.0, 0, 2.0)]
    [DataRow(20.0, 0, 2.0)]
    [DataRow(-5.0, 1, 2.0)]
    public async Task QueryAsyncRestartTime(double enter, int restarts, double expected)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteSamplesAsync(store, _mem, At(0), TimeSpan.FromSeconds(30), 1);
        await TimeSeriesSamples.WriteAsync(store, string.Empty, FirstBoot, TestContext.CancellationToken, TimeSeriesSamples.Service(Unit, "active", At(-5), 0, At(1)), TimeSeriesSamples.Service(Unit, "active", At(enter), (uint)restarts, At(2)));

        // Act
        var result = await RunAsync(store, At(12), At(0), At(10), 1000, null, _mem);

        // Assert
        var found = result.Markers.Where(marker => marker.Kind == MarkerKind.ServiceRestart).ToList();

        Assert.HasCount(1, found, "one restart");
        Assert.AreEqual(At(expected), found[0].At, "the time of the restart");
    }

    /// <summary>
    /// A backup run is a span from its start to its end, still running, or with an unknown end; forged times are clamped or dropped.
    /// </summary>
    /// <param name="scenario">The arranged runs</param>
    /// <param name="expected">The described markers</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    [DataRow("ended", "Ended|1|2|1.5")]
    [DataRow("startedDayBefore", "Ended|-20|2|1.5")]
    [DataRow("running", "Running|1|-|none")]
    [DataRow("unknownAfterBoot", "Unknown|1|-|none")]
    [DataRow("startWrittenTwice", "Running|1|-|none")]
    [DataRow("afterQueryTime", "Ended|1|12|12")]
    [DataRow("startAfterQueryTime", "")]
    public async Task QueryAsyncBackupRunSpans(string scenario, string expected)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteSamplesAsync(store, _mem, At(0), TimeSpan.FromSeconds(30), 1);
        await ArrangeBackupAsync(store, scenario);

        // Act
        var result = await RunAsync(store, At(12), At(0), At(10), 1000, null, _mem);

        // Assert
        Assert.AreEqual(expected, string.Join(";", result.Markers.Where(marker => marker.Kind == MarkerKind.BackupRun).Select(DescribeBackup)), "the backup markers: end state, start, end, last sighting in hours");
    }

    /// <summary>
    /// A backup marker carries its command and boot ID.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task QueryAsyncBackupRunMarkerCarriesCommandAndBootId()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteSamplesAsync(store, _mem, At(0), TimeSpan.FromSeconds(30), 1);
        await ArrangeBackupAsync(store, "ended");

        // Act
        var result = await RunAsync(store, At(12), At(0), At(10), 1000, null, _mem);

        // Assert
        var marker = result.Markers.Single(candidate => candidate.Kind == MarkerKind.BackupRun);

        Assert.AreEqual("pzstd", marker.Text, "the command");
        Assert.AreEqual(FirstBoot, marker.BootId, "the boot ID");
        Assert.AreEqual(BackupRunEnd.Ended, marker.End, "ended");
        Assert.AreEqual(At(2), marker.EndsAt, "the end");
        Assert.AreEqual(At(1.5), marker.LastSeenAt, "the last sighting");
    }

    /// <summary>
    /// Markers come in time order, a tie by kind.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task QueryAsyncMarkersInTimeOrder()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteSamplesAsync(store, _mem, At(0), TimeSpan.FromSeconds(30), 1);
        await TimeSeriesSamples.WriteAsync(store,
                                           string.Empty,
                                           FirstBoot,
                                           TestContext.CancellationToken,
                                           TimeSeriesSamples.Boot(At(3), SecondBoot),
                                           TimeSeriesSamples.Kill(At(3), "php-fpm", 4711, FirstBoot, 1000, "killed"),
                                           TimeSeriesSamples.Service(Unit, "active", At(-5), 0, At(0.2)),
                                           TimeSeriesSamples.Service(Unit, "active", At(1.5), 0, At(2)),
                                           TimeSeriesSamples.BackupEnd(At(0.7), FirstBoot, At(0.5), At(0.7), At(0.75)),
                                           TimeSeriesSamples.BackupStart(At(0.5), FirstBoot, At(0.5), "pzstd"));

        // Act
        var result = await RunAsync(store, At(5), At(0), At(4), 1000, null, _mem);

        // Assert
        Assert.AreSequenceEqual([MarkerKind.BackupRun, MarkerKind.ServiceRestart, MarkerKind.OomKill, MarkerKind.Boot], result.Markers.Select(marker => marker.Kind).ToList(), "backup 0.5 h, restart 1.5 h, kill and boot at 3 h");
        Assert.IsNull(result.MarkersCompleteUntil, "the list is complete");
    }

    /// <summary>
    /// Nothing after the query time is returned: no time, marker or gap.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC6")]
    public async Task QueryAsyncReturnsNothingAfterQueryTime()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await TimeSeriesSamples.WriteAsync(store,
                                           string.Empty,
                                           FirstBoot,
                                           TestContext.CancellationToken,
                                           TimeSeriesSamples.Metric(_mem.Source, _mem.Name, At(0).AddMinutes(10), 1),
                                           TimeSeriesSamples.Metric(_mem.Source, _mem.Name, At(2), 2),
                                           TimeSeriesSamples.Boot(At(2), SecondBoot),
                                           TimeSeriesSamples.Kill(At(0).AddMinutes(30), "php-fpm", 4711, FirstBoot, 1000, "killed"));
        await InsertGapAsync(directory.Path, "agent-1", GapState.Final, 20, 30, 1);
        await InsertGapAsync(directory.Path, "agent-1", GapState.Final, 120, 130, 3);

        // Act
        var result = await RunAsync(store, At(1), At(0), At(3), 1000, null, _mem);

        // Assert
        Assert.AreSequenceEqual([At(0).AddMinutes(10)], result.Times, "the sample after the query time is not returned");
        Assert.AreSequenceEqual([MarkerKind.OomKill], result.Markers.Select(marker => marker.Kind).ToList(), "the boot after the query time is not a marker");
        Assert.AreSequenceEqual(["agent-1|sequence|final|20-30|no_data|"], result.Gaps.Select(DescribeGap).ToList(), "the gap that starts after the query time is not listed");
    }

    /// <summary>
    /// A range that does not start before the query time gives an empty result.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC6")]
    public async Task QueryAsyncRangeStartingAtQueryTimeIsEmpty()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await TimeSeriesSamples.WriteAsync(store, string.Empty, FirstBoot, TestContext.CancellationToken, TimeSeriesSamples.Metric(_mem.Source, _mem.Name, At(1), 1), TimeSeriesSamples.Boot(At(1), SecondBoot));

        // Act
        var result = await RunAsync(store, At(0), At(0), At(1), 1000, null, _mem);

        // Assert
        Assert.IsEmpty(result.Times, "no time");
        Assert.IsEmpty(result.Lines, "no line");
        Assert.IsEmpty(result.Markers, "no marker");
        Assert.IsEmpty(result.Gaps, "no gap");
    }

    /// <summary>
    /// With a stalled fold the points are complete only up to the earliest capture time that is not folded.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC6")]
    public async Task QueryAsyncCompleteUntilIsEarliestUnfoldedCapture()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteSamplesAsync(store, _mem, At(0), TimeSpan.FromMinutes(1), 60);
        await FoldAsync(store, At(3), StepPolicy());
        await TimeSeriesSamples.WriteAsync(store,
                                           string.Empty,
                                           string.Empty,
                                           TestContext.CancellationToken,
                                           TimeSeriesSamples.Metric(_mem.Source, _mem.Name, At(0).AddMinutes(80), 1),
                                           TimeSeriesSamples.Metric(_mem.Source, _mem.Name, At(0).AddMinutes(70), 2));

        // Act
        var result = await RunAsync(store, At(3), At(0), At(0).AddMinutes(90), 1000, StepPolicy(), _mem);

        // Assert
        Assert.AreEqual(TimeSeriesResolution.Step, result.Resolution, "a step result");
        Assert.AreEqual(At(0).AddMinutes(70), result.CompleteUntil, "the earliest capture time among the values not folded yet");
    }

    /// <summary>
    /// When every value is folded the points are complete up to the query time.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC6")]
    public async Task QueryAsyncCompleteUntilIsQueryTimeWhenAllFolded()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteSamplesAsync(store, _mem, At(0), TimeSpan.FromMinutes(1), 60);
        await FoldAsync(store, At(3), StepPolicy());

        // Act
        var result = await RunAsync(store, At(3), At(0), At(4), 1000, StepPolicy(), _mem);

        // Assert
        Assert.AreEqual(At(3), result.CompleteUntil, "the query time, though the range ends later");
    }

    /// <summary>
    /// A raw result has no completeness time.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC6")]
    public async Task QueryAsyncRawHasNoCompleteUntil()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteSamplesAsync(store, _mem, At(0), TimeSpan.FromSeconds(30), 3);

        // Act
        var result = await RunAsync(store, At(1), At(0), At(0.5), 1000, null, _mem);

        // Assert
        Assert.AreEqual(TimeSeriesResolution.Raw, result.Resolution, "raw");
        Assert.IsNull(result.CompleteUntil, "raw values are read directly and are never behind a fold");
    }

    /// <summary>
    /// Over 64 lines the first 64 stay in series order, then in the UTF-8 byte order of the label text, and the series that lost lines is named.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC7")]
    public async Task QueryAsyncOverSixtyFourLinesCutsInSeriesThenUtf8ByteOrder()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var second = new SeriesKey("system", "load2");
        var secondLabels = Enumerable.Range(0, 23).Select(index => Labelled($"b{index:00}")).Concat([Labelled("\U0001F600"), Labelled("～")]).ToList();

        for (var index = 0; index < 40; index++)
        {
            await TimeSeriesSamples.InsertBucketAsync(directory.Path, "system", "load", Labelled($"a{index:00}"), At(0), 1, 1, 1, TestContext.CancellationToken);
        }

        foreach (var labels in secondLabels)
        {
            await TimeSeriesSamples.InsertBucketAsync(directory.Path, "system", "load2", labels, At(0), 1, 1, 1, TestContext.CancellationToken);
        }

        // Act
        var result = await RunAsync(store, At(3), At(0), At(0).AddMinutes(10), 1000, StepPolicy(), new SeriesKey("system", "load"), second);

        // Assert
        var expected = Enumerable.Range(0, 40).Select(index => $"load|{Labelled($"a{index:00}")}").Concat(Enumerable.Range(0, 23).Select(index => $"load2|{Labelled($"b{index:00}")}")).Concat([$"load2|{Labelled("～")}"]).ToList();

        Assert.AreSequenceEqual(expected, result.Lines.Select(line => $"{line.Name}|{line.Labels}").ToList(), "40 lines of the first series, 24 of the second in byte order: U+FF5E (three bytes) stays, U+1F600 (four bytes) is cut");
        Assert.AreSequenceEqual(["load2"], result.CutSeries.Select(key => key.Name).ToList(), "the series that lost a line");
    }

    /// <summary>
    /// Over 1,000 markers the earliest 1,000 are kept, with the time up to which the list is complete.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC7")]
    public async Task QueryAsyncOverThousandMarkersKeepsEarliestWithCompleteUntil()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteSamplesAsync(store, _mem, At(0), TimeSpan.FromSeconds(30), 1);
        await TimeSeriesSamples.WriteAsync(store, string.Empty, string.Empty, TestContext.CancellationToken, Enumerable.Range(0, 1100).Select(index => TimeSeriesSamples.Boot(At(0).AddSeconds(index), $"{index:x8}-0000-0000-0000-000000000000")).ToArray());

        // Act
        var result = await RunAsync(store, At(2), At(0), At(1), 1000, null, _mem);

        // Assert
        Assert.HasCount(TimeSeriesService.MaxMarkers, result.Markers, "the markers are cut at the cap");
        Assert.AreEqual(At(0).AddSeconds(999), result.Markers[^1].At, "the earliest 1,000 are kept");
        Assert.AreEqual(At(0).AddSeconds(1000), result.MarkersCompleteUntil, "the list is complete up to the first marker that was cut");
        Assert.IsNull(result.GapsCompleteUntil, "the gap list is complete");
    }

    /// <summary>
    /// Over 1,000 gaps the earliest 1,000 are kept, with the time up to which the list is complete.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC7")]
    public async Task QueryAsyncOverThousandGapsKeepsEarliestWithCompleteUntil()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteSamplesAsync(store, _mem, At(0), TimeSpan.FromSeconds(30), 1);
        await TimeSeriesSamples.InsertGapRowsAsync(directory.Path, 1100, At(0), TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(30), TestContext.CancellationToken);

        // Act
        var result = await RunAsync(store, At(72), At(0), At(48), 1000, null, _mem);

        // Assert
        Assert.HasCount(TimeSeriesService.MaxGaps, result.Gaps, "the gaps are cut at the cap");
        Assert.AreEqual(At(0).AddMinutes(999), result.Gaps[^1].From, "the earliest 1,000 are kept");
        Assert.AreEqual(At(0).AddMinutes(1000), result.GapsCompleteUntil, "the list is complete up to the start of the first gap that was cut");
        Assert.IsNull(result.MarkersCompleteUntil, "the marker list is complete");
    }

    /// <summary>
    /// When ten full pages of gap records are read, the gap list is complete only up to the last capture time read less the seven-day window.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC7")]
    public async Task QueryAsyncFullRecordPageBoundsCompleteUntil()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var end = At(0).AddDays(30);

        await WriteSamplesAsync(store, _mem, At(0), TimeSpan.FromSeconds(30), 1);
        await TimeSeriesSamples.InsertGapRecordsAsync(directory.Path, 10500, end.AddDays(6), TestContext.CancellationToken);

        // Act
        var result = await RunAsync(store, At(0).AddDays(40), At(0), end, 1000, null, _mem);

        // Assert
        Assert.IsEmpty(result.Gaps, "the spans of the records lie after the range");
        Assert.AreEqual(end.AddDays(6).AddSeconds(10009).AddDays(-7), result.GapsCompleteUntil, "the capture time of the last of ten pages of 1,001 records, less seven days");
    }

    /// <summary>
    /// Every event read asks for at most 1,001 rows, at most ten pages per kind, and the service reads at most 2,001 changes.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC7")]
    public async Task QueryAsyncEventReadsUseAtMostTenPagesOfOneThousandOne()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var reader = new CountingTimeSeriesReader(store);
        var end = At(0).AddDays(30);

        await WriteSamplesAsync(store, _mem, At(0), TimeSpan.FromSeconds(30), 1);
        await TimeSeriesSamples.InsertGapRecordsAsync(directory.Path, 10500, end.AddDays(6), TestContext.CancellationToken);

        // Act
        await RunAsync(reader, At(0).AddDays(40), At(0), end, 1000, null, _mem);

        // Assert
        Assert.AreEqual(10, reader.CountOf(nameof(ITimeSeriesReader.GapRecordsAsync)), "ten pages of gap records");

        foreach (var name in new[] { nameof(ITimeSeriesReader.GapRecordsAsync), nameof(ITimeSeriesReader.KernelEventsAsync), nameof(ITimeSeriesReader.BackupRunsAsync), nameof(ITimeSeriesReader.GapsInRangeAsync), nameof(ITimeSeriesReader.BootTimesAsync) })
        {
            Assert.IsLessThanOrEqualTo(10, reader.CountOf(name), $"{name}: at most ten pages");
            Assert.IsTrue(reader.LimitsOf(name).All(limit => limit is >= 1 and <= 1001), $"{name}: every page asks for at most 1,001 rows");
        }

        Assert.IsTrue(reader.LimitsOf(nameof(ITimeSeriesReader.ServiceChangesAsync)).All(limit => limit is >= 1 and <= 2001), "the service changes ask for at most 2,001 rows");
    }

    /// <summary>
    /// A query that breaks a rule is refused with an error that names it, before any store read.
    /// </summary>
    /// <param name="scenario">The broken rule</param>
    /// <param name="message">The expected error text</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC8")]
    [DataRow("noSeries", "store: invalid query: at least one series is required")]
    [DataRow("seventeenSeries", "store: invalid query: at most 16 series")]
    [DataRow("emptySource", "store: invalid query: source and name are required")]
    [DataRow("emptyName", "store: invalid query: source and name are required")]
    [DataRow("targetZero", "store: invalid query: target must be 1 to 2000")]
    [DataRow("targetOver", "store: invalid query: target must be 1 to 2000")]
    [DataRow("fromEqualsTo", "store: invalid query: from must be before to")]
    [DataRow("fromAfterTo", "store: invalid query: from must be before to")]
    [DataRow("fromBelowStorable", "store: invalid query: from and to must be within the storable range")]
    [DataRow("toAboveStorable", "store: invalid query: from and to must be within the storable range")]
    public async Task QueryAsyncInvalidQueryRefusedBeforeStoreRead(string scenario, string message)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var reader = new CountingTimeSeriesReader(store);
        var service = new TimeSeriesService(reader, RetentionSamples.Defaults(), new FakeTimeProvider(At(1)));

        // Act
        var exception = await Assert.ThrowsExactlyAsync<InvalidQueryException>(() => service.QueryAsync(InvalidQuery(scenario), TestContext.CancellationToken), "the query breaks a rule");

        // Assert
        Assert.AreEqual(message, exception.Message, "the error names the rule");
        Assert.IsEmpty(reader.Calls, "no store read before the refusal");
    }

    /// <summary>
    /// The edges of the storable range read without an error, because every derived bound is clamped.
    /// </summary>
    /// <param name="unlimited">Whether the policy keeps every period</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC8")]
    [DataRow(false)]
    [DataRow(true)]
    public async Task QueryAsyncStorableEdgesReadWithoutError(bool unlimited)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var policy = unlimited ? TimeSeriesSamples.Unlimited() : RetentionSamples.Defaults();

        // Act
        var result = await RunAsync(store, StorableTime.Max, StorableTime.Min, StorableTime.Max, 2000, policy, _mem);

        // Assert
        Assert.AreEqual(unlimited ? TimeSeriesResolution.Raw : TimeSeriesResolution.Step, result.Resolution, "unlimited periods try raw, the default ones do not reach back");
        Assert.AreEqual(unlimited == false, result.StartsBeforeRollups, "the range starts before a finite hourly period");
    }

    /// <summary>
    /// A cancelled token ends the query with a cancellation, before the first read and after a later one, and returns no result.
    /// </summary>
    /// <param name="afterFirstRead">Whether the token is cancelled after the first read</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC9")]
    [DataRow(false)]
    [DataRow(true)]
    public async Task QueryAsyncCancelledThrowsWithoutResult(bool afterFirstRead)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        using var source = new CancellationTokenSource();
        var reader = new CountingTimeSeriesReader(store);

        await WriteSamplesAsync(store, _mem, At(0), TimeSpan.FromSeconds(30), 3);

        if (afterFirstRead)
        {
            reader.AfterCall = _ => source.Cancel();
        }
        else
        {
            await source.CancelAsync();
        }

        var service = new TimeSeriesService(reader, RetentionSamples.Defaults(), new FakeTimeProvider(At(1)));
        var query = new TimeSeriesQuery
                    {
                        Series = [_mem],
                        From = At(0),
                        To = At(0.5),
                        Target = 1000
                    };

        // Act and assert
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.QueryAsync(query, source.Token), "the query is cancelled");
    }

    /// <summary>
    /// Gets an instant a number of hours after <see cref="Samples.Base"/>.
    /// </summary>
    /// <param name="hours">The hours</param>
    /// <returns>The instant</returns>
    private static DateTimeOffset At(double hours)
    {
        return Samples.Base.AddHours(hours);
    }

    /// <summary>
    /// Gets the hours after <see cref="Samples.Base"/> of an instant as text.
    /// </summary>
    /// <param name="instant">The instant</param>
    /// <returns>The hours</returns>
    private static string Hours(DateTimeOffset instant)
    {
        return (instant - Samples.Base).TotalHours.ToString("0.##", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Gets the minutes after <see cref="Samples.Base"/> of an instant as text.
    /// </summary>
    /// <param name="instant">The instant</param>
    /// <returns>The minutes</returns>
    private static string Minutes(DateTimeOffset instant)
    {
        return (instant - Samples.Base).TotalMinutes.ToString("0", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Creates the stored label text of one label <c>k</c>.
    /// </summary>
    /// <param name="value">The value</param>
    /// <returns>The text</returns>
    private static string Labelled(string value)
    {
        return $"{{\"k\":\"{value}\"}}";
    }

    /// <summary>
    /// Gets the policy of the default periods with a raw period so short that no query is raw.
    /// </summary>
    /// <returns>The policy</returns>
    private static RetentionPolicy StepPolicy()
    {
        return TimeSeriesSamples.Policy(TimeSpan.FromMinutes(1));
    }

    /// <summary>
    /// Gets the boot ID of a boot number.
    /// </summary>
    /// <param name="boot">The number, 1 or 2</param>
    /// <returns>The boot ID</returns>
    private static string BootOf(int boot)
    {
        return boot == 1 ? FirstBoot : SecondBoot;
    }

    /// <summary>
    /// Gets an enter time in hours after <see cref="Samples.Base"/>.
    /// </summary>
    /// <param name="hours">The hours, NaN for none</param>
    /// <returns>The instant, or <c>null</c></returns>
    private static DateTimeOffset? EnterOf(double hours)
    {
        return double.IsNaN(hours) ? null : At(hours);
    }

    /// <summary>
    /// Describes the states of the points of a line as one letter each: value, gap, between samples.
    /// </summary>
    /// <param name="line">The line</param>
    /// <returns>The letters</returns>
    private static string States(SeriesLine line)
    {
        return string.Concat(line.Points.Select(point => point.State switch
                                                         {
                                                             PointState.Value => 'V',
                                                             PointState.Gap => 'G',
                                                             _ => 'B'
                                                         }));
    }

    /// <summary>
    /// Describes the times of a result as seconds after <see cref="Samples.Base"/>.
    /// </summary>
    /// <param name="result">The result</param>
    /// <returns>The seconds, separated by commas</returns>
    private static string Offsets(TimeSeriesResult result)
    {
        return string.Join(",", result.Times.Select(time => (time - Samples.Base).TotalSeconds.ToString("0", CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// Describes a gap by agent, kind, state, span in minutes, cause and collector.
    /// </summary>
    /// <param name="gap">The gap</param>
    /// <returns>The description</returns>
    private static string DescribeGap(SeriesGap gap)
    {
        return $"{gap.AgentId}|{gap.Kind}|{gap.State}|{Minutes(gap.From)}-{Minutes(gap.To)}|{gap.Cause}|{gap.Collector}";
    }

    /// <summary>
    /// Describes a backup marker by end state, start, end and last sighting in hours.
    /// </summary>
    /// <param name="marker">The marker</param>
    /// <returns>The description</returns>
    private static string DescribeBackup(SeriesMarker marker)
    {
        var ends = marker.End == BackupRunEnd.Ended ? Hours(marker.EndsAt!.Value) : "-";
        var seen = marker.LastSeenAt is null ? "none" : Hours(marker.LastSeenAt.Value);

        return $"{marker.End}|{Hours(marker.At)}|{ends}|{seen}";
    }

    /// <summary>
    /// Creates the payload text of a forged service record with an enter time.
    /// </summary>
    /// <param name="enter">The enter time text</param>
    /// <returns>The payload</returns>
    private static string ForgedState(string enter)
    {
        return $"{{\"unit\":\"{Unit}\",\"load_state\":\"loaded\",\"active_state\":\"active\",\"sub_state\":\"running\",\"active_enter_at\":\"{enter}\",\"restarts\":0}}";
    }

    /// <summary>
    /// Creates the gap record of the filesystem collector between one and two minutes after <see cref="Samples.Base"/>.
    /// </summary>
    /// <returns>The record</returns>
    private static DataRecord FilesystemGap()
    {
        return TimeSeriesSamples.GapRecord(At(0).AddMinutes(2), At(0).AddMinutes(1), At(0).AddMinutes(2), GapCause.CollectorTimeout, "filesystem", 0, 0);
    }

    /// <summary>
    /// Creates the invalid query of a scenario.
    /// </summary>
    /// <param name="scenario">The broken rule</param>
    /// <returns>The query</returns>
    private static TimeSeriesQuery InvalidQuery(string scenario)
    {
        IReadOnlyList<SeriesKey> series = scenario switch
                                          {
                                              "noSeries" => [],
                                              "seventeenSeries" => Enumerable.Range(0, 17).Select(index => new SeriesKey("system", $"metric_{index}")).ToList(),
                                              "emptySource" => [new SeriesKey(string.Empty, "x")],
                                              "emptyName" => [new SeriesKey("system", string.Empty)],
                                              _ => [_mem]
                                          };

        return new TimeSeriesQuery
               {
                   Series = series,
                   From = scenario switch
                          {
                              "fromEqualsTo" => At(1),
                              "fromAfterTo" => At(2),
                              "fromBelowStorable" => DateTimeOffset.MinValue,
                              _ => At(0)
                          },
                   To = scenario switch
                        {
                            "toAboveStorable" => DateTimeOffset.MaxValue,
                            _ => At(1)
                        },
                   Target = scenario switch
                            {
                                "targetZero" => 0,
                                "targetOver" => 2001,
                                _ => 1000
                            }
               };
    }

    /// <summary>
    /// Checks the axis of a step result against the rules of the ladder.
    /// </summary>
    /// <param name="result">The result</param>
    /// <param name="from">The start of the range</param>
    /// <param name="end">The end of the range</param>
    /// <param name="target">The target</param>
    private static void AssertAxis(TimeSeriesResult result, DateTimeOffset from, DateTimeOffset end, int target)
    {
        Assert.AreEqual(TimeSeriesResolution.Step, result.Resolution, "the range is not raw");

        var step = result.Step!.Value;

        Assert.IsLessThanOrEqualTo(target, result.Times.Count, "at most the target steps");
        Assert.AreEqual(Overlapping(from, end, step), result.Times.Count, "one time per step that overlaps the range");
        Assert.IsTrue(_ladder.Contains(step) || (step > TimeSpan.FromDays(1) && step.Ticks % TimeSpan.FromDays(1).Ticks == 0), "a step of the ladder or whole days");
        Assert.IsTrue(result.Times.All(time => (time.UtcTicks - DateTime.UnixEpoch.Ticks) % step.Ticks == 0), "multiples of the step since the epoch");
        Assert.IsTrue(result.Times[0] <= from && from < result.Times[0] + step, "the first step holds the start");
        Assert.IsTrue(result.Times[^1] < end && end <= result.Times[^1] + step, "the last step holds the end");
        Assert.IsTrue(_ladder.Where(smaller => smaller < step).All(smaller => Overlapping(from, end, smaller) > target), "no smaller step of the ladder fits the target");
    }

    /// <summary>
    /// Counts the steps of a width that overlap a range.
    /// </summary>
    /// <param name="from">The start of the range</param>
    /// <param name="end">The end of the range</param>
    /// <param name="step">The width of a step</param>
    /// <returns>The number of steps</returns>
    private static long Overlapping(DateTimeOffset from, DateTimeOffset end, TimeSpan step)
    {
        var first = (from.UtcTicks - DateTime.UnixEpoch.Ticks) / step.Ticks;
        var last = (end.UtcTicks - 1 - DateTime.UnixEpoch.Ticks) / step.Ticks;

        return last - first + 1;
    }

    /// <summary>
    /// Creates the start record of a backup run.
    /// </summary>
    /// <param name="capturedHours">The capture time in hours after the start of the test day</param>
    /// <param name="startedHours">The start of the run in hours</param>
    /// <returns>The record</returns>
    private static DataRecord Start(double capturedHours, double startedHours)
    {
        return TimeSeriesSamples.BackupStart(At(capturedHours), FirstBoot, At(startedHours), "pzstd");
    }

    /// <summary>
    /// Creates the end record of a backup run.
    /// </summary>
    /// <param name="startedHours">The start of the run in hours</param>
    /// <param name="capturedHours">The capture time in hours</param>
    /// <param name="goneHours">The time the run was found gone in hours</param>
    /// <param name="seenHours">The last sighting in hours</param>
    /// <returns>The record</returns>
    private static DataRecord End(double startedHours, double capturedHours, double goneHours, double seenHours)
    {
        return TimeSeriesSamples.BackupEnd(At(capturedHours), FirstBoot, At(startedHours), At(seenHours), At(goneHours));
    }

    /// <summary>
    /// Runs a query against a reader at a query time.
    /// </summary>
    /// <param name="reader">The reader</param>
    /// <param name="now">The query time</param>
    /// <param name="from">The start of the range</param>
    /// <param name="to">The end of the range</param>
    /// <param name="target">The target</param>
    /// <param name="policy">The policy, or <c>null</c> for the default periods</param>
    /// <param name="series">The series</param>
    /// <returns>A task that returns the result</returns>
    private Task<TimeSeriesResult> RunAsync(ITimeSeriesReader reader, DateTimeOffset now, DateTimeOffset from, DateTimeOffset to, int target, RetentionPolicy? policy, params SeriesKey[] series)
    {
        var service = new TimeSeriesService(reader, policy ?? RetentionSamples.Defaults(), new FakeTimeProvider(now));

        return service.QueryAsync(new TimeSeriesQuery
                                  {
                                      Series = series,
                                      From = from,
                                      To = to,
                                      Target = target
                                  },
                                  TestContext.CancellationToken);
    }

    /// <summary>
    /// Folds every stored value into the rollups.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="now">The time of the fold</param>
    /// <param name="policy">The policy, or <c>null</c> for the default periods</param>
    /// <returns>A task that completes when the values are folded</returns>
    private async Task FoldAsync(SqliteStore store, DateTimeOffset now, RetentionPolicy? policy)
    {
        await RetentionSamples.FoldAllAsync(store, policy ?? RetentionSamples.Defaults(), now, TestContext.CancellationToken);
    }

    /// <summary>
    /// Writes samples of a series, one per step, whose value is their index.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="key">The series</param>
    /// <param name="first">The time of the first sample</param>
    /// <param name="step">The distance of the samples</param>
    /// <param name="count">The number of samples</param>
    /// <param name="labels">The labels, or <c>null</c> for none</param>
    /// <returns>A task that completes when the samples are stored</returns>
    private async Task WriteSamplesAsync(SqliteStore store, SeriesKey key, DateTimeOffset first, TimeSpan step, int count, Dictionary<string, string>? labels = null)
    {
        await TimeSeriesSamples.WriteAsync(store, string.Empty, string.Empty, TestContext.CancellationToken, Enumerable.Range(0, count).Select(index => TimeSeriesSamples.Metric(key.Source, key.Name, first + (step * index), index, labels)).ToArray());
    }

    /// <summary>
    /// Writes and folds one sample per minute of the first two hours of the test day, whose value is the minute.
    /// </summary>
    /// <param name="store">The store</param>
    /// <returns>A task that completes when the samples are folded</returns>
    private async Task ArrangeMinuteSeriesAsync(SqliteStore store)
    {
        await WriteSamplesAsync(store, _mem, At(0), TimeSpan.FromMinutes(1), 120);
        await FoldAsync(store, At(3), StepPolicy());
    }

    /// <summary>
    /// Writes the samples of two lines at different times: the first at 0, 30 and 60 seconds, the second at 10, 40 and 70 seconds.
    /// </summary>
    /// <param name="store">The store</param>
    /// <returns>A task that completes when the samples are stored</returns>
    private async Task ArrangeTwoLinesAsync(SqliteStore store)
    {
        await TimeSeriesSamples.WriteAsync(store,
                                           string.Empty,
                                           string.Empty,
                                           TestContext.CancellationToken,
                                           TimeSeriesSamples.Metric(_mem.Source, _mem.Name, At(0), 1),
                                           TimeSeriesSamples.Metric(_mem.Source, _mem.Name, At(0).AddSeconds(30), 2),
                                           TimeSeriesSamples.Metric(_mem.Source, _mem.Name, At(0).AddSeconds(60), 3),
                                           TimeSeriesSamples.Metric(_rss.Source, _rss.Name, At(0).AddSeconds(10), 10),
                                           TimeSeriesSamples.Metric(_rss.Source, _rss.Name, At(0).AddSeconds(40), 20),
                                           TimeSeriesSamples.Metric(_rss.Source, _rss.Name, At(0).AddSeconds(70), 30));
    }

    /// <summary>
    /// Writes two samples of a series three minutes apart.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="key">The series</param>
    /// <returns>A task that completes when the samples are stored</returns>
    private async Task ArrangeThreeMinuteSamplesAsync(SqliteStore store, SeriesKey key)
    {
        await TimeSeriesSamples.WriteAsync(store,
                                           string.Empty,
                                           string.Empty,
                                           TestContext.CancellationToken,
                                           TimeSeriesSamples.Metric(key.Source, key.Name, At(0), 1),
                                           TimeSeriesSamples.Metric(key.Source, key.Name, At(0).AddMinutes(3), 2));
    }

    /// <summary>
    /// Inserts a sequence gap row that starts and ends a number of minutes after <see cref="Samples.Base"/>.
    /// </summary>
    /// <param name="directory">The store directory</param>
    /// <param name="agentId">The agent ID</param>
    /// <param name="state">The stored state</param>
    /// <param name="fromMinutes">The start of the span in minutes</param>
    /// <param name="toMinutes">The end of the span in minutes</param>
    /// <param name="firstSeq">The first missing number; the last is one more</param>
    /// <returns>A task that completes when the row is stored</returns>
    private async Task InsertGapAsync(string directory, string agentId, string state, int fromMinutes, int toMinutes, long firstSeq)
    {
        await TimeSeriesSamples.InsertGapAsync(directory,
                                               new SampleGap
                                               {
                                                   AgentId = agentId,
                                                   State = state,
                                                   FirstSeq = firstSeq,
                                                   LastSeq = firstSeq + 1,
                                                   From = At(0).AddMinutes(fromMinutes),
                                                   To = At(0).AddMinutes(toMinutes)
                                               },
                                               TestContext.CancellationToken);
    }

    /// <summary>
    /// Writes the records of a backup scenario, for a query at 12 hours over the first ten hours of the test day.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="scenario">The scenario</param>
    /// <returns>A task that completes when the records are stored</returns>
    private async Task ArrangeBackupAsync(SqliteStore store, string scenario)
    {
        DataRecord[] records = scenario switch
                               {
                                   "ended" => [Start(1, 1), End(1, 1.5, 2, 1.5)],
                                   "startedDayBefore" => [Start(-20, -20), End(-20, 1.5, 2, 1.5)],
                                   "running" => [Start(1, 1)],
                                   "unknownAfterBoot" => [Start(1, 1), TimeSeriesSamples.Boot(At(3), SecondBoot)],
                                   "startWrittenTwice" => [Start(1, 1), Start(1, 1)],
                                   "afterQueryTime" => [Start(1, 1), End(1, 9, 20, 15)],
                                   _ => [Start(9, 20)]
                               };

        await TimeSeriesSamples.WriteAsync(store, string.Empty, FirstBoot, TestContext.CancellationToken, records);
    }

    #endregion // Methods
}