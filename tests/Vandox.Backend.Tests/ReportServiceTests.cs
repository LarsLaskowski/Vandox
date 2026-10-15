using Microsoft.Extensions.Time.Testing;

using Vandox.Backend.Reports;
using Vandox.Core.Model;
using Vandox.Core.Signatures;
using Vandox.Storage;

using static Vandox.Backend.Tests.ReportSamples;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="ReportService"/>
/// </summary>
[TestClass]
public class ReportServiceTests
{
    #region Constants

    private static readonly DateTimeOffset _now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Two runs do not overlap: the second one reads nothing while the first one is in the store, and runs when the first one has ended.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ReportServiceRunsOneReportAtATime()
    {
        // Arrange
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var records = new ScriptedRecordReader
                      {
                          Before = (_, token) => release.Task.WaitAsync(token)
                      };

        using var service = Service(records, new ScriptedIncidentStore([], false));

        var first = service.RunAsync(Builder(), TestContext.CancellationToken);

        await records.WaitForCallsAsync(1);

        var second = service.RunAsync(Builder(), TestContext.CancellationToken);

        await Task.Delay(200, TestContext.CancellationToken);

        var readsWhileFirstRuns = records.Queries.Count;

        // Act
        release.SetResult();

        await first.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);
        await second.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(1, readsWhileFirstRuns, "the second run waits for the first one");
        Assert.AreEqual(1, records.MaxInFlight, "never more than one read in flight");
        Assert.AreEqual(2, records.Queries.Count(query => query.Kind == RecordKind.LogLine), "the second run reads when the first one has ended");
    }

    /// <summary>
    /// A run that waits for the slot stops waiting when its token is cancelled, without reading anything, and the slot stays usable for the next run.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ReportServiceStopsWaitingForTheRunWhenCancelled()
    {
        // Arrange
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var records = new ScriptedRecordReader
                      {
                          Before = (_, token) => release.Task.WaitAsync(token)
                      };

        using var service = Service(records, new ScriptedIncidentStore([], false));
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);

        var first = service.RunAsync(Builder(), TestContext.CancellationToken);

        await records.WaitForCallsAsync(1);

        var second = service.RunAsync(Builder(), waiting.Token);

        // Act
        await waiting.CancelAsync();

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => second);

        release.SetResult();

        await first.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        var third = await service.RunAsync(Builder(), TestContext.CancellationToken).WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        // Assert
        Assert.IsNotNull(exception, "the wait ended with its token");
        Assert.AreEqual(0, third.IncidentsListed, "the next run is made");
        Assert.AreEqual(2, records.Queries.Count(query => query.Kind == RecordKind.LogLine), "the cancelled run read nothing, the first and the third did");
    }

    /// <summary>
    /// The incidents are read one at a time, in two passes, and the page gets all of them.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ReportServiceReadsIncidentsOneAtATime()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        for (var id = 1; id <= 3; id++)
        {
            var saved = await store.SaveIncidentAsync(Outage(KillAt.AddDays(id), 1, "mariadbd"), KillAt, TestContext.CancellationToken);

            Assert.AreEqual(IncidentSaveOutcome.Created, saved.Outcome, "the incident is created");
        }

        var recording = new RecordingIncidentStore(store);
        var builder = Builder();

        using var service = Service(store, recording);

        // Act
        var result = await service.RunAsync(builder, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(3, result.IncidentsListed, "the incidents listed");
        Assert.HasCount(3, builder.Document.Outages, "every incident reached the writer");
        Assert.IsGreaterThanOrEqualTo(6, recording.Queries.Count, "an incident per query in each of the two passes");
        Assert.IsTrue(recording.Queries.All(query => query.Limit == 1), "every incident query asks for one incident");
    }

    /// <summary>
    /// Creates the service over a reader and a store, with a fixed clock and a lookback of one hour.
    /// </summary>
    /// <param name="records">The reader</param>
    /// <param name="incidents">The store</param>
    /// <returns>The service</returns>
    private static ReportService Service(IRecordReader records, IIncidentStore incidents)
    {
        return new ReportService(records, incidents, BuiltInSignatures.Catalog, TimeSpan.FromHours(1), new FakeTimeProvider(_now));
    }

    /// <summary>
    /// Creates a writer that keeps the first 50 outages.
    /// </summary>
    /// <returns>The builder</returns>
    private static ReportViewBuilder Builder()
    {
        return new ReportViewBuilder(ReportText.English, 0, ReportsView.PageOutages);
    }

    #endregion // Methods
}