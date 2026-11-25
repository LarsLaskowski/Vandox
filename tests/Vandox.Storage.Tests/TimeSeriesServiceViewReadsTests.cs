using Microsoft.Extensions.Time.Testing;

using Vandox.Core.Model;

namespace Vandox.Storage.Tests;

/// <summary>
/// Tests for the entry points of <see cref="TimeSeriesService"/> that the services view uses, on a real store and a fake clock
/// </summary>
[TestClass]
public class TimeSeriesServiceViewReadsTests
{
    #region Constants

    private const string FirstBoot = TimelineSamples.BootId;

    private const string SecondBoot = TimelineSamples.OtherBootId;

    private const string Unit = "svc.service";

    #endregion // Constants

    #region Fields

    private static readonly SeriesKey _mem = new("system", "mem_available_bytes");

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// The restart read finds the restarts of the markers of a full query, with the same rules and the same times, and no other marker.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    public async Task TimeSeriesServiceRestartsMatchQueryMarkers()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await TimeSeriesSamples.WriteAsync(store,
                                           string.Empty,
                                           FirstBoot,
                                           TestContext.CancellationToken,
                                           TimeSeriesSamples.Service("counter.service", "active", null, 0, At(-1)),
                                           TimeSeriesSamples.Service("counter.service", "active", null, 1, At(1)),
                                           TimeSeriesSamples.Service("enter.service", "active", At(-3), 0, At(0.2)),
                                           TimeSeriesSamples.Service("enter.service", "active", At(0.5), 0, At(1.2)),
                                           TimeSeriesSamples.Service("rebooted.service", "active", At(-3), 0, At(-1)),
                                           TimeSeriesSamples.Boot(At(0), SecondBoot),
                                           TimeSeriesSamples.Service("rebooted.service", "active", At(0.4), 0, At(1.1)));

        var service = new TimeSeriesService(store, RetentionSamples.Defaults(), new FakeTimeProvider(At(12)));

        // Act
        var restarts = await service.RestartsAsync(At(0), At(10), TestContext.CancellationToken);
        var query = await service.QueryAsync(Query(At(0), At(10), false), TestContext.CancellationToken);

        // Assert
        var expected = query.Markers.Where(marker => marker.Kind == MarkerKind.ServiceRestart).Select(marker => $"{marker.At:O}|{marker.Text}").ToList();

        Assert.AreSequenceEqual(["counter.service", "enter.service"], restarts.Markers.Select(marker => marker.Text).Order(StringComparer.Ordinal).ToList(), "the counter rise and the enter time without a boot between are restarts, the enter time across a boot is none");
        Assert.AreSequenceEqual(expected, restarts.Markers.Select(marker => $"{marker.At:O}|{marker.Text}").ToList(), "the same markers at the same times and in the same order as the query");
        Assert.AreEqual(At(0.5), restarts.Markers[0].At, "the moved enter time is the time of its restart");
        Assert.IsTrue(restarts.Markers.All(marker => marker.Kind == MarkerKind.ServiceRestart), "no boot, kill or backup marker");
        Assert.IsNull(restarts.CompleteUntil, "the list is complete");
    }

    /// <summary>
    /// Over 1,000 restarts the earliest 1,000 are kept, with the time up to which the list is complete.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    public async Task TimeSeriesServiceRestartsCapAtThousandWithCompleteUntil()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var records = new List<DataRecord>();

        foreach (var index in Enumerable.Range(0, 1100))
        {
            records.Add(TimeSeriesSamples.Service($"unit{index:D4}.service", "active", null, 0, At(-1).AddMilliseconds(index)));
            records.Add(TimeSeriesSamples.Service($"unit{index:D4}.service", "active", null, 1, At(0).AddSeconds(index)));
        }

        await TimeSeriesSamples.WriteAsync(store, string.Empty, FirstBoot, TestContext.CancellationToken, [.. records]);

        var service = new TimeSeriesService(store, RetentionSamples.Defaults(), new FakeTimeProvider(At(12)));

        // Act
        var restarts = await service.RestartsAsync(At(0), At(10), TestContext.CancellationToken);

        // Assert
        Assert.HasCount(TimeSeriesService.MaxMarkers, restarts.Markers, "the restarts are cut at the cap");
        Assert.AreEqual(At(0).AddSeconds(999), restarts.Markers[^1].At, "the earliest 1,000 are kept");
        Assert.AreEqual(At(0).AddSeconds(1000), restarts.CompleteUntil, "complete up to the first restart that was cut");
    }

    /// <summary>
    /// A restart whose boot check cannot be decided bounds the list at the time of that restart.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    public async Task TimeSeriesServiceRestartsUndecidedBootBoundsList()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await TimeSeriesSamples.WriteAsync(store,
                                           string.Empty,
                                           FirstBoot,
                                           TestContext.CancellationToken,
                                           TimeSeriesSamples.Service(Unit, "active", At(-2), 0, At(-1)),
                                           TimeSeriesSamples.Service(Unit, "active", At(0.5), 0, At(1)));
        await TimeSeriesSamples.InsertBootsAsync(directory.Path, At(2), 10011, TimeSpan.FromSeconds(1), TestContext.CancellationToken);

        var service = new TimeSeriesService(store, RetentionSamples.Defaults(), new FakeTimeProvider(At(12)));

        // Act
        var restarts = await service.RestartsAsync(At(0), At(10), TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(restarts.Markers, "the restart is not decided, so it is not listed");
        Assert.AreEqual(At(0.5), restarts.CompleteUntil, "the list is complete up to the time of the undecided restart");
    }

    /// <summary>
    /// A restart read refuses a range that breaks a rule with the texts of the query, before any store read.
    /// </summary>
    /// <param name="scenario">The broken rule</param>
    /// <param name="message">The error text</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    [DataRow("fromEqualsTo", "store: invalid query: from must be before to")]
    [DataRow("fromAfterTo", "store: invalid query: from must be before to")]
    [DataRow("fromBelowStorable", "store: invalid query: from and to must be within the storable range")]
    [DataRow("toAboveStorable", "store: invalid query: from and to must be within the storable range")]
    public async Task TimeSeriesServiceRestartsRefusesInvalidRangeBeforeStoreRead(string scenario, string message)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var reader = new CountingTimeSeriesReader(store);
        var service = new TimeSeriesService(reader, RetentionSamples.Defaults(), new FakeTimeProvider(At(1)));
        var (from, to) = scenario switch
                         {
                             "fromEqualsTo" => (At(1), At(1)),
                             "fromAfterTo" => (At(2), At(1)),
                             "fromBelowStorable" => (DateTimeOffset.MinValue, At(1)),
                             _ => (At(0), DateTimeOffset.MaxValue)
                         };

        // Act
        var exception = await Assert.ThrowsExactlyAsync<InvalidQueryException>(() => service.RestartsAsync(from, to, TestContext.CancellationToken), "the range breaks a rule");

        // Assert
        Assert.AreEqual(message, exception.Message, "the error names the rule");
        Assert.IsEmpty(reader.Calls, "no store read before the refusal");
    }

    /// <summary>
    /// A range that begins at or after now reads nothing, and the end of a range that reaches past now is clamped to now.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    public async Task TimeSeriesServiceRestartsReturnsEmptyForFutureRangeAndClampsEndToNow()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await TimeSeriesSamples.WriteAsync(store,
                                           string.Empty,
                                           FirstBoot,
                                           TestContext.CancellationToken,
                                           TimeSeriesSamples.Service(Unit, "active", null, 0, At(-1)),
                                           TimeSeriesSamples.Service(Unit, "active", null, 1, At(1)),
                                           TimeSeriesSamples.Service(Unit, "active", null, 2, At(3)));

        var reader = new CountingTimeSeriesReader(store);
        var service = new TimeSeriesService(reader, RetentionSamples.Defaults(), new FakeTimeProvider(At(2)));

        // Act
        var future = await service.RestartsAsync(At(2), At(5), TestContext.CancellationToken);
        var callsAfterFuture = reader.Calls.Count;
        var clamped = await service.RestartsAsync(At(0), At(10), TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(future.Markers, "a range that begins at now has no restart");
        Assert.IsNull(future.CompleteUntil, "and no bound");
        Assert.AreEqual(0, callsAfterFuture, "nothing was read for it");
        Assert.AreSequenceEqual([At(1)], clamped.Markers.Select(marker => marker.At).ToList(), "the restart after now is not read");
    }

    /// <summary>
    /// A query that asks for steps only reads the rollup steps although the range lies within the raw period and fits the target.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC7")]
    public async Task TimeSeriesServiceStepsOnlySkipsRaw()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await TimeSeriesSamples.WriteAsync(store, string.Empty, string.Empty, TestContext.CancellationToken, Enumerable.Range(0, 20).Select(index => TimeSeriesSamples.Metric(_mem.Source, _mem.Name, At(0).AddSeconds(30 * index), index)).ToArray());
        await RetentionSamples.FoldAllAsync(store, RetentionSamples.Defaults(), At(2), TestContext.CancellationToken);

        var service = new TimeSeriesService(store, RetentionSamples.Defaults(), new FakeTimeProvider(At(2)));

        // Act
        var raw = await service.QueryAsync(Query(At(0), At(1), false), TestContext.CancellationToken);
        var steps = await service.QueryAsync(Query(At(0), At(1), true), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(TimeSeriesResolution.Raw, raw.Resolution, "without the option the 20 times are raw");
        Assert.AreEqual(TimeSeriesResolution.Step, steps.Resolution, "with the option the steps are read");
        Assert.AreEqual(TimeSpan.FromMinutes(5), steps.Step, "the smallest step of the ladder");
        Assert.HasCount(1, steps.Lines, "one line");
        Assert.Contains(PointState.Value, steps.Lines[0].Points.Select(point => point.State).ToList(), "the folded values are read");
    }

    /// <summary>
    /// A query that skips the markers returns none, with no completeness time, and reads no marker source, while the gaps are still read.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC7")]
    public async Task TimeSeriesServiceSkipMarkersReadsNoMarkers()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await TimeSeriesSamples.WriteAsync(store,
                                           string.Empty,
                                           FirstBoot,
                                           TestContext.CancellationToken,
                                           TimeSeriesSamples.Metric(_mem.Source, _mem.Name, At(0), 1),
                                           TimeSeriesSamples.Boot(At(0.2), SecondBoot),
                                           TimeSeriesSamples.Service(Unit, "active", null, 0, At(-1)),
                                           TimeSeriesSamples.Service(Unit, "active", null, 1, At(0.5)),
                                           TimeSeriesSamples.BackupStart(At(0.6), FirstBoot, At(0.6), "pzstd"));

        var reader = new CountingTimeSeriesReader(store);
        var service = new TimeSeriesService(reader, RetentionSamples.Defaults(), new FakeTimeProvider(At(12)));

        // Act
        var skipped = await service.QueryAsync(Query(At(0), At(10), false, true), TestContext.CancellationToken);
        var callsWhenSkipped = reader.Calls.ToList();
        var full = await service.QueryAsync(Query(At(0), At(10), false), TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(skipped.Markers, "no marker");
        Assert.IsNull(skipped.MarkersCompleteUntil, "no completeness time of the markers");

        foreach (var name in new[] { "KernelEventsAsync", "ServiceChangesAsync", "PreviousServiceStatesAsync", "BackupRunsAsync", "BootTimesAsync" })
        {
            Assert.DoesNotContain(name, callsWhenSkipped, $"{name} is not called");
        }

        Assert.Contains("GapsInRangeAsync", callsWhenSkipped, "the gaps are still read");
        Assert.IsNotEmpty(full.Markers, "the same query without the option finds the boot, the restart and the backup run");
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
    /// Creates a query of the series <see cref="_mem"/>.
    /// </summary>
    /// <param name="from">The start of the range</param>
    /// <param name="to">The end of the range</param>
    /// <param name="stepsOnly">Whether raw resolution is skipped</param>
    /// <param name="skipMarkers">Whether no markers are read</param>
    /// <returns>The query</returns>
    private static TimeSeriesQuery Query(DateTimeOffset from, DateTimeOffset to, bool stepsOnly, bool skipMarkers = false)
    {
        return new TimeSeriesQuery
               {
                   Series = [_mem],
                   From = from,
                   To = to,
                   Target = 1000,
                   StepsOnly = stepsOnly,
                   SkipMarkers = skipMarkers
               };
    }

    #endregion // Methods
}