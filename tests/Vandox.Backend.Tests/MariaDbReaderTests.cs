using Microsoft.Extensions.Time.Testing;

using Vandox.Backend.History;
using Vandox.Backend.ServiceHealth;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="MariaDbReader"/>
/// </summary>
[TestClass]
public class MariaDbReaderTests
{
    #region Fields

    private static readonly DateTimeOffset _now = HistorySamples.Base;

    private static readonly string[] _metricNames = [
                                                        "mariadb_memory_max_bytes",
                                                        "mem_total_bytes",
                                                        "mariadb_questions_per_second",
                                                        "mariadb_innodb_buffer_pool_used_percent",
                                                        "mariadb_innodb_buffer_pool_read_hit_percent"
                                                    ];

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// The metrics are read in steps only, also for a range inside the raw period, and without markers: the five series in order, the rollups only, no read of kernel events, service
    /// changes or backups; then the status is read for the same range at the step of that read, and every read carries the token.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC7")]
    public async Task MariaDbReaderQueriesStepsOnlyWithoutMarkers()
    {
        // Arrange
        var range = new HistoryRange(_now.AddHours(-1), _now);

        using var series = new ScriptedTimeSeriesReader();
        using var reader = new FakeMariaDbHistoryReader();

        reader.AnswerStatus = query => ServicesSamples.StatusAnswer(query);

        // Act
        var charts = await Create(series, reader).ReadAsync(range, 200, TestContext.CancellationToken);

        // Assert
        var steps = series.Calls.Where(call => call.Name == nameof(ITimeSeriesReader.RollupStepsAsync)).Select(call => (RollupStepQuery)call.Argument!).ToArray();
        var names = series.Calls.Select(call => call.Name).ToArray();
        var statusCalls = reader.CallsOf(nameof(IMariaDbHistoryReader.MariaDbStatusAsync));

        Assert.IsEmpty(series.RawQueries, "no raw read although the range is inside the raw period");
        Assert.AreSequenceEqual(["mariadb", "system", "mariadb", "mariadb", "mariadb"], steps.Select(step => step.Source).ToArray(), "the sources of the five series");
        Assert.AreSequenceEqual(_metricNames, steps.Select(step => step.Name).ToArray(), "the five metrics in order");
        Assert.DoesNotContain(nameof(ITimeSeriesReader.KernelEventsAsync), names, "no kernel events are read");
        Assert.DoesNotContain(nameof(ITimeSeriesReader.ServiceChangesAsync), names, "no service changes are read");
        Assert.DoesNotContain(nameof(ITimeSeriesReader.BackupRunsAsync), names, "no backups are read");
        Assert.IsTrue(series.Calls.All(call => call.Token == TestContext.CancellationToken), "every read of the series carries the token");
        Assert.HasCount(1, statusCalls, "the status is read once");

        var status = (MariaDbStatusQuery)statusCalls[0].Argument!;

        Assert.AreEqual(range.Start, status.From, "the status from the start of the range");
        Assert.AreEqual(range.End, status.To, "the status to the end of the range");
        Assert.AreEqual(steps[0].Step, status.Step, "the status at the step of the metric read");
        Assert.AreEqual(TimeSpan.FromMinutes(5), status.Step, "five minutes for an hour on a chart of 200 pixels");
        Assert.AreEqual(TestContext.CancellationToken, statusCalls[0].Token, "the status read carries the token");
        Assert.IsNotNull(charts.Availability, "the availability chart");
    }

    /// <summary>
    /// The charts are built from the series and the status: the availability chart has a point per step of the query, and the summary is that of the status.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    [TestCategory("AC7")]
    public async Task MariaDbReaderBuildsTheChartsFromSeriesAndStatus()
    {
        // Arrange
        var range = new HistoryRange(_now.AddHours(-1), _now);

        using var series = new ScriptedTimeSeriesReader();
        using var reader = new FakeMariaDbHistoryReader();

        reader.AnswerStatus = query => ServicesSamples.StatusAnswer(query);

        // Act
        var charts = await Create(series, reader).ReadAsync(range, 200, TestContext.CancellationToken);

        // Assert
        Assert.IsNotNull(charts.Availability, "the availability chart");
        Assert.HasCount(12, charts.Availability.Times, "twelve steps of five minutes");
        Assert.AreEqual(_now.AddHours(-1), charts.Availability.Times[0], "the first step starts at the range");
        Assert.AreEqual("Up 100.0 %, down 0.0 %, not answering 0.0 %, unknown 0.0 % of this range.", charts.Summary, "the summary of the status");
        Assert.AreEqual("In use 1.2 GiB of a theoretical maximum of unknown; RAM unknown.", charts.Budget, "the budget from the status and the lines without a value");
    }

    /// <summary>
    /// The retention of the status that the policy sets reaches the chart view: a year with a 30-day retention says where the status begins.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task MariaDbReaderPassesTheSnapshotRetentionToTheView()
    {
        // Arrange
        var range = new HistoryRange(_now.AddDays(-365), _now);

        using var series = new ScriptedTimeSeriesReader();
        using var reader = new FakeMariaDbHistoryReader();

        reader.AnswerStatus = query => ServicesSamples.StatusAnswer(query, _now.AddDays(-30));

        // Act
        var charts = await Create(series, reader).ReadAsync(range, 200, TestContext.CancellationToken);

        // Assert
        Assert.Contains("MariaDB's status is kept for 30 days; availability, memory in use and connections begin at 2026-09-01 10:00:00 UTC.", charts.Notes.ToArray(), "the note of the retention");
    }

    /// <summary>
    /// A failure of the store passes to the caller, which shows its own text.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC9")]
    public async Task MariaDbReaderPassesAStoreFailure()
    {
        // Arrange
        var range = new HistoryRange(_now.AddHours(-1), _now);

        using var series = new ScriptedTimeSeriesReader();
        using var reader = new FakeMariaDbHistoryReader();

        reader.Failure = new StoreException("the store failed");

        // Act and assert
        var thrown = await Assert.ThrowsExactlyAsync<StoreException>(() => Create(series, reader).ReadAsync(range, 200, TestContext.CancellationToken), "the failure of the store");

        Assert.AreEqual("the store failed", thrown.Message, "the failure itself");
    }

    /// <summary>
    /// A range that starts after now gives a series without a step: the status is not read and the section says that no MariaDB data is stored.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task MariaDbReaderReadsNoStatusForAFutureRange()
    {
        // Arrange
        var range = new HistoryRange(_now.AddDays(1), _now.AddDays(2));

        using var series = new ScriptedTimeSeriesReader();
        using var reader = new FakeMariaDbHistoryReader();

        // Act
        var charts = await Create(series, reader).ReadAsync(range, 200, TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(reader.Calls, "no status read");
        Assert.Contains(ServicesView.NoMariaDbDataText, charts.Notes.ToArray(), "no MariaDB data");
    }

    /// <summary>
    /// Creates the reader over a series reader and a MariaDB history reader, with the clock at the time of the tests and the default retention.
    /// </summary>
    /// <param name="series">The reader of the series</param>
    /// <param name="reader">The MariaDB history reader</param>
    /// <returns>The reader</returns>
    private static MariaDbReader Create(ScriptedTimeSeriesReader series, FakeMariaDbHistoryReader reader)
    {
        var clock = new FakeTimeProvider(_now);
        var policy = HistoryPageHarness.Policy();

        return new MariaDbReader(new TimeSeriesService(series, policy, clock), reader, policy, clock);
    }

    #endregion // Methods
}