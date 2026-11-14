using System.Text.Json;

using Microsoft.Extensions.Time.Testing;

using Vandox.Backend.Incidents;
using Vandox.Backend.Retention;
using Vandox.Core.Model;
using Vandox.Core.Signatures;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="KillHoldScanner"/>
/// </summary>
[TestClass]
public class KillHoldScannerTests
{
    #region Constants

    private const int MaxSteps = 1000;

    private static readonly DateTimeOffset _kill = IncidentSamples.Base;

    private static readonly DateTimeOffset _now = IncidentSamples.Base.AddDays(200);

    private static readonly TimeSpan _before = TimeSpan.FromHours(1);

    private static readonly TimeSpan _after = TimeSpan.FromMinutes(30);

    private static readonly TimeSpan _margin = TimeSpan.FromMinutes(5);

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// An agent kernel event of an OOM kill behind more than one page of other kernel events becomes a hold of its window, and the scan progress reaches the highest record ID.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC11")]
    public async Task KillHoldScannerScanHoldsWindowOfKernelEventKill()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var agent = new AgentRecords();
        var records = Enumerable.Range(0, 1001).Select(index => AgentRecords.BootEvent(_kill.AddDays(-1).AddSeconds(index))).Append(AgentRecords.OomKill(_kill)).ToArray();

        await agent.WriteAsync(store, _now, TestContext.CancellationToken, records);

        // Act
        var added = await new KillHoldScanner(store).ScanAsync(Policy(), _now, TestContext.CancellationToken);
        var holds = await AllHoldsAsync(store);
        var progress = await store.KillScanProgressAsync(TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(1, added, "one hold added");
        Assert.AreSequenceEqual([Hold(_kill)], holds, "the hold spans the kill's window and the margin");
        Assert.AreEqual(1002L, progress.HighestId, "the highest stored record ID");
        Assert.AreEqual(progress.HighestId, progress.ScannedId, "the scan reached the highest record ID");
    }

    /// <summary>
    /// A journal line that the catalog reads as an OOM kill makes no hold, and neither does a kernel event of another type.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC11")]
    public async Task KillHoldScannerScanMakesNoHoldFromCatalogKillLine()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var agent = new AgentRecords();

        await agent.WriteAsync(store, _now, TestContext.CancellationToken, AgentRecords.KillLine(_kill), AgentRecords.BootEvent(_kill.AddMinutes(1)));

        // Act
        var added = await new KillHoldScanner(store).ScanAsync(Policy(), _now, TestContext.CancellationToken);
        var holds = await AllHoldsAsync(store);
        var progress = await store.KillScanProgressAsync(TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0, added, "no hold added");
        Assert.IsEmpty(holds, "no hold stored");
        Assert.AreEqual(progress.HighestId, progress.ScannedId, "the records are scanned all the same");
    }

    /// <summary>
    /// A kernel event of an OOM kill stored after a scan, with an older capture time, becomes a hold in the next scan; a kill scanned once makes no second hold.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC11")]
    public async Task KillHoldScannerScanCatchesLateKernelEvents()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var agent = new AgentRecords();
        var scanner = new KillHoldScanner(store);
        var late = _kill.AddDays(-10);

        await agent.WriteAsync(store, _now, TestContext.CancellationToken, AgentRecords.OomKill(_kill));
        await scanner.ScanAsync(Policy(), _now, TestContext.CancellationToken);
        await agent.WriteAsync(store, _now.AddMinutes(1), TestContext.CancellationToken, AgentRecords.OomKill(late));

        // Act
        var added = await scanner.ScanAsync(Policy(), _now.AddMinutes(1), TestContext.CancellationToken);
        var again = await scanner.ScanAsync(Policy(), _now.AddMinutes(2), TestContext.CancellationToken);
        var holds = await AllHoldsAsync(store);

        // Assert
        Assert.AreEqual(1, added, "the late kill adds one hold");
        Assert.AreEqual(0, again, "a scan without new records adds none");
        Assert.AreSequenceEqual([Hold(late), Hold(_kill)], holds, "the hold of the late kill beside the earlier one, none twice");
    }

    /// <summary>
    /// A new kill whose window overlaps a stored hold is merged with it into one hold, and a kill apart from it gets its own.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC11")]
    public async Task KillHoldScannerSaveMergesOverlappingStoredHolds()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var agent = new AgentRecords();
        var scanner = new KillHoldScanner(store);
        var overlapping = _kill.AddMinutes(45);
        var apart = _kill.AddHours(5);

        await agent.WriteAsync(store, _now, TestContext.CancellationToken, AgentRecords.OomKill(_kill));
        await scanner.ScanAsync(Policy(), _now, TestContext.CancellationToken);
        await agent.WriteAsync(store, _now.AddMinutes(1), TestContext.CancellationToken, AgentRecords.OomKill(overlapping), AgentRecords.OomKill(apart));

        // Act
        await scanner.ScanAsync(Policy(), _now.AddMinutes(1), TestContext.CancellationToken);

        var holds = await AllHoldsAsync(store);

        // Assert
        Assert.AreSequenceEqual([(Hold(_kill).Start, Hold(overlapping).End), Hold(apart)], holds, "the overlapping windows are one hold, the other kill has its own");
    }

    /// <summary>
    /// An imported kernel event of an OOM kill makes no hold: holds come only from the agent's kernel collector.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC11")]
    public async Task KillHoldScannerScanMakesNoHoldFromImportedKernelEventKill()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var imported = AgentRecords.OomKill(_kill);

        imported.Origin = RecordOrigin.Import;
        await IncidentSamples.WriteAsync(store, TestContext.CancellationToken, imported);

        // Act
        var added = await new KillHoldScanner(store).ScanAsync(Policy(), _now, TestContext.CancellationToken);
        var holds = await AllHoldsAsync(store);
        var progress = await store.KillScanProgressAsync(TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0, added, "no hold added");
        Assert.IsEmpty(holds, "no hold stored");
        Assert.AreEqual(progress.HighestId, progress.ScannedId, "the imported kill is scanned all the same");
    }

    /// <summary>
    /// The hold of a kill at the edge of the storable range ends at that edge instead of beyond it.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC11")]
    public async Task KillHoldScannerScanClampsHoldsToTheStorableRange()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var agent = new AgentRecords();
        var first = StorableTime.Min.AddMinutes(1);
        var last = StorableTime.Max.AddMinutes(-1);

        await agent.WriteAsync(store, _now, TestContext.CancellationToken, AgentRecords.OomKill(first), AgentRecords.OomKill(last));

        // Act
        var added = await new KillHoldScanner(store).ScanAsync(Policy(), _now, TestContext.CancellationToken);
        var holds = await AllHoldsAsync(store);

        // Assert
        Assert.AreEqual(2, added, "two holds added");
        Assert.AreSequenceEqual([(StorableTime.Min, Hold(first).End), (Hold(last).Start, StorableTime.Max)], holds, "each hold ends at the edge of the storable range");
    }

    /// <summary>
    /// With a finite incident period, a kill whose hold ended before <c>now − incidents</c> makes no hold, a kill whose hold ends in the period does, and both are scanned.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC11")]
    public async Task KillHoldScannerScanMakesNoHoldPastTheIncidentPeriod()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var agent = new AgentRecords();
        var period = TimeSpan.FromDays(100);
        var expired = _now - period - _after - _margin - TimeSpan.FromMinutes(1);
        var edge = _now - period - _after - _margin;
        var policy = Policy(period);

        await agent.WriteAsync(store, _now, TestContext.CancellationToken, AgentRecords.OomKill(expired), AgentRecords.OomKill(edge.AddHours(5)), AgentRecords.OomKill(edge));

        // Act
        var added = await new KillHoldScanner(store).ScanAsync(policy, _now, TestContext.CancellationToken);
        var holds = await AllHoldsAsync(store);
        var progress = await store.KillScanProgressAsync(TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(2, added, "two holds added");
        Assert.AreSequenceEqual([Hold(edge), Hold(edge.AddHours(5))], holds, "the holds of the kills whose hold ends at or after the start of the incident period");
        Assert.AreEqual(progress.HighestId, progress.ScannedId, "the expired kill is scanned all the same");
    }

    /// <summary>
    /// The hold of a kernel event of an OOM kill captured 40 seconds after or before the kill's log line spans the kernel event's window
    /// and the 5-minute margin, so it contains the whole window of the log line.
    /// </summary>
    /// <param name="offsetSeconds">Seconds from the log line to the kernel event, negative before it</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC11")]
    [DataRow(40)]
    [DataRow(-40)]
    public async Task KillHoldScannerScanHoldCoversWindowOfKillLineSecondsApart(int offsetSeconds)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var agent = new AgentRecords();
        var kernelEvent = _kill.AddSeconds(offsetSeconds);

        await agent.WriteAsync(store, _now, TestContext.CancellationToken, AgentRecords.KillLine(_kill), AgentRecords.OomKill(kernelEvent));

        // Act
        var added = await new KillHoldScanner(store).ScanAsync(Policy(), _now, TestContext.CancellationToken);
        var holds = await AllHoldsAsync(store);

        // Assert
        Assert.AreEqual(1, added, "one hold added");
        Assert.AreSequenceEqual([(kernelEvent - _before - _margin, kernelEvent + _after + _margin)], holds, "the hold spans the kernel event's window and the margin");
        Assert.IsLessThanOrEqualTo(_kill - _before, holds[0].Start, "the hold starts at or before the window of the kill line");
        Assert.IsGreaterThanOrEqualTo(_kill + _after, holds[0].End, "the hold ends at or after the window of the kill line");
    }

    /// <summary>
    /// The incident of an agent kernel OOM kill whose window data has passed every period is built the same after the removal as on an unchanged copy, while the data outside the window is removed; also when the
    /// kernel event is captured 40 seconds after or before the kill's log line.
    /// </summary>
    /// <param name="offsetSeconds">Seconds from the log line to the kernel event, negative before it</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC11")]
    [DataRow(0)]
    [DataRow(40)]
    [DataRow(-40)]
    public async Task KillHoldScannerIncidentAfterRemovalEqualsIncidentBefore(int offsetSeconds)
    {
        // Arrange
        using var unchangedDirectory = new TempDirectory();
        using var removedDirectory = new TempDirectory();
        await using var unchanged = await SqliteStore.OpenAsync(unchangedDirectory.Path, TestContext.CancellationToken);
        await using var removed = await SqliteStore.OpenAsync(removedDirectory.Path, TestContext.CancellationToken);
        var policy = Policy();

        await WriteHistoryAsync(unchanged, offsetSeconds);
        await WriteHistoryAsync(removed, offsetSeconds);
        await new KillHoldScanner(unchanged).ScanAsync(policy, _now, TestContext.CancellationToken);
        await FoldAllAsync(removed, policy);
        await new KillHoldScanner(removed).ScanAsync(policy, _now, TestContext.CancellationToken);
        await RemoveAllAsync(removed, policy);

        // Act
        var before = await ReconstructAsync(unchanged, policy);
        var after = await ReconstructAsync(removed, policy);
        var linesBefore = await CountAsync(unchanged, RecordKind.LogLine);
        var linesAfter = await CountAsync(removed, RecordKind.LogLine);
        var snapshotsAfter = await CountAsync(removed, RecordKind.ProcessSnapshot);

        // Assert
        Assert.AreEqual(7, linesBefore, "the unchanged copy holds every line");
        Assert.AreEqual(4, linesAfter, "only the lines in the kill's window stay");
        Assert.AreEqual(2, snapshotsAfter, "only the process snapshots in the kill's window stay");
        Assert.AreEqual(before, after, "the incident is built the same after the removal");
    }

    /// <summary>
    /// Creates the default policy with the reconstructor's window.
    /// </summary>
    /// <param name="incidents">The incident period, <c>null</c> for unlimited</param>
    /// <returns>The policy</returns>
    private static RetentionPolicy Policy(TimeSpan? incidents = null)
    {
        return new RetentionPolicy
               {
                   Raw = TimeSpan.FromDays(30),
                   FiveMinuteRollups = TimeSpan.FromDays(365),
                   HourlyRollups = TimeSpan.FromDays(1095),
                   Logs = TimeSpan.FromDays(90),
                   Snapshots = TimeSpan.FromDays(30),
                   Events = TimeSpan.FromDays(1095),
                   Incidents = incidents,
                   KillWindowBefore = _before,
                   KillWindowAfter = _after
               };
    }

    /// <summary>
    /// Returns the hold of a kill: its window widened by the margin on both sides.
    /// </summary>
    /// <param name="at">The capture time of the kernel event</param>
    /// <returns>The start and end of the hold</returns>
    private static (DateTimeOffset Start, DateTimeOffset End) Hold(DateTimeOffset at)
    {
        return (at - _before - _margin, at + _after + _margin);
    }

    /// <summary>
    /// Returns a time relative to the kill.
    /// </summary>
    /// <param name="minutes">Minutes after the kill, negative before it</param>
    /// <returns>The time</returns>
    private static DateTimeOffset At(int minutes)
    {
        return _kill.AddMinutes(minutes);
    }

    /// <summary>
    /// Creates the agent history around the kill: lines, snapshots and metrics in its window and three hours before and after it, the kill line and its kernel event. It ends on a connection snapshot, which the
    /// removal keeps as the record with the highest ID and the counts of the test do not include.
    /// </summary>
    /// <param name="offsetSeconds">Seconds from the kill line to its kernel event, negative before it</param>
    /// <returns>The records</returns>
    private static DataRecord[] History(int offsetSeconds)
    {
        return [
                   IncidentSamples.Line(At(-180), "cron", "heartbeat ok", "journal", "journal"),
                   IncidentSamples.Processes(At(-180), ("mariadbd", 100)),
                   IncidentSamples.Metric(At(-180), "mem_available_bytes", 2000),
                   IncidentSamples.Line(At(-40), "kernel", "Linux version 5.15.0-91-generic (buildd@lcy02) #101-Ubuntu SMP", "journal", "journal"),
                   IncidentSamples.Line(At(-30), "systemd", "Started mariadb.service - MariaDB database server.", "journal", "journal"),
                   IncidentSamples.Processes(At(-30), ("mariadbd", 300), ("pzstd", 100)),
                   IncidentSamples.Metric(At(-20), "mem_available_bytes", 800),
                   IncidentSamples.Connections(At(-15), 12, "apache2"),
                   IncidentSamples.Processes(At(-10), ("mariadbd", 900), ("pzstd", 200)),
                   IncidentSamples.Metric(At(-10), "mem_available_bytes", 300),
                   IncidentSamples.Metric(At(-5), "swap_used_bytes", 100),
                   AgentRecords.KillLine(At(0)),
                   AgentRecords.OomKill(At(0).AddSeconds(offsetSeconds)),
                   IncidentSamples.Line(At(5), "systemd", "mariadb.service: Failed with result 'oom-kill'.", "journal", "journal"),
                   IncidentSamples.Line(At(120), "cron", "heartbeat ok", "journal", "journal"),
                   IncidentSamples.Line(At(180), "cron", "heartbeat ok", "journal", "journal"),
                   IncidentSamples.Processes(At(180), ("mariadbd", 100)),
                   IncidentSamples.Connections(At(180), 3, "apache2")
               ];
    }

    /// <summary>
    /// Reads every stored hold as start and end.
    /// </summary>
    /// <param name="store">The store</param>
    /// <returns>A task that returns the holds in start order</returns>
    private async Task<List<(DateTimeOffset Start, DateTimeOffset End)>> AllHoldsAsync(SqliteStore store)
    {
        var holds = await store.HoldsAsync(StorableTime.Min, StorableTime.Max, TestContext.CancellationToken);

        return holds.Select(hold => (hold.Start, hold.End)).ToList();
    }

    /// <summary>
    /// Writes the agent history.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="offsetSeconds">Seconds from the kill line to its kernel event, negative before it</param>
    /// <returns>A task that completes when the history is stored</returns>
    private Task WriteHistoryAsync(SqliteStore store, int offsetSeconds)
    {
        return new AgentRecords().WriteAsync(store, _now, TestContext.CancellationToken, History(offsetSeconds));
    }

    /// <summary>
    /// Folds every stored metric value.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="policy">The policy</param>
    /// <returns>A task that completes when nothing is left to fold</returns>
    private async Task FoldAllAsync(SqliteStore store, RetentionPolicy policy)
    {
        for (var step = 0; step < MaxSteps; step++)
        {
            var result = await store.FoldRollupsAsync(policy, _now, TestContext.CancellationToken);

            if (result.More == false)
            {
                return;
            }
        }

        Assert.Fail($"the fold did not end within {MaxSteps} steps");
    }

    /// <summary>
    /// Removes everything past its period, step by step with the cursor.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="policy">The policy</param>
    /// <returns>A task that completes when nothing is left to remove</returns>
    private async Task RemoveAllAsync(SqliteStore store, RetentionPolicy policy)
    {
        RemovalCursor? cursor = null;

        for (var step = 0; step < MaxSteps; step++)
        {
            var result = await store.RemoveExpiredAsync(policy, _now, cursor, TestContext.CancellationToken);

            if (result.More == false)
            {
                return;
            }

            cursor = result.Next;
        }

        Assert.Fail($"the removal did not end within {MaxSteps} steps");
    }

    /// <summary>
    /// Runs the reconstruction with the log period of the policy and the store's holds and returns the one stored incident.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="policy">The policy</param>
    /// <returns>A task that returns the incident as JSON</returns>
    private async Task<string> ReconstructAsync(SqliteStore store, RetentionPolicy policy)
    {
        var reconstructor = new IncidentReconstructor(store, store, store, BuiltInSignatures.Catalog, _before, _after, new FakeTimeProvider(_now))
                            {
                                LogPeriodStart = _now - policy.Logs,
                                Holds = store
                            };

        await reconstructor.RunAsync(_ => Task.CompletedTask, TestContext.CancellationToken);

        var stored = await store.IncidentsAsync(new IncidentQuery
                                                {
                                                    From = StorableTime.Min,
                                                    To = StorableTime.Max,
                                                    Limit = 10
                                                },
                                                TestContext.CancellationToken);

        Assert.HasCount(1, stored, "one incident is stored");

        return JsonSerializer.Serialize(stored[0].Incident);
    }

    /// <summary>
    /// Counts the stored records of a kind.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="kind">The kind</param>
    /// <returns>A task that returns the count</returns>
    private async Task<int> CountAsync(SqliteStore store, string kind)
    {
        var records = await store.RecordsAsync(new RecordQuery
                                               {
                                                   Kind = kind,
                                                   From = StorableTime.Min,
                                                   To = StorableTime.Max,
                                                   Limit = 100
                                               },
                                               TestContext.CancellationToken);

        return records.Count;
    }

    #endregion // Methods
}