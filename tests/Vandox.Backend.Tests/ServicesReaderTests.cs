using Microsoft.Extensions.Time.Testing;

using Vandox.Backend.History;
using Vandox.Backend.ServiceHealth;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="ServicesReader"/>
/// </summary>
[TestClass]
public class ServicesReaderTests
{
    #region Fields

    private static readonly DateTimeOffset _now = HistorySamples.Base;

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// The reader reads the restarts of the range with the time-series service and the availability of the range with the store, once each and with the token, and the table
    /// holds the units of the availability with the restarts counted per unit.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    public async Task ServicesReaderReadsRestartsAndAvailability()
    {
        // Arrange
        var range = new HistoryRange(_now.AddDays(-30), _now);

        using var series = new ScriptedTimeSeriesReader();
        using var reader = new FakeServiceHistoryReader();

        series.Services.Add(ServicesSamples.RestartRecord(_now.AddDays(-9), ServicesSamples.PhpFpm, 1));
        series.Services.Add(ServicesSamples.RestartRecord(_now.AddDays(-5), ServicesSamples.PhpFpm, 2));
        series.Services.Add(ServicesSamples.RestartRecord(_now.AddDays(-2), ServicesSamples.PhpFpm, 3));
        reader.Result = ServicesSamples.Availability(ServicesSamples.Unit(ServicesSamples.Nginx, TimeSpan.FromHours(718), TimeSpan.FromHours(2), TimeSpan.Zero),
                                                     ServicesSamples.Unit(ServicesSamples.PhpFpm, TimeSpan.FromDays(30), TimeSpan.Zero, TimeSpan.Zero));

        // Act
        var table = await Create(series, reader).ReadAsync(range, TestContext.CancellationToken);

        // Assert
        var changes = series.Calls.Where(call => call.Name == nameof(ITimeSeriesReader.ServiceChangesAsync)).ToArray();
        var queries = reader.Queries;

        Assert.HasCount(1, changes, "the restarts are read once");
        Assert.AreEqual(range.Start, ((TimeRangeQuery)changes[0].Argument!).From, "the restarts from the start of the range");
        Assert.AreEqual(range.End, ((TimeRangeQuery)changes[0].Argument!).To, "the restarts to the end of the range");
        Assert.HasCount(1, queries, "the availability is read once");
        Assert.AreEqual(range.Start, queries[0].From, "the availability from the start of the range");
        Assert.AreEqual(range.End, queries[0].To, "the availability to the end of the range");
        Assert.IsTrue(series.Calls.All(call => call.Token == TestContext.CancellationToken), "every read of the series carries the token");
        Assert.AreEqual(TestContext.CancellationToken, reader.Calls[0].Token, "the read of the availability carries the token");
        Assert.AreSequenceEqual([ServicesSamples.PhpFpm, ServicesSamples.Nginx], table.Units.Rows.Select(row => HistorySamples.Text(row.Cells[0])).ToArray(), "the unit with restarts first");
        Assert.AreEqual("3", ServicesSamples.CellTexts(table.Units.Rows[0])[4], "three restarts of the unit");
        Assert.HasCount(3, table.Restarts.Rows, "the list of the restarts");
        Assert.IsFalse(table.Empty, "units are listed");
    }

    /// <summary>
    /// A range that ends after now is read up to now, the restarts and the availability alike.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC3")]
    public async Task ServicesReaderReadsUpToNow()
    {
        // Arrange
        var range = new HistoryRange(_now.AddDays(-30), _now.AddDays(2));

        using var series = new ScriptedTimeSeriesReader();
        using var reader = new FakeServiceHistoryReader();

        reader.Result = ServicesSamples.Availability(ServicesSamples.Unit(ServicesSamples.Nginx, TimeSpan.FromDays(30), TimeSpan.Zero, TimeSpan.Zero));

        // Act
        await Create(series, reader).ReadAsync(range, TestContext.CancellationToken);

        // Assert
        var changes = series.Calls.Where(call => call.Name == nameof(ITimeSeriesReader.ServiceChangesAsync)).Select(call => (TimeRangeQuery)call.Argument!).ToArray();

        Assert.HasCount(1, changes, "the restarts are read once");
        Assert.AreEqual(_now, changes[0].To, "the restarts up to now");
        Assert.AreSequenceEqual([range.Start], reader.Queries.Select(query => query.From).ToArray(), "the availability from the start of the range");
        Assert.AreSequenceEqual([_now], reader.Queries.Select(query => query.To).ToArray(), "the availability up to now");
    }

    /// <summary>
    /// The notes of the read reach the table: the gaps of the systemd collector and gaps not read from the availability, restarts not counted from the restarts.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC3")]
    public async Task ServicesReaderPassesTheNotesOfTheReads()
    {
        // Arrange
        var range = new HistoryRange(_now.AddDays(-30), _now);

        using var series = new ScriptedTimeSeriesReader();
        using var reader = new FakeServiceHistoryReader();

        reader.Result = new ServiceAvailabilityResult
                        {
                            Units = [ServicesSamples.Unit(ServicesSamples.Nginx, TimeSpan.FromDays(10), TimeSpan.Zero, TimeSpan.FromDays(20))],
                            UnitCount = 1,
                            SystemdGaps = true,
                            GapsCompleteUntil = new DateTimeOffset(2026, 9, 10, 4, 5, 6, TimeSpan.Zero)
                        };

        // Act
        var table = await Create(series, reader).ReadAsync(range, TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual([ServicesView.SystemdGapsText, "Gaps after 2026-09-10 04:05:06 UTC are not read; the time after it counts as unknown."], table.Notes.ToArray(), "the notes of the availability");
    }

    /// <summary>
    /// A failure of the store passes to the caller, which shows its own text.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC9")]
    public async Task ServicesReaderPassesAStoreFailure()
    {
        // Arrange
        var range = new HistoryRange(_now.AddDays(-30), _now);

        using var series = new ScriptedTimeSeriesReader();
        using var reader = new FakeServiceHistoryReader();

        reader.Failure = new StoreException("the store failed");

        // Act and assert
        var thrown = await Assert.ThrowsExactlyAsync<StoreException>(() => Create(series, reader).ReadAsync(range, TestContext.CancellationToken), "the failure of the store");

        Assert.AreEqual("the store failed", thrown.Message, "the failure itself");
    }

    /// <summary>
    /// Creates the reader over a series reader and a service history reader, with the clock at the time of the tests.
    /// </summary>
    /// <param name="series">The reader of the series</param>
    /// <param name="reader">The service history reader</param>
    /// <returns>The reader</returns>
    private static ServicesReader Create(ScriptedTimeSeriesReader series, FakeServiceHistoryReader reader)
    {
        var clock = new FakeTimeProvider(_now);

        return new ServicesReader(new TimeSeriesService(series, HistoryPageHarness.Policy(), clock), reader, clock);
    }

    #endregion // Methods
}