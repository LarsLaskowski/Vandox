using System.Globalization;

using Microsoft.Data.Sqlite;

using Vandox.Core.Model;

namespace Vandox.Storage.Tests;

/// <summary>
/// Tests for the time-series reads of <see cref="SqliteStore"/>
/// </summary>
[TestClass]
public class SqliteStoreTimeSeriesTests
{
    #region Constants

    private const string Source = TimeSeriesSamples.MetricSource;

    private const string LimitRefusal = "store: invalid query: limit must be 1 to 1001";

    private const string ServiceLimitRefusal = "store: invalid query: limit must be 1 to 2001";

    private const string FirstBoot = TimelineSamples.BootId;

    private const string SecondBoot = TimelineSamples.OtherBootId;

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// A raw read returns one line per stored label set with the empty and the missing labels as one set, merges the samples of one line at one time and keeps to the series and the range.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    [TestCategory("AC2")]
    public async Task RawValuesAsyncReturnsOneLinePerLabelSetAndMergesSamplesOfOneTime()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var user = TimeSeriesSamples.Mode("user");

        await TimeSeriesSamples.WriteAsync(store,
                                           string.Empty,
                                           string.Empty,
                                           TestContext.CancellationToken,
                                           TimeSeriesSamples.Metric(Source, "cpu", Samples.Base.AddSeconds(-1), 99),
                                           TimeSeriesSamples.Metric(Source, "cpu", Samples.Base, 1),
                                           TimeSeriesSamples.Metric(Source, "cpu", Samples.Base.AddSeconds(30), 2, []),
                                           TimeSeriesSamples.Metric(Source, "cpu", Samples.Base, 10, user),
                                           TimeSeriesSamples.Metric(Source, "cpu", Samples.Base, 30, user),
                                           TimeSeriesSamples.Metric(Source, "cpu", Samples.Base.AddSeconds(30), 20, user),
                                           TimeSeriesSamples.Metric(Source, "cpu", Samples.Base.AddSeconds(60), 5, TimeSeriesSamples.Mode("system")),
                                           TimeSeriesSamples.Metric(Source, "cpu", Samples.Base.AddSeconds(90), 99),
                                           TimeSeriesSamples.Metric(Source, "other", Samples.Base, 99),
                                           TimeSeriesSamples.Metric("agent", "cpu", Samples.Base, 99));

        // Act
        var page = await store.RawValuesAsync(RawQuery("cpu", 100, 64), TestContext.CancellationToken);

        // Assert
        var lines = page.Lines.ToDictionary(line => line.Labels);

        Assert.HasCount(3, lines, "one line per label set, the empty and the missing labels together");
        Assert.IsFalse(page.TimesExceeded, "four distinct times are below the limit");
        Assert.IsFalse(page.LinesCut, "three lines are below the limit");
        Assert.AreEqual("0s x1, 30s x1", TimeSeriesSamples.Describe(lines[string.Empty]), "the line without labels: from is inside the range, to is not");
        Assert.AreEqual("0s x2, 30s x1", TimeSeriesSamples.Describe(lines["{\"mode\":\"user\"}"]), "the two samples of one time are one point");
        Assert.AreEqual("60s x1", TimeSeriesSamples.Describe(lines["{\"mode\":\"system\"}"]), "the third line has one point");

        var merged = lines["{\"mode\":\"user\"}"].Points[0];

        Assert.AreEqual(10, merged.Minimum, "minimum of the merged samples");
        Assert.AreEqual(20, merged.Average, "average of the merged samples");
        Assert.AreEqual(30, merged.Maximum, "maximum of the merged samples");
        Assert.AreEqual(30, merged.Last, "the last value is the one of the higher record ID");
        Assert.AreEqual(Samples.Base, merged.LastAt, "the time of the last sample");
        Assert.AreEqual(Samples.Base, merged.At, "the time of the point");
    }

    /// <summary>
    /// A raw read that meets one distinct time more than the limit stops there and says so.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    public async Task RawValuesAsyncStopsAfterOneTimeOverMaxTimes()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await TimeSeriesSamples.WriteAsync(store, string.Empty, string.Empty, TestContext.CancellationToken, Enumerable.Range(0, 10).Select(index => TimeSeriesSamples.Metric(Source, "cpu", Samples.Base.AddSeconds(index * 30), index)).ToArray());

        // Act
        var page = await store.RawValuesAsync(RawQuery("cpu", 3, 64, 3600), TestContext.CancellationToken);

        // Assert
        var times = page.Lines.SelectMany(line => line.Points).Select(point => (point.At - Samples.Base).TotalSeconds).Distinct().Order().ToList();

        Assert.IsTrue(page.TimesExceeded, "ten times exceed a limit of three");
        Assert.IsLessThanOrEqualTo(4, times.Count, "the read stops after one time more than the limit");
        Assert.IsGreaterThanOrEqualTo(3, times.Count, "the first times up to the limit are read");
        Assert.AreEqual(0, times[0], "the read starts at the earliest time");
        Assert.AreEqual(60, times[2], "the times come in capture order");
    }

    /// <summary>
    /// Lines that share their times count each time once, so two lines on three times stay within a limit of three.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    public async Task RawValuesAsyncCountsTimesOfAllLinesOnceAndStaysWithinMaxTimes()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var records = new List<DataRecord>();

        for (var index = 0; index < 3; index++)
        {
            records.Add(TimeSeriesSamples.Metric(Source, "cpu", Samples.Base.AddSeconds(index * 30), index, TimeSeriesSamples.Mode("user")));
            records.Add(TimeSeriesSamples.Metric(Source, "cpu", Samples.Base.AddSeconds(index * 30), index, TimeSeriesSamples.Mode("system")));
        }

        await TimeSeriesSamples.WriteAsync(store, string.Empty, string.Empty, TestContext.CancellationToken, records.ToArray());

        // Act
        var page = await store.RawValuesAsync(RawQuery("cpu", 3, 64), TestContext.CancellationToken);

        // Assert
        Assert.IsFalse(page.TimesExceeded, "three distinct times are not more than three");
        Assert.HasCount(2, page.Lines, "both lines are returned");
        Assert.IsTrue(page.Lines.All(line => line.Points.Count == 3), "each line holds all three times");
    }

    /// <summary>
    /// A raw read cuts the lines beyond its limit and says so, and keeps all lines at the limit.
    /// </summary>
    /// <param name="maxLines">The line limit</param>
    /// <param name="cut">Whether the lines are cut</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    [DataRow(2, true)]
    [DataRow(4, false)]
    public async Task RawValuesAsyncCutsLinesBeyondMaxLines(int maxLines, bool cut)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await TimeSeriesSamples.WriteAsync(store,
                                           string.Empty,
                                           string.Empty,
                                           TestContext.CancellationToken,
                                           Enumerable.Range(0, 4).Select(index => TimeSeriesSamples.Metric(Source, "cpu", Samples.Base.AddSeconds(index), index, TimeSeriesSamples.Mode($"mode{index}"))).ToArray());

        // Act
        var page = await store.RawValuesAsync(RawQuery("cpu", 100, maxLines), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(cut, page.LinesCut, "the flag tells that lines were cut");
        Assert.IsGreaterThanOrEqualTo(maxLines, page.Lines.Count, "at least the lines up to the limit are returned");
        Assert.IsLessThanOrEqualTo(cut ? maxLines + 1 : maxLines, page.Lines.Count, "at most one line more than the limit is read");
    }

    /// <summary>
    /// The statement shape of the raw read streams the values by capture time over the series index and sorts only the labels of equal times (verified fact 11).
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC1")]
    public async Task RawValuesAsyncQueryPlanStreamsByCaptureTime()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await TimeSeriesSamples.WriteAsync(store,
                                           string.Empty,
                                           string.Empty,
                                           TestContext.CancellationToken,
                                           TimeSeriesSamples.Metric(Source, "cpu", Samples.Base.AddSeconds(30), 2, TimeSeriesSamples.Mode("user")),
                                           TimeSeriesSamples.Metric(Source, "cpu", Samples.Base, 1, TimeSeriesSamples.Mode("user")));

        // Act
        var page = await store.RawValuesAsync(RawQuery("cpu", 100, 64), TestContext.CancellationToken);
        var plan = await PlanAsync(directory.Path);

        // Assert
        Assert.AreEqual("0s x1, 30s x1", TimeSeriesSamples.Describe(page.Lines[0]), "the points come in capture order");
        Assert.Contains("metrics_source_name_time", string.Join(" | ", plan), "the series index is used");
        Assert.DoesNotContain("USE TEMP B-TREE FOR ORDER BY", plan, $"the whole order is not sorted in a temporary tree: {string.Join(" | ", plan)}");
    }

    /// <summary>
    /// A step read merges the 5-minute buckets of a step with minimum, maximum, count, the sum over the count and the latest last value.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC2")]
    public async Task RollupStepsAsyncMergesMinMaxCountAndLatestLast()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await TimeSeriesSamples.WriteAsync(store,
                                           string.Empty,
                                           string.Empty,
                                           TestContext.CancellationToken,
                                           TimeSeriesSamples.Metric(Source, "load", Samples.Base.AddMinutes(-5), 100),
                                           TimeSeriesSamples.Metric(Source, "load", Samples.Base.AddSeconds(10), 4),
                                           TimeSeriesSamples.Metric(Source, "load", Samples.Base.AddSeconds(20), 9),
                                           TimeSeriesSamples.Metric(Source, "load", Samples.Base.AddMinutes(6), 1),
                                           TimeSeriesSamples.Metric(Source, "load", Samples.Base.AddMinutes(6), 2),
                                           TimeSeriesSamples.Metric(Source, "load", Samples.Base.AddMinutes(12), 7),
                                           TimeSeriesSamples.Metric(Source, "load", Samples.Base.AddMinutes(25), 50));
        await RetentionSamples.FoldAllAsync(store, RetentionSamples.Defaults(), Samples.Base.AddHours(3), TestContext.CancellationToken);

        // Act
        var page = await store.RollupStepsAsync(StepQuery(RollupTier.FiveMinutes, "load", Samples.Base, Samples.Base.AddMinutes(20), TimeSpan.FromMinutes(10)), TestContext.CancellationToken);

        // Assert
        Assert.HasCount(1, page.Lines, "one line without labels");
        Assert.AreEqual(string.Empty, page.Lines[0].Labels, "the labels of the line");
        Assert.HasCount(2, page.Lines[0].Points, "two steps hold buckets");

        var first = page.Lines[0].Points[0];
        var second = page.Lines[0].Points[1];

        Assert.AreEqual(Samples.Base, first.At, "the first step starts at its multiple of ten minutes");
        Assert.AreEqual(1, first.Minimum, "minimum of the four values");
        Assert.AreEqual(9, first.Maximum, "maximum of the four values");
        Assert.AreEqual(4, first.Count, "count of the four values");
        Assert.AreEqual(4, first.Average, "sum over count: (4 + 9 + 1 + 2) / 4");
        Assert.AreEqual(2, first.Last, "the last value is the one of the latest time and the higher record ID");
        Assert.AreEqual(Samples.Base.AddMinutes(6), first.LastAt, "the time of the last value");
        Assert.AreEqual(Samples.Base.AddMinutes(10), second.At, "the second step");
        Assert.AreEqual(1, second.Count, "one value in the second step");
        Assert.AreEqual(7, second.Average, "the value of the second step");
        Assert.AreEqual(Samples.Base.AddMinutes(12), second.LastAt, "the time of the value");
    }

    /// <summary>
    /// A step read of the hourly tier merges hour buckets into steps of one and of two hours and leaves out steps without a bucket.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC2")]
    public async Task RollupStepsAsyncReadsHourlyTierIntoOneAndTwoHourSteps()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await TimeSeriesSamples.WriteAsync(store,
                                           string.Empty,
                                           string.Empty,
                                           TestContext.CancellationToken,
                                           TimeSeriesSamples.Metric(Source, "load", Samples.Base.AddHours(-1), 5),
                                           TimeSeriesSamples.Metric(Source, "load", Samples.Base.AddSeconds(10), 4),
                                           TimeSeriesSamples.Metric(Source, "load", Samples.Base.AddMinutes(50), 6),
                                           TimeSeriesSamples.Metric(Source, "load", Samples.Base.AddMinutes(90), 2),
                                           TimeSeriesSamples.Metric(Source, "load", Samples.Base.AddMinutes(100), 8));
        await RetentionSamples.FoldAllAsync(store, RetentionSamples.Defaults(), Samples.Base.AddHours(3), TestContext.CancellationToken);

        // Act
        var hours = await store.RollupStepsAsync(StepQuery(RollupTier.Hour, "load", Samples.Base, Samples.Base.AddHours(4), TimeSpan.FromHours(1)), TestContext.CancellationToken);
        var pairs = await store.RollupStepsAsync(StepQuery(RollupTier.Hour, "load", Samples.Base, Samples.Base.AddHours(4), TimeSpan.FromHours(2)), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual("0s x2, 3600s x2", TimeSeriesSamples.Describe(hours.Lines[0]), "two hour buckets are two steps; the hour with no value is left out");
        Assert.HasCount(1, pairs.Lines[0].Points, "the two hours of the first step are one step, the second step holds no bucket");

        var pair = pairs.Lines[0].Points[0];

        Assert.AreEqual(Samples.Base, pair.At, "the step starts at its multiple of two hours");
        Assert.AreEqual(4, pair.Count, "count of the four values of both hours");
        Assert.AreEqual(2, pair.Minimum, "minimum over both hours");
        Assert.AreEqual(8, pair.Maximum, "maximum over both hours");
        Assert.AreEqual(5.0, pair.Average, "sum over count: 20 / 4");
        Assert.AreEqual(8, pair.Last, "the last value of the later hour");
        Assert.AreEqual(Samples.Base.AddMinutes(100), pair.LastAt, "the time of the last value");
    }

    /// <summary>
    /// A merged sum beyond the finite range is clamped as the fold does, so the average stays finite.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC2")]
    public async Task RollupStepsAsyncClampsSumToFiniteRange()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await TimeSeriesSamples.InsertBucketAsync(directory.Path, string.Empty, Samples.Base, double.MaxValue, 1, double.MaxValue, TestContext.CancellationToken);
        await TimeSeriesSamples.InsertBucketAsync(directory.Path, string.Empty, Samples.Base.AddMinutes(5), double.MaxValue, 1, double.MaxValue, TestContext.CancellationToken);
        await TimeSeriesSamples.InsertBucketAsync(directory.Path, string.Empty, Samples.Base.AddMinutes(10), -double.MaxValue, 1, -double.MaxValue, TestContext.CancellationToken);
        await TimeSeriesSamples.InsertBucketAsync(directory.Path, string.Empty, Samples.Base.AddMinutes(15), -double.MaxValue, 1, -double.MaxValue, TestContext.CancellationToken);

        // Act
        var page = await store.RollupStepsAsync(StepQuery(RollupTier.FiveMinutes, "load", Samples.Base, Samples.Base.AddMinutes(20), TimeSpan.FromMinutes(10)), TestContext.CancellationToken);

        // Assert
        Assert.HasCount(2, page.Lines[0].Points, "two steps");
        Assert.AreEqual(double.MaxValue / 2, page.Lines[0].Points[0].Average, "the positive sum is clamped to the largest number before it is divided");
        Assert.AreEqual(-double.MaxValue / 2, page.Lines[0].Points[1].Average, "the negative sum is clamped to the smallest number before it is divided");
        Assert.AreEqual(2, page.Lines[0].Points[0].Count, "count of the merged buckets");
    }

    /// <summary>
    /// Lines come in the UTF-8 byte order of the stored label text, which differs from the order of UTF-16 code units for characters above U+FFFF.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC2")]
    [TestCategory("AC7")]
    public async Task RollupStepsAsyncReturnsLinesInUtf8ByteOrderOfLabels()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var labels = new[] { Labelled("\U0001F600"), Labelled("～"), Labelled("a"), string.Empty, Labelled("B") };

        foreach (var text in labels)
        {
            await TimeSeriesSamples.InsertBucketAsync(directory.Path, text, Samples.Base, 1, 1, 1, TestContext.CancellationToken);
        }

        // Act
        var page = await store.RollupStepsAsync(StepQuery(RollupTier.FiveMinutes, "load", Samples.Base, Samples.Base.AddMinutes(5), TimeSpan.FromMinutes(5), 64), TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual([string.Empty, Labelled("B"), Labelled("a"), Labelled("～"), Labelled("\U0001F600")], page.Lines.Select(line => line.Labels).ToList(), "byte order: empty, upper case, lower case, U+FF5E (three bytes), U+1F600 (four bytes)");
        Assert.IsFalse(page.LinesCut, "five lines are below the limit");
    }

    /// <summary>
    /// A step read keeps the first lines in label order and says that lines were cut.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC2")]
    [TestCategory("AC7")]
    public async Task RollupStepsAsyncCutsLinesBeyondMaxLines()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        foreach (var text in new[] { Labelled("d"), Labelled("b"), Labelled("c"), Labelled("a") })
        {
            await TimeSeriesSamples.InsertBucketAsync(directory.Path, text, Samples.Base, 1, 1, 1, TestContext.CancellationToken);
        }

        // Act
        var page = await store.RollupStepsAsync(StepQuery(RollupTier.FiveMinutes, "load", Samples.Base, Samples.Base.AddMinutes(5), TimeSpan.FromMinutes(5), 2), TestContext.CancellationToken);

        // Assert
        Assert.IsTrue(page.LinesCut, "four lines are more than two");
        Assert.IsGreaterThanOrEqualTo(2, page.Lines.Count, "the lines up to the limit are returned");
        Assert.IsLessThanOrEqualTo(3, page.Lines.Count, "at most one line more than the limit is read");
        Assert.AreEqual(Labelled("a"), page.Lines[0].Labels, "the first line in label order");
        Assert.AreEqual(Labelled("b"), page.Lines[1].Labels, "the second line in label order");
    }

    /// <summary>
    /// A label set with no bucket in the range is no line.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC2")]
    public async Task RollupStepsAsyncReturnsNoLineForLabelSetWithoutBucketInRange()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await TimeSeriesSamples.InsertBucketAsync(directory.Path, Labelled("outside"), Samples.Base.AddHours(1), 1, 1, 1, TestContext.CancellationToken);
        await TimeSeriesSamples.InsertBucketAsync(directory.Path, Labelled("inside"), Samples.Base, 1, 1, 1, TestContext.CancellationToken);

        // Act
        var page = await store.RollupStepsAsync(StepQuery(RollupTier.FiveMinutes, "load", Samples.Base, Samples.Base.AddMinutes(5), TimeSpan.FromMinutes(5)), TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual([Labelled("inside")], page.Lines.Select(line => line.Labels).ToList(), "only the label set with a bucket in the range is a line");
        Assert.IsFalse(page.LinesCut, "nothing was cut");
    }

    /// <summary>
    /// The scan of the label sets of a series examines at most 1,024 of them, so a set behind them is not found (spec N2).
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC2")]
    public async Task RollupStepsAsyncExaminesAtMostThousandTwentyFourLabelSets()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await TimeSeriesSamples.InsertLabelSetsAsync(directory.Path, 1100, Samples.Base.AddHours(-1), TestContext.CancellationToken);
        await TimeSeriesSamples.InsertBucketAsync(directory.Path, "{\"set\":\"9999\"}", Samples.Base, 1, 1, 1, TestContext.CancellationToken);

        // Act
        var page = await store.RollupStepsAsync(StepQuery(RollupTier.FiveMinutes, "load", Samples.Base, Samples.Base.AddMinutes(5), TimeSpan.FromMinutes(5)), TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(page.Lines, "1,100 label sets without a bucket in the range come first; the scan ends after 1,024 of them");
    }

    /// <summary>
    /// The gap rows of a range are the open and the final ones of every agent that overlap it, in order of their start, and never the closed ones.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    public async Task GapsInRangeAsyncReturnsOverlappingOpenAndFinalOfAllAgents()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await InsertSequenceGapAsync(directory.Path, "agent-1", GapState.Open, 0, 11, 1);
        await InsertSequenceGapAsync(directory.Path, "agent-2", GapState.Final, 12, 13, 2);
        await InsertSequenceGapAsync(directory.Path, "agent-1", GapState.Closed, 14, 15, 3);
        await InsertSequenceGapAsync(directory.Path, "agent-1", GapState.Open, 0, 10, 4);
        await InsertSequenceGapAsync(directory.Path, "agent-2", GapState.Open, 20, 25, 5);
        await InsertSequenceGapAsync(directory.Path, "agent-1", GapState.Open, 19, 30, 6);
        await InsertSequenceGapAsync(directory.Path, "agent-1", GapState.Final, 15, 16, 7);
        await InsertSequenceGapAsync(directory.Path, "agent-2", GapState.Final, 15, 17, 8);
        await InsertSequenceGapAsync(directory.Path, "agent-2", GapState.Final, 30, 40, 9);

        // Act
        var gaps = await store.GapsInRangeAsync(TimeSeriesSamples.Range(Samples.Base.AddMinutes(10), Samples.Base.AddMinutes(20)), TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual(["agent-1 open 0-11 first 1", "agent-2 final 12-13 first 2", "agent-1 final 15-16 first 7", "agent-2 final 15-17 first 8", "agent-1 open 19-30 first 6"],
                                gaps.Select(gap => $"{gap.AgentId} {gap.State} {Minutes(gap.From)}-{Minutes(gap.To)} first {gap.FirstSeq}").ToList(),
                                "overlapping gaps by start, then ID; a gap ending at the start or starting at the end of the range, a closed gap and gaps outside are left out");
        Assert.IsTrue(gaps.All(gap => gap.Kind == GapKind.Sequence && gap.Id > 0), "kind and ID are read");
    }

    /// <summary>
    /// The range read limits the gaps it returns to the earliest ones.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    public async Task GapsInRangeAsyncHonoursLimitInOrderOfStart()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await InsertSequenceGapAsync(directory.Path, "agent-1", GapState.Final, 5, 6, 3);
        await InsertSequenceGapAsync(directory.Path, "agent-1", GapState.Final, 1, 2, 1);
        await InsertSequenceGapAsync(directory.Path, "agent-1", GapState.Final, 3, 4, 2);

        // Act
        var gaps = await store.GapsInRangeAsync(TimeSeriesSamples.Range(Samples.Base, Samples.Base.AddHours(1), 2), TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual([1UL, 2UL], gaps.Select(gap => gap.FirstSeq!.Value).ToList(), "the two earliest gaps");
    }

    /// <summary>
    /// A time gap stored open reads final when no open sequence gap of its agent lies between its neighbouring records, and open when one does.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    public async Task GapsInRangeAsyncReportsDerivedTimeGapState()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await TimeSeriesSamples.InsertGapAsync(directory.Path,
                                               new SampleGap
                                               {
                                                   AgentId = "agent-1",
                                                   Kind = GapKind.Time,
                                                   FromSeq = 10,
                                                   ToSeq = 20,
                                                   From = Samples.Base,
                                                   To = Samples.Base.AddMinutes(5)
                                               },
                                               TestContext.CancellationToken);
        await TimeSeriesSamples.InsertGapAsync(directory.Path,
                                               new SampleGap
                                               {
                                                   AgentId = "agent-1",
                                                   FirstSeq = 12,
                                                   LastSeq = 15,
                                                   From = Samples.Base.AddMinutes(1),
                                                   To = Samples.Base.AddMinutes(2)
                                               },
                                               TestContext.CancellationToken);
        await TimeSeriesSamples.InsertGapAsync(directory.Path,
                                               new SampleGap
                                               {
                                                   AgentId = "agent-2",
                                                   Kind = GapKind.Time,
                                                   FromSeq = 10,
                                                   ToSeq = 20,
                                                   From = Samples.Base.AddMinutes(10),
                                                   To = Samples.Base.AddMinutes(15)
                                               },
                                               TestContext.CancellationToken);

        // Act
        var gaps = await store.GapsInRangeAsync(TimeSeriesSamples.Range(Samples.Base.AddMinutes(-1), Samples.Base.AddHours(1)), TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual(["agent-1 time open", "agent-1 sequence open", "agent-2 time final"], gaps.Select(gap => $"{gap.AgentId} {gap.Kind} {gap.State}").ToList(), "the time gap of agent-1 lies over an open sequence gap, the one of agent-2 over none");
        Assert.AreEqual(Samples.Base, gaps[0].From, "start of the time gap");
        Assert.AreEqual(Samples.Base.AddMinutes(5), gaps[0].To, "end of the time gap");
        Assert.AreEqual(12UL, gaps[1].FirstSeq, "first number of the sequence gap");
        Assert.AreEqual(15UL, gaps[1].LastSeq, "last number of the sequence gap");
        Assert.AreEqual(GapCause.NoData, gaps[0].Cause, "cause");
    }

    /// <summary>
    /// The service reads are written as the plan states: the first row of each unit in the range and every row whose restart counter or active enter time differs from its predecessor's, with that predecessor and both boot IDs.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task ServiceChangesAsyncReturnsFirstPerUnitAndChangesWithPrevious()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var entered = Samples.Base.AddHours(-24);

        await TimeSeriesSamples.WriteAsync(store,
                                           string.Empty,
                                           FirstBoot,
                                           TestContext.CancellationToken,
                                           TimeSeriesSamples.Service("c.service", "active", entered, 0, Samples.Base.AddHours(-1)),
                                           TimeSeriesSamples.Service("a.service", "active", entered, 0, Samples.Base.AddHours(1)),
                                           TimeSeriesSamples.Service("b.service", "active", entered, 0, Samples.Base.AddHours(1.5)),
                                           TimeSeriesSamples.Service("a.service", "active", entered, 0, Samples.Base.AddHours(2)),
                                           TimeSeriesSamples.Service("c.service", "active", entered, 0, Samples.Base.AddHours(2)),
                                           TimeSeriesSamples.Service("a.service", "active", Samples.Base.AddHours(2.5), 0, Samples.Base.AddHours(3)),
                                           TimeSeriesSamples.Service("a.service", "active", Samples.Base.AddHours(2.5), 1, Samples.Base.AddHours(4)));
        await TimeSeriesSamples.WriteAsync(store,
                                           string.Empty,
                                           SecondBoot,
                                           TestContext.CancellationToken,
                                           TimeSeriesSamples.Service("a.service", "active", null, 1, Samples.Base.AddHours(5)),
                                           TimeSeriesSamples.Service("a.service", "active", null, 1, Samples.Base.AddHours(6)),
                                           TimeSeriesSamples.Service("a.service", "active", Samples.Base.AddHours(6.5), 1, Samples.Base.AddHours(7)),
                                           TimeSeriesSamples.Service("a.service", "failed", Samples.Base.AddHours(6.5), 1, Samples.Base.AddHours(8)),
                                           TimeSeriesSamples.Service("a.service", "active", Samples.Base.AddHours(8.5), 1, Samples.Base.AddHours(9)),
                                           TimeSeriesSamples.Service("a.service", "active", Samples.Base.AddHours(9.5), 1, Samples.Base.AddHours(10)));

        // Act
        var rows = await store.ServiceChangesAsync(TimeSeriesSamples.Range(Samples.Base, Samples.Base.AddHours(10), TimeSeriesSamples.ServiceLimit), TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual([
                                    "a.service@1 active r0 enter=-24 prev=none",
                                    "b.service@1.5 active r0 enter=-24 prev=none",
                                    "c.service@2 active r0 enter=-24 prev=none",
                                    "a.service@3 active r0 enter=2.5 prev=2",
                                    "a.service@4 active r1 enter=2.5 prev=3",
                                    "a.service@5 active r1 enter=none prev=4",
                                    "a.service@7 active r1 enter=6.5 prev=6",
                                    "a.service@9 active r1 enter=8.5 prev=8"
                                ],
                                rows.Select(Describe).ToList(),
                                "first row of each unit in the range, then each change of restarts or enter time (a missing enter time against a present one is a change; a re-record and a change of state alone are not); the row at the end of the range is left out");
        Assert.AreEqual(FirstBoot, rows[0].BootId, "the boot ID of the first row");
        Assert.AreEqual(SecondBoot, rows[5].BootId, "the boot ID of a row after a boot");
        Assert.AreEqual(FirstBoot, rows[5].Previous!.BootId, "the boot ID of its predecessor");
        Assert.AreEqual("failed", rows[7].Previous!.ActiveState, "the predecessor is the row before, also when it was not a change itself");
    }

    /// <summary>
    /// The members the service reads extract with <c>json_extract</c> are at the top level of the stored payload (verified fact 5a), and the read returns them.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task ServiceChangesAsyncReadsMembersOfStoredPayload()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await TimeSeriesSamples.WriteAsync(store, string.Empty, FirstBoot, TestContext.CancellationToken, TimeSeriesSamples.Service("nginx.service", "active", Samples.Base.AddHours(-1), 3, Samples.Base.AddHours(1)));
        await TimeSeriesSamples.WriteAsync(store, string.Empty, FirstBoot, TestContext.CancellationToken, TimeSeriesSamples.Boot(Samples.Base.AddHours(2), SecondBoot));
        await TimeSeriesSamples.WriteAsync(store, string.Empty, FirstBoot, TestContext.CancellationToken, TimeSeriesSamples.Kill(Samples.Base.AddHours(3), "php-fpm", 4711, FirstBoot, 123456789, "killed"));

        // Act
        var stored = await TimeSeriesSamples.TextAsync(directory.Path,
                                                       "SELECT json_extract(s.data, '$.unit') || '|' || json_extract(s.data, '$.active_state') || '|' || json_extract(s.data, '$.restarts') || '|' || json_extract(s.data, '$.active_enter_at') "
                                                       + "|| '|' || json_extract(b.data, '$.type') || '|' || json_extract(b.data, '$.boot.boot_id') || '|' || json_extract(k.data, '$.oom_kill.victim_pid') || '|' || json_extract(k.data, '$.oom_kill.kernel_time_ns') "
                                                       + "FROM records AS s, records AS b, records AS k WHERE s.kind = 'service_state' AND b.kind = 'kernel_event' AND json_extract(b.data, '$.type') = 'boot' AND k.kind = 'kernel_event' AND json_extract(k.data, '$.type') = 'oom_kill'",
                                                       TestContext.CancellationToken);
        var rows = await store.ServiceChangesAsync(TimeSeriesSamples.Range(Samples.Base, Samples.Base.AddHours(10), TimeSeriesSamples.ServiceLimit), TestContext.CancellationToken);

        // Assert
        var parts = stored.Split('|');

        Assert.HasCount(8, parts, $"every member extracts to a value: {stored}");
        Assert.AreEqual("nginx.service", parts[0], "unit");
        Assert.AreEqual("active", parts[1], "active_state");
        Assert.AreEqual("3", parts[2], "restarts");
        Assert.AreEqual(Samples.Base.AddHours(-1), DateTimeOffset.Parse(parts[3], CultureInfo.InvariantCulture), "active_enter_at");
        Assert.AreEqual("boot", parts[4], "type of the boot event");
        Assert.AreEqual(SecondBoot, parts[5], "boot.boot_id");
        Assert.AreEqual("4711", parts[6], "oom_kill.victim_pid");
        Assert.AreEqual("123456789", parts[7], "oom_kill.kernel_time_ns");
        Assert.HasCount(1, rows, "the read returns the one unit");
        Assert.AreEqual("nginx.service", rows[0].Unit, "the read extracts the unit");
        Assert.AreEqual(3, rows[0].Restarts, "the read extracts the restart counter");
    }

    /// <summary>
    /// A record whose payload lacks members reads with empty values instead of failing.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task ServiceChangesAsyncReadsRecordWithMissingMembers()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await TimeSeriesSamples.InsertRecordAsync(directory.Path, "service_state", Samples.Base.AddHours(1), "{\"unit\":\"forged.service\"}", TestContext.CancellationToken);

        // Act
        var rows = await store.ServiceChangesAsync(TimeSeriesSamples.Range(Samples.Base, Samples.Base.AddHours(2), TimeSeriesSamples.ServiceLimit), TestContext.CancellationToken);

        // Assert
        Assert.HasCount(1, rows, "the first row of the unit");
        Assert.AreEqual("forged.service", rows[0].Unit, "the unit");
        Assert.AreEqual(string.Empty, rows[0].ActiveState, "a missing state reads empty");
        Assert.AreEqual(0, rows[0].Restarts, "a missing counter reads zero");
        Assert.IsNull(rows[0].ActiveEnterAt, "a missing enter time reads null");
        Assert.AreEqual(string.Empty, rows[0].BootId, "no boot ID");
        Assert.IsNull(rows[0].Previous, "no predecessor");
    }

    /// <summary>
    /// The service changes honour their limit.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task ServiceChangesAsyncHonoursLimitInOrderOfCaptureTime()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await TimeSeriesSamples.WriteAsync(store,
                                           string.Empty,
                                           FirstBoot,
                                           TestContext.CancellationToken,
                                           TimeSeriesSamples.Service("c.service", "active", null, 0, Samples.Base.AddHours(3)),
                                           TimeSeriesSamples.Service("a.service", "active", null, 0, Samples.Base.AddHours(1)),
                                           TimeSeriesSamples.Service("b.service", "active", null, 0, Samples.Base.AddHours(2)));

        // Act
        var rows = await store.ServiceChangesAsync(TimeSeriesSamples.Range(Samples.Base, Samples.Base.AddHours(10), 2), TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual(["a.service", "b.service"], rows.Select(row => row.Unit).ToList(), "the two earliest rows");
    }

    /// <summary>
    /// A service change read refuses a limit outside 1 to 2,001 and takes 2,001.
    /// </summary>
    /// <param name="limit">The limit</param>
    /// <param name="refused">Whether the read is refused</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    [DataRow(0, true)]
    [DataRow(2002, true)]
    [DataRow(2001, false)]
    public async Task ServiceChangesAsyncRefusesLimitOutsideOneToTwoThousandOne(int limit, bool refused)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var query = TimeSeriesSamples.Range(Samples.Base, Samples.Base.AddHours(1), limit);

        // Act
        var rows = refused ? null : await store.ServiceChangesAsync(query, TestContext.CancellationToken);
        var exception = refused ? await Assert.ThrowsExactlyAsync<InvalidQueryException>(() => store.ServiceChangesAsync(query, TestContext.CancellationToken), "an invalid limit") : null;

        // Assert
        Assert.AreEqual(ServiceLimitRefusal, exception?.Message ?? ServiceLimitRefusal, "the text names the rule");
        Assert.AreEqual(refused, exception is not null, "refused or read");
        Assert.AreEqual(refused, rows is null, "no rows for a refused read");
    }

    /// <summary>
    /// The previous-state read returns the latest record of each unit before the time and nothing for a unit without one.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task PreviousServiceStatesAsyncReturnsLatestRecordBeforeTimePerUnit()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await TimeSeriesSamples.WriteAsync(store,
                                           string.Empty,
                                           FirstBoot,
                                           TestContext.CancellationToken,
                                           TimeSeriesSamples.Service("a.service", "active", null, 1, Samples.Base.AddHours(1)),
                                           TimeSeriesSamples.Service("b.service", "failed", null, 0, Samples.Base.AddHours(0.5)),
                                           TimeSeriesSamples.Service("other.service", "active", null, 0, Samples.Base.AddHours(2.5)));
        await TimeSeriesSamples.WriteAsync(store,
                                           string.Empty,
                                           SecondBoot,
                                           TestContext.CancellationToken,
                                           TimeSeriesSamples.Service("a.service", "active", Samples.Base.AddHours(1.75), 2, Samples.Base.AddHours(2)),
                                           TimeSeriesSamples.Service("a.service", "inactive", null, 9, Samples.Base.AddHours(3)),
                                           TimeSeriesSamples.Service("a.service", "inactive", null, 9, Samples.Base.AddHours(4)));

        // Act
        var rows = await store.PreviousServiceStatesAsync(["a.service", "b.service", "c.service"], Samples.Base.AddHours(3), TestContext.CancellationToken);

        // Assert
        var ordered = rows.OrderBy(row => row.Unit, StringComparer.Ordinal).ToList();

        Assert.AreSequenceEqual(["a.service@2 active r2 enter=1.75 prev=none", "b.service@0.5 failed r0 enter=none prev=none"], ordered.Select(Describe).ToList(), "the latest record before the time per unit; a record at the time and a unit without a record are not returned");
        Assert.AreEqual(SecondBoot, ordered[0].BootId, "the boot ID of the record");
        Assert.AreEqual(FirstBoot, ordered[1].BootId, "the boot ID of the other record");
    }

    /// <summary>
    /// The previous-state read refuses more than 2,001 units and takes 2,001.
    /// </summary>
    /// <param name="units">The number of units</param>
    /// <param name="refused">Whether the read is refused</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    [DataRow(2002, true)]
    [DataRow(2001, false)]
    public async Task PreviousServiceStatesAsyncRefusesMoreThanTwoThousandOneUnits(int units, bool refused)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var names = Enumerable.Range(0, units).Select(index => $"unit-{index}.service").ToList();

        // Act
        var rows = refused ? null : await store.PreviousServiceStatesAsync(names, Samples.Base, TestContext.CancellationToken);
        var exception = refused ? await Assert.ThrowsExactlyAsync<InvalidQueryException>(() => store.PreviousServiceStatesAsync(names, Samples.Base, TestContext.CancellationToken), "too many units") : null;

        // Assert
        Assert.AreEqual(refused, exception is not null, "refused or read");
        Assert.IsTrue(exception is null || exception.Message.StartsWith("store: invalid query: ", StringComparison.Ordinal), "the text names the rule of a query");
        Assert.IsTrue(exception is null || exception.Message.Contains("2001", StringComparison.Ordinal), "the text names the largest number of units");
        Assert.IsTrue(rows is null || rows.Count == 0, "no unit has a record");
    }

    /// <summary>
    /// The previous-state read examines the 10,000 most recent service records before the time in one call, then filters them by unit: 300 new units whose records lie beyond them are not found, and the units inside are.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task PreviousServiceStatesAsyncScansAtMostTenThousandRows()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var start = Samples.Base.AddHours(-4);

        await TimeSeriesSamples.InsertServiceStatesAsync(directory.Path, "new-%03d.service", start, 300, TestContext.CancellationToken);
        await TimeSeriesSamples.InsertServiceStatesAsync(directory.Path, "beyond.service", start.AddSeconds(300), 1, TestContext.CancellationToken);
        await TimeSeriesSamples.InsertServiceStatesAsync(directory.Path, "edge.service", start.AddSeconds(301), 1, TestContext.CancellationToken);
        await TimeSeriesSamples.InsertServiceStatesAsync(directory.Path, "filler.service", start.AddSeconds(302), 9999, TestContext.CancellationToken);

        var units = Enumerable.Range(0, 300).Select(index => $"new-{index:000}.service").Concat(["beyond.service", "edge.service", "filler.service"]).ToList();

        // Act
        var rows = await store.PreviousServiceStatesAsync(units, Samples.Base, TestContext.CancellationToken);

        // Assert
        var ordered = rows.OrderBy(row => row.Unit, StringComparer.Ordinal).ToList();

        Assert.AreSequenceEqual(["edge.service", "filler.service"], ordered.Select(row => row.Unit).ToList(), "the 10,000th most recent row (edge) is examined, the 10,001st (beyond) and the 300 older units are not");
        Assert.AreEqual(start.AddSeconds(301), ordered[0].CapturedAt, "the record of the unit at the edge");
        Assert.AreEqual(start.AddSeconds(302 + 9998), ordered[1].CapturedAt, "the latest record of the unit with many records");
    }

    /// <summary>
    /// The boot read returns only the boot events of the range, with their boot ID, in order of capture time.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task BootTimesAsyncReturnsOnlyBoots()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await TimeSeriesSamples.WriteAsync(store,
                                           string.Empty,
                                           string.Empty,
                                           TestContext.CancellationToken,
                                           TimeSeriesSamples.Boot(Samples.Base.AddHours(1), FirstBoot),
                                           TimeSeriesSamples.Boot(Samples.Base.AddHours(-1), "11111111-1111-1111-1111-111111111111"),
                                           TimeSeriesSamples.Kill(Samples.Base.AddHours(2), "php-fpm", 4711, FirstBoot, 1, "killed"),
                                           TimeSeriesSamples.Boot(Samples.Base, "22222222-2222-2222-2222-222222222222"),
                                           TimeSeriesSamples.Boot(Samples.Base.AddHours(4), SecondBoot),
                                           TimeSeriesSamples.Service("a.service", "active", null, 0, Samples.Base.AddHours(3)));

        // Act
        var boots = await store.BootTimesAsync(TimeSeriesSamples.Range(Samples.Base, Samples.Base.AddHours(4)), TestContext.CancellationToken);
        var first = await store.BootTimesAsync(TimeSeriesSamples.Range(Samples.Base, Samples.Base.AddHours(4), 1), TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual(["22222222-2222-2222-2222-222222222222@0", $"{FirstBoot}@1"], boots.Select(boot => $"{boot.BootId}@{(boot.CapturedAt - Samples.Base).TotalHours:0}").ToList(), "boots in the range by capture time: the start is in, the end is out, a kill and a service record are no boot");
        Assert.HasCount(1, first, "the limit applies");
        Assert.AreEqual(Samples.Base, first[0].CapturedAt, "the earliest boot");
    }

    /// <summary>
    /// A gap record read projects its span, cause, collector, numbers and agent in order of capture time and returns no other record.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    public async Task GapRecordsAsyncProjectsSpanCauseCollectorAndNumbers()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await TimeSeriesSamples.WriteAsync(store,
                                           "agent-1",
                                           FirstBoot,
                                           TestContext.CancellationToken,
                                           TimeSeriesSamples.GapRecord(Samples.Base.AddHours(2), Samples.Base.AddHours(1.5), Samples.Base.AddHours(2), GapCause.SequenceMissing, string.Empty, 10, 19),
                                           TimeSeriesSamples.GapRecord(Samples.Base.AddHours(1), Samples.Base.AddMinutes(55), Samples.Base.AddHours(1), GapCause.CollectorTimeout, "filesystem", 0, 0),
                                           TimeSeriesSamples.GapRecord(Samples.Base.AddHours(3), Samples.Base.AddHours(2.5), Samples.Base.AddHours(3), GapCause.NoData, string.Empty, 0, 0),
                                           TimeSeriesSamples.Service("a.service", "active", null, 0, Samples.Base.AddHours(1.2)));
        await TimeSeriesSamples.WriteAsync(store,
                                           "agent-2",
                                           SecondBoot,
                                           TestContext.CancellationToken,
                                           TimeSeriesSamples.GapRecord(Samples.Base.AddHours(1.5), Samples.Base.AddHours(1.4), Samples.Base.AddHours(1.5), GapCause.NoData, string.Empty, 0, 0));

        // Act
        var rows = await store.GapRecordsAsync(TimeSeriesSamples.Range(Samples.Base, Samples.Base.AddHours(2.5)), TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual(["agent-1|collector_timeout|filesystem|55|60|none-none", "agent-2|no_data||84|90|none-none", "agent-1|sequence_missing||90|120|10-19"],
                                rows.Select(row => $"{row.AgentId}|{row.Cause}|{row.Collector}|{Minutes(DateTimeOffset.Parse(row.SpanFrom, CultureInfo.InvariantCulture))}|{Minutes(DateTimeOffset.Parse(row.SpanTo, CultureInfo.InvariantCulture))}|{row.FirstSeq?.ToString(CultureInfo.InvariantCulture) ?? "none"}-{row.LastSeq?.ToString(CultureInfo.InvariantCulture) ?? "none"}").ToList(),
                                "gap records by capture time with their projected members; the record at the end of the range and the service record are left out");
        Assert.AreEqual(Samples.Base.AddHours(1), rows[0].CapturedAt, "the capture time");
        Assert.IsTrue(rows.All(row => row.Id > 0), "the IDs are read");
    }

    /// <summary>
    /// A kernel event read projects type, boot ID, victim command, kernel time and process ID of every event in order of capture time, also for events with a long message or without members.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task KernelEventsAsyncProjectsFieldsWithoutMessage()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await TimeSeriesSamples.WriteAsync(store,
                                           string.Empty,
                                           FirstBoot,
                                           TestContext.CancellationToken,
                                           TimeSeriesSamples.Kill(Samples.Base.AddHours(1), "php-fpm", 4711, FirstBoot, 123456789, new string('x', 8000)),
                                           TimeSeriesSamples.Boot(Samples.Base.AddHours(2), SecondBoot),
                                           TimeSeriesSamples.Kill(Samples.Base.AddHours(3), "mysqld", 99, FirstBoot, null, "killed"),
                                           TimeSeriesSamples.Kill(Samples.Base.AddHours(6), "late", 1, FirstBoot, 1, "outside"));
        await TimeSeriesSamples.InsertRecordAsync(directory.Path, "kernel_event", Samples.Base.AddHours(4), "{\"type\":\"oom_kill\"}", TestContext.CancellationToken);

        // Act
        var rows = await store.KernelEventsAsync(TimeSeriesSamples.Range(Samples.Base, Samples.Base.AddHours(5)), TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual([
                                    "oom_kill|boot=0123abcd-0123-0123-0123-0123456789ab|php-fpm|pid=4711|kernel=123456789@1",
                                    "boot|boot=89abcdef-0123-0123-0123-0123456789ab||pid=none|kernel=none@2",
                                    "oom_kill|boot=0123abcd-0123-0123-0123-0123456789ab|mysqld|pid=99|kernel=none@3",
                                    "oom_kill|boot=||pid=none|kernel=none@4"
                                ],
                                rows.Select(DescribeKernel).ToList(),
                                "every kernel event of the range by capture time with its projected members; a record with no members reads empty");
        Assert.IsTrue(rows.Select(row => row.Id).Order().SequenceEqual(rows.Select(row => row.Id)), "the IDs come in the order of the rows");
    }

    /// <summary>
    /// A backup run read projects phase, boot ID, start, last sighting, end and command of the start and end records, with absent members empty.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task BackupRunsAsyncProjectsFields()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var started = Samples.Base.AddHours(1);

        await TimeSeriesSamples.WriteAsync(store,
                                           string.Empty,
                                           FirstBoot,
                                           TestContext.CancellationToken,
                                           TimeSeriesSamples.BackupEnd(started.AddMinutes(40), FirstBoot, started, started.AddMinutes(35), started.AddMinutes(40)),
                                           TimeSeriesSamples.BackupStart(started, FirstBoot, started, "pzstd -9 /var/backups"),
                                           TimeSeriesSamples.BackupStart(Samples.Base.AddHours(3), SecondBoot, Samples.Base.AddHours(3), "rsync"),
                                           TimeSeriesSamples.BackupStart(Samples.Base.AddHours(5), SecondBoot, Samples.Base.AddHours(5), "outside"));

        // Act
        var rows = await store.BackupRunsAsync(TimeSeriesSamples.Range(Samples.Base, Samples.Base.AddHours(4)), TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual([$"start|{FirstBoot}|60|none|none|pzstd -9 /var/backups@60", $"end|{FirstBoot}|60|95|100|@100", $"start|{SecondBoot}|180|none|none|rsync@180"],
                                rows.Select(DescribeBackup).ToList(),
                                "start and end records by capture time with their projected members, absent times and command empty");
        Assert.IsTrue(rows.All(row => row.Id > 0), "the IDs are read");
    }

    /// <summary>
    /// The event reads and the gap read refuse a limit outside 1 to 1,001 and read at 1,001.
    /// </summary>
    /// <param name="read">The read</param>
    /// <param name="limit">The limit</param>
    /// <param name="refused">Whether the read is refused</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    [TestCategory("AC4")]
    [DataRow("GapsInRange", 0, true)]
    [DataRow("GapsInRange", 1002, true)]
    [DataRow("GapsInRange", 1001, false)]
    [DataRow("BootTimes", 0, true)]
    [DataRow("BootTimes", 1002, true)]
    [DataRow("BootTimes", 1001, false)]
    [DataRow("GapRecords", 0, true)]
    [DataRow("GapRecords", 1002, true)]
    [DataRow("GapRecords", 1001, false)]
    [DataRow("KernelEvents", 0, true)]
    [DataRow("KernelEvents", 1002, true)]
    [DataRow("KernelEvents", 1001, false)]
    [DataRow("BackupRuns", 0, true)]
    [DataRow("BackupRuns", 1002, true)]
    [DataRow("BackupRuns", 1001, false)]
    public async Task EventReadsRefuseLimitOverOneThousandOne(string read, int limit, bool refused)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var query = TimeSeriesSamples.Range(Samples.Base, Samples.Base.AddHours(1), limit);

        // Act
        var count = refused ? -1 : await CountAsync(store, read, query);
        var exception = refused ? await Assert.ThrowsExactlyAsync<InvalidQueryException>(() => CountAsync(store, read, query), "an invalid limit") : null;

        // Assert
        Assert.AreEqual(LimitRefusal, exception?.Message ?? LimitRefusal, "the text names the rule");
        Assert.AreEqual(refused, exception is not null, "refused or read");
        Assert.IsTrue(refused || count == 0, "an empty store gives no rows");
    }

    /// <summary>
    /// A page of an event read continues after an ID at the start time, and a row at a later time is returned whatever its ID.
    /// </summary>
    /// <param name="read">The read</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    [TestCategory("AC4")]
    [DataRow("GapRecords")]
    [DataRow("KernelEvents")]
    [DataRow("BackupRuns")]
    public async Task EventReadsPageByCaptureTimeAndIdWithAfterId(string read)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await TimeSeriesSamples.WriteAsync(store, "agent-1", FirstBoot, TestContext.CancellationToken, PagedRecords(read));

        var end = Samples.Base.AddHours(3);
        var all = await IdsAsync(store, read, TimeSeriesSamples.Range(Samples.Base.AddHours(1), end));

        // Act
        var firstPage = await IdsAsync(store, read, TimeSeriesSamples.Range(Samples.Base.AddHours(1), end, 2));
        var secondPage = await IdsAsync(store, read, TimeSeriesSamples.Range(Samples.Base.AddHours(1), end, 2, all[1]));

        // Assert
        Assert.HasCount(4, all, "three rows at the start time and one later");
        Assert.AreSequenceEqual(all.Take(2).ToList(), firstPage, "the first page holds the first two rows");
        Assert.AreSequenceEqual(all.Skip(2).ToList(), secondPage, "the next page skips the rows at the start time up to the ID and keeps the later row");
    }

    /// <summary>
    /// The earliest unfolded time is the earliest capture time of the stored values above the fold watermark, and none once every value is folded.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC6")]
    public async Task EarliestUnfoldedAsyncReadsAboveWatermark()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var empty = await store.EarliestUnfoldedAsync(TestContext.CancellationToken);

        await TimeSeriesSamples.WriteAsync(store, string.Empty, string.Empty, TestContext.CancellationToken, TimeSeriesSamples.Metric(Source, "load", Samples.Base.AddMinutes(70), 1), TimeSeriesSamples.Metric(Source, "load", Samples.Base.AddSeconds(10), 2));

        var beforeFold = await store.EarliestUnfoldedAsync(TestContext.CancellationToken);

        await RetentionSamples.FoldAllAsync(store, RetentionSamples.Defaults(), Samples.Base.AddHours(3), TestContext.CancellationToken);

        var afterFold = await store.EarliestUnfoldedAsync(TestContext.CancellationToken);

        await TimeSeriesSamples.WriteAsync(store, string.Empty, string.Empty, TestContext.CancellationToken, TimeSeriesSamples.Metric(Source, "load", Samples.Base.AddMinutes(150), 3), TimeSeriesSamples.Metric(Source, "load", Samples.Base.AddMinutes(130), 4));

        // Act
        var newer = await store.EarliestUnfoldedAsync(TestContext.CancellationToken);

        // Assert
        Assert.IsNull(empty, "no value, nothing unfolded");
        Assert.AreEqual(Samples.Base.AddSeconds(10), beforeFold, "no fold step yet: every value is unfolded, the earliest capture time counts although it was written second");
        Assert.IsNull(afterFold, "every value is folded");
        Assert.AreEqual(Samples.Base.AddMinutes(130), newer, "of the values written after the fold, the earliest capture time");
    }

    /// <summary>
    /// Creates a raw read of the series <c>name</c> over a range that starts at <see cref="Samples.Base"/>.
    /// </summary>
    /// <param name="name">The metric name</param>
    /// <param name="maxTimes">The limit of distinct times</param>
    /// <param name="maxLines">The line limit</param>
    /// <param name="seconds">The length of the range in seconds</param>
    /// <returns>The query</returns>
    private static RawValueQuery RawQuery(string name, int maxTimes, int maxLines, int seconds = 90)
    {
        return new RawValueQuery
               {
                   Source = Source,
                   Name = name,
                   From = Samples.Base,
                   To = Samples.Base.AddSeconds(seconds),
                   MaxTimes = maxTimes,
                   MaxLines = maxLines
               };
    }

    /// <summary>
    /// Creates a step read.
    /// </summary>
    /// <param name="tier">The tier</param>
    /// <param name="name">The metric name</param>
    /// <param name="from">The start of the range</param>
    /// <param name="to">The end of the range</param>
    /// <param name="step">The step</param>
    /// <param name="maxLines">The line limit</param>
    /// <returns>The query</returns>
    private static RollupStepQuery StepQuery(RollupTier tier, string name, DateTimeOffset from, DateTimeOffset to, TimeSpan step, int maxLines = 64)
    {
        return new RollupStepQuery
               {
                   Tier = tier,
                   Source = Source,
                   Name = name,
                   From = from,
                   To = to,
                   Step = step,
                   MaxLines = maxLines
               };
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
    /// Gets the minutes after <see cref="Samples.Base"/> of an instant.
    /// </summary>
    /// <param name="instant">The instant</param>
    /// <returns>The minutes</returns>
    private static string Minutes(DateTimeOffset instant)
    {
        return (instant - Samples.Base).TotalMinutes.ToString("0", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Gets the hours after <see cref="Samples.Base"/> of an instant.
    /// </summary>
    /// <param name="instant">The instant</param>
    /// <returns>The hours</returns>
    private static string Hours(DateTimeOffset instant)
    {
        return (instant - Samples.Base).TotalHours.ToString("0.##", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Describes a service row by unit, capture hour, state, restarts, enter hour and the capture hour of its predecessor.
    /// </summary>
    /// <param name="row">The row</param>
    /// <returns>The description</returns>
    private static string Describe(ServiceStateRow row)
    {
        var enter = row.ActiveEnterAt is null ? "none" : Hours(DateTimeOffset.Parse(row.ActiveEnterAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal));
        var previous = row.Previous is null ? "none" : Hours(row.Previous.CapturedAt);

        return $"{row.Unit}@{Hours(row.CapturedAt)} {row.ActiveState} r{row.Restarts} enter={enter} prev={previous}";
    }

    /// <summary>
    /// Describes a kernel event row.
    /// </summary>
    /// <param name="row">The row</param>
    /// <returns>The description</returns>
    private static string DescribeKernel(KernelEventRow row)
    {
        var pid = row.VictimPid?.ToString(CultureInfo.InvariantCulture) ?? "none";
        var kernel = row.KernelTimeNs?.ToString(CultureInfo.InvariantCulture) ?? "none";

        return $"{row.Type}|boot={row.BootId}|{row.VictimCommand}|pid={pid}|kernel={kernel}@{Hours(row.CapturedAt)}";
    }

    /// <summary>
    /// Describes a backup run row.
    /// </summary>
    /// <param name="row">The row</param>
    /// <returns>The description</returns>
    private static string DescribeBackup(BackupRunRow row)
    {
        return $"{row.Phase}|{row.BootId}|{TimeOf(row.StartedAt)}|{TimeOf(row.LastSeenAt)}|{TimeOf(row.GoneBy)}|{row.Command}@{Minutes(row.CapturedAt)}";
    }

    /// <summary>
    /// Gets the minutes after <see cref="Samples.Base"/> of a stored time text.
    /// </summary>
    /// <param name="text">The text, empty for absent</param>
    /// <returns>The minutes, or <c>none</c> for an absent time</returns>
    private static string TimeOf(string text)
    {
        return text.Length == 0 ? "none" : Minutes(DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal));
    }

    /// <summary>
    /// Creates the four records of a paging test: three at one capture time and one later.
    /// </summary>
    /// <param name="read">The read the records are for</param>
    /// <returns>The records</returns>
    private static DataRecord[] PagedRecords(string read)
    {
        var at = Samples.Base.AddHours(1);
        var later = Samples.Base.AddHours(2);

        return read switch
               {
                   "GapRecords" => [.. Enumerable.Range(0, 3).Select(index => TimeSeriesSamples.GapRecord(at, at.AddMinutes(-1 - index), at, GapCause.NoData, string.Empty, 0, 0)), TimeSeriesSamples.GapRecord(later, later.AddMinutes(-1), later, GapCause.NoData, string.Empty, 0, 0)],
                   "KernelEvents" => [.. Enumerable.Range(0, 3).Select(index => TimeSeriesSamples.Kill(at, "cmd", 100 + index, FirstBoot, index + 1, "killed")), TimeSeriesSamples.Kill(later, "cmd", 200, FirstBoot, 9, "killed")],
                   _ => [.. Enumerable.Range(0, 3).Select(index => TimeSeriesSamples.BackupStart(at, FirstBoot, at.AddMinutes(-index), $"cmd{index}")), TimeSeriesSamples.BackupStart(later, FirstBoot, later, "later")]
               };
    }

    /// <summary>
    /// Runs an event read and returns the IDs of its rows.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="read">The read</param>
    /// <param name="query">The query</param>
    /// <returns>A task that returns the IDs</returns>
    private async Task<List<long>> IdsAsync(SqliteStore store, string read, TimeRangeQuery query)
    {
        return read switch
               {
                   "GapRecords" => (await store.GapRecordsAsync(query, TestContext.CancellationToken)).Select(row => row.Id).ToList(),
                   "KernelEvents" => (await store.KernelEventsAsync(query, TestContext.CancellationToken)).Select(row => row.Id).ToList(),
                   _ => (await store.BackupRunsAsync(query, TestContext.CancellationToken)).Select(row => row.Id).ToList()
               };
    }

    /// <summary>
    /// Runs one of the reads that refuse a limit over 1,001 and counts its rows.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="read">The read</param>
    /// <param name="query">The query</param>
    /// <returns>A task that returns the number of rows</returns>
    private async Task<int> CountAsync(SqliteStore store, string read, TimeRangeQuery query)
    {
        return read switch
               {
                   "GapsInRange" => (await store.GapsInRangeAsync(query, TestContext.CancellationToken)).Count,
                   "BootTimes" => (await store.BootTimesAsync(query, TestContext.CancellationToken)).Count,
                   "GapRecords" => (await store.GapRecordsAsync(query, TestContext.CancellationToken)).Count,
                   "KernelEvents" => (await store.KernelEventsAsync(query, TestContext.CancellationToken)).Count,
                   _ => (await store.BackupRunsAsync(query, TestContext.CancellationToken)).Count
               };
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
    private async Task InsertSequenceGapAsync(string directory, string agentId, string state, int fromMinutes, int toMinutes, long firstSeq)
    {
        await TimeSeriesSamples.InsertGapAsync(directory,
                                               new SampleGap
                                               {
                                                   AgentId = agentId,
                                                   State = state,
                                                   FirstSeq = firstSeq,
                                                   LastSeq = firstSeq + 1,
                                                   From = Samples.Base.AddMinutes(fromMinutes),
                                                   To = Samples.Base.AddMinutes(toMinutes)
                                               },
                                               TestContext.CancellationToken);
    }

    /// <summary>
    /// Runs <c>EXPLAIN QUERY PLAN</c> on the statement shape of the raw read: one series in a range, in order of capture time, labels and record ID.
    /// </summary>
    /// <param name="directory">The store directory</param>
    /// <returns>A task that returns the steps of the plan</returns>
    private async Task<List<string>> PlanAsync(string directory)
    {
        var details = new List<string>();

        await using var connection = new SqliteConnection($"Data Source={RetentionSamples.DatabasePath(directory)};Mode=ReadOnly;Pooling=False");

        await connection.OpenAsync(TestContext.CancellationToken);

        await using var command = connection.CreateCommand();

#pragma warning disable CA2100 // the statement is a test-owned literal
        command.CommandText = "EXPLAIN QUERY PLAN SELECT record_id, captured_at, labels, value FROM metrics WHERE source = $source AND name = $name AND captured_at >= $from AND captured_at < $to ORDER BY captured_at, labels, record_id";
#pragma warning restore CA2100
        command.Parameters.AddWithValue("$source", Source);
        command.Parameters.AddWithValue("$name", "cpu");
        command.Parameters.AddWithValue("$from", StorageTime.ToNanoseconds(Samples.Base));
        command.Parameters.AddWithValue("$to", StorageTime.ToNanoseconds(Samples.Base.AddDays(1)));

        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        while (await reader.ReadAsync(TestContext.CancellationToken))
        {
            details.Add(reader.GetString(3));
        }

        return details;
    }

    #endregion // Methods
}