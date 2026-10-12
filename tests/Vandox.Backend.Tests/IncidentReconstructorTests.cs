using System.Text.Json;

using Microsoft.Extensions.Time.Testing;

using Vandox.Backend.Incidents;
using Vandox.Core.Incidents;
using Vandox.Core.Model;
using Vandox.Core.Signatures;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="IncidentReconstructor"/>
/// </summary>
[TestClass]
public class IncidentReconstructorTests
{
    #region Constants

    private static readonly TimeSpan _before = TimeSpan.FromHours(1);

    private static readonly TimeSpan _after = TimeSpan.FromMinutes(30);

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// The copies of one kill in several logs are one kill, and kills three hours apart are two incidents.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IncidentReconstructorCreatesOneIncidentPerKill()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var clock = new FakeTimeProvider(IncidentSamples.Base.AddDays(1));
        var saved = new List<SavedIncident>();

        await IncidentSamples.WriteAsync(store,
                                         TestContext.CancellationToken,
                                         IncidentSamples.Kill(IncidentSamples.Base),
                                         IncidentSamples.Kill(IncidentSamples.Base.AddSeconds(1), log: "kern.log"),
                                         IncidentSamples.Kill(IncidentSamples.Base.AddSeconds(2), "journal", "journal"),
                                         IncidentSamples.Kill(IncidentSamples.Base.AddHours(3)));

        // Act
        var result = await RunAsync(store, store, clock, saved);
        var stored = await ReadAllAsync(store);

        // Assert
        Assert.AreEqual(4L, result.LinesRead, "lines read");
        Assert.AreEqual(2L, result.Kills, "kills");
        Assert.AreEqual(2L, result.Incidents, "incidents");
        Assert.AreEqual(2L, result.Created, "created");
        Assert.AreEqual(0L, result.Updated + result.Unchanged + result.Merged + result.Kept + result.Skipped, "nothing else happened");
        Assert.HasCount(2, stored, "stored incidents");
        Assert.AreEqual(1L, stored[0].Incident.KillCount, "kills of the first incident");
        Assert.AreEqual(1L, stored[1].Incident.KillCount, "kills of the second incident");
        Assert.AreEqual(IncidentSamples.Base, stored[0].Incident.FirstKillAt, "time of the first kill");
        Assert.AreEqual(3L, stored[0].Incident.Kills[0].LineCount, "the first kill holds its three copies");
        Assert.HasCount(3, stored[0].Incident.Kills[0].RecordIds, "record IDs of the three copies");
        Assert.AreSequenceEqual([IncidentSaveOutcome.Created, IncidentSaveOutcome.Created], saved.Select(item => item.Result.Outcome), "the callback saw both incidents");
        Assert.AreSequenceEqual(stored.Select(row => row.Id), saved.Select(item => item.Result.Id), "the callback saw the stored IDs");
    }

    /// <summary>
    /// The incident of a kill holds the timeline, growth, consumers, backup, boot, metric extremes, connection peak and template findings of its stored data.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IncidentReconstructorBuildsIncidentFromStoredRecords()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var clock = new FakeTimeProvider(IncidentSamples.Base.AddDays(1));
        var spikeHour = new DateTimeOffset(2026, 10, 2, 4, 0, 0, TimeSpan.Zero);
        var records = new List<DataRecord>
                      {
                          IncidentSamples.Boot(At(-40)),
                          IncidentSamples.Line(At(-30), "systemd", "Started mariadb.service - MariaDB database server."),
                          IncidentSamples.Line(At(-20), "systemd", "Stopped mariadb.service - MariaDB database server."),
                          IncidentSamples.Kill(At(0)),
                          IncidentSamples.Line(At(5), "systemd", "mariadb.service: Failed with result 'oom-kill'."),
                          IncidentSamples.Processes(At(-30), ("mariadbd", 300), ("pzstd", 100), ("php-fpm", 50)),
                          IncidentSamples.Processes(At(-10), ("mariadbd", 900), ("pzstd", 200), ("php-fpm", 60), ("apache2", 400)),
                          IncidentSamples.Connections(At(-15), 12, "apache2"),
                          IncidentSamples.Metric(At(-20), "mem_available_bytes", 800),
                          IncidentSamples.Metric(At(-10), "mem_available_bytes", 300),
                          IncidentSamples.Metric(At(-5), "mem_available_bytes", 500),
                          IncidentSamples.Metric(At(-10), "swap_used_bytes", 900),
                          IncidentSamples.Metric(At(-5), "swap_used_bytes", 100),
                          IncidentSamples.Metric(At(-10), "load1", 9.0)
                      };

        for (var hour = 1; hour <= 25; hour++)
        {
            records.Add(IncidentSamples.Line(spikeHour.AddHours(-hour).AddMinutes(30), "cron", "heartbeat ok"));
        }

        for (var index = 0; index < 12; index++)
        {
            records.Add(IncidentSamples.Line(spikeHour.AddMinutes(10 + index), "cron", "cron job ran"));
        }

        await IncidentSamples.WriteAsync(store, TestContext.CancellationToken, [.. records]);

        // Act
        var result = await RunAsync(store, store, clock, []);
        var stored = (await ReadAllAsync(store)).Single().Incident;

        // Assert
        Assert.AreEqual(1L, result.Created, "created");
        Assert.AreEqual(At(-60), stored.Start, "start of the window");
        Assert.AreEqual(At(30), stored.End, "end of the window");
        Assert.AreSequenceEqual(["boot", "backup_first_seen", "service_start", "service_stop", "backup_last_seen", "oom_kill", "service_failed"], stored.Timeline.Select(entry => entry.Type), "timeline in order");
        Assert.AreEqual("mariadb.service", stored.Timeline[2].Texts["unit"], "unit of the service start");
        Assert.AreEqual("mariadbd", stored.Kills[0].VictimCommand, "victim");
        Assert.AreEqual(4242L, stored.Kills[0].VictimPid, "victim pid");
        Assert.AreEqual(7577600000L, stored.Kills[0].AnonRssBytes, "anon-RSS in bytes");
        Assert.AreEqual(2L, stored.Hypothesis.SnapshotCount, "snapshots after the boot");
        Assert.IsTrue(stored.Hypothesis.GrowthKnown, "growth is known");
        Assert.AreSequenceEqual(["mariadbd", "apache2", "pzstd"], stored.Hypothesis.Growth.Select(entry => entry.Program), "growth candidates");
        Assert.AreSequenceEqual(["mariadbd", "apache2", "pzstd"], stored.Hypothesis.Consumers.Select(entry => entry.Program), "consumers");
        Assert.IsTrue(stored.Hypothesis.BackupRunning, "the backup ran");
        Assert.AreSequenceEqual(["pzstd"], stored.Hypothesis.BackupPrograms, "backup programs");
        Assert.AreEqual(At(-40), stored.Hypothesis.RecentBootAt, "boot");
        Assert.AreEqual(2L, stored.Hypothesis.ServiceEvents, "service events before the kill");
        Assert.AreSequenceEqual(["mem_available_bytes", "swap_used_bytes", "load1"], stored.Metrics.Select(metric => metric.Name), "metric extremes");
        Assert.AreEqual(300.0, stored.Metrics[0].Value, "lowest available memory");
        Assert.AreEqual(At(-10), stored.Metrics[0].At, "time of the lowest available memory");
        Assert.AreEqual(900.0, stored.Metrics[1].Value, "highest swap use");
        Assert.AreEqual(9.0, stored.Metrics[2].Value, "highest load");
        Assert.IsNotNull(stored.Connections, "connection peak");
        Assert.AreEqual(12L, stored.Connections.Sockets, "sockets of the peak");
        Assert.AreEqual("apache2", stored.Connections.Processes[0].Command, "busiest process");
        Assert.Contains("cron job ran", stored.NewTemplates.Select(template => template.Template).ToList(), "the template first seen in the window");
        Assert.ContainsSingle(spike => spike.Template == "cron job ran", stored.TemplateSpikes, "one spike of the template");

        var spike = stored.TemplateSpikes.Single(entry => entry.Template == "cron job ran");

        Assert.AreEqual(12L, spike.Count, "lines of the spike hour");
        Assert.AreEqual(spikeHour, spike.Hour, "spike hour");
    }

    /// <summary>
    /// A second run without new data changes nothing; new kills update the incident of their window, and a kill that bridges two incidents merges them into the older ID.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IncidentReconstructorRerunUpdatesInsteadOfDuplicating()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var clock = new FakeTimeProvider(IncidentSamples.Base.AddDays(1));
        var first = new List<SavedIncident>();
        var third = new List<SavedIncident>();

        await IncidentSamples.WriteAsync(store, TestContext.CancellationToken, IncidentSamples.Kill(At(0)), IncidentSamples.Kill(At(180)), IncidentSamples.Kill(At(270)));

        var created = await RunAsync(store, store, clock, first);

        clock.Advance(TimeSpan.FromDays(1));

        var unchanged = await RunAsync(store, store, clock, []);
        var afterUnchanged = await ReadAllAsync(store);

        await IncidentSamples.WriteAsync(store, TestContext.CancellationToken, IncidentSamples.Kill(At(20)), IncidentSamples.Kill(At(195)));
        clock.Advance(TimeSpan.FromDays(1));

        // Act
        var updated = await RunAsync(store, store, clock, third);
        var stored = await ReadAllAsync(store);

        // Assert
        Assert.AreEqual(3L, created.Created, "the first run creates three incidents");
        Assert.AreEqual(3L, unchanged.Unchanged, "the second run leaves all three unchanged");
        Assert.AreEqual(0L, unchanged.Created + unchanged.Updated + unchanged.Merged + unchanged.Kept, "the second run changes nothing");
        Assert.IsTrue(afterUnchanged.All(row => row.UpdatedAt == IncidentSamples.Base.AddDays(1)), "updated_at is untouched by the second run");
        Assert.AreEqual(2L, updated.Incidents, "two incidents are rebuilt");
        Assert.AreEqual(0L, updated.Created, "no new incident");
        Assert.AreEqual(2L, updated.Updated, "both rebuilt incidents update a stored one");
        Assert.AreEqual(1L, updated.Merged, "the bridged pair is merged");
        Assert.AreEqual(0L, updated.Kept, "no stored incident is left over");
        Assert.AreEqual(first[0].Result.Id, third[0].Result.Id, "the first incident keeps its ID");
        Assert.AreEqual(2L, third[0].Incident.KillCount, "the first incident holds the new kill");
        Assert.AreEqual(first[1].Result.Id, third[1].Result.Id, "the bridged incident takes the older ID");
        Assert.AreEqual(1L, third[1].Result.Merged, "one incident merged away");
        Assert.AreEqual(3L, third[1].Incident.KillCount, "the bridged incident holds all its kills");
        Assert.AreSequenceEqual([first[0].Result.Id, first[1].Result.Id], stored.Select(row => row.Id), "two incidents are stored, the younger ID is gone");
        Assert.AreEqual(At(300), stored[1].Incident.End, "the bridged incident reaches the window end of the last kill");
    }

    /// <summary>
    /// A stored incident that no rebuilt incident overlaps is kept as it is and counted.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IncidentReconstructorKeepsStoredIncidentsWithoutMatch()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var clock = new FakeTimeProvider(IncidentSamples.Base.AddDays(1));
        var old = new Incident
                  {
                      Start = IncidentSamples.Base.AddYears(-3),
                      End = IncidentSamples.Base.AddYears(-3).AddHours(2),
                      WindowBeforeSeconds = 3600,
                      WindowAfterSeconds = 1800,
                      FirstKillAt = IncidentSamples.Base.AddYears(-3).AddHours(1),
                      LastKillAt = IncidentSamples.Base.AddYears(-3).AddHours(1),
                      KillCount = 1
                  };
        var savedAt = IncidentSamples.Base.AddDays(-1);

        await store.SaveIncidentAsync(old, savedAt, TestContext.CancellationToken);
        await IncidentSamples.WriteAsync(store, TestContext.CancellationToken, IncidentSamples.Kill(At(0)));

        // Act
        var result = await RunAsync(store, store, clock, []);
        var stored = await ReadAllAsync(store);

        // Assert
        Assert.AreEqual(1L, result.Created, "the rebuilt incident is new");
        Assert.AreEqual(1L, result.Kept, "the old incident is kept");
        Assert.HasCount(2, stored, "both incidents are stored");
        Assert.AreEqual(JsonSerializer.Serialize(old), JsonSerializer.Serialize(stored[0].Incident), "the old incident is unchanged");
        Assert.AreEqual(savedAt, stored[0].UpdatedAt, "updated_at of the old incident is untouched");
    }

    /// <summary>
    /// An incident that the store refuses is skipped and counted, and the run goes on with the next one.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IncidentReconstructorSkipsInvalidIncidentAndContinues()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var clock = new FakeTimeProvider(IncidentSamples.Base.AddDays(1));
        var refusing = new RefusingIncidentStore(store, 2);
        var saved = new List<SavedIncident>();

        await IncidentSamples.WriteAsync(store, TestContext.CancellationToken, IncidentSamples.Kill(At(0)), IncidentSamples.Kill(At(180)), IncidentSamples.Kill(At(360)));

        // Act
        var result = await RunAsync(store, refusing, clock, saved);
        var stored = await ReadAllAsync(store);

        // Assert
        Assert.AreEqual(3, refusing.Saves, "all three incidents were offered to the store");
        Assert.AreEqual(3L, result.Incidents, "built incidents, the skipped one included");
        Assert.AreEqual(3L, result.Kills, "kills");
        Assert.AreEqual(2L, result.Created, "created");
        Assert.AreEqual(1L, result.Skipped, "skipped");
        Assert.AreEqual(0L, result.Kept, "nothing is left over");
        Assert.AreSequenceEqual([At(0), At(360)], saved.Select(item => item.Incident.FirstKillAt), "the first and the third incident were passed on");
        Assert.AreSequenceEqual([At(0), At(360)], stored.Select(row => row.Incident.FirstKillAt), "the first and the third incident are stored");
    }

    /// <summary>
    /// A reader that returns the same full page again makes the run fail instead of looping.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IncidentReconstructorFailsWhenPageDoesNotAdvance()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var reader = new RepeatingRecordReader();
        var reconstructor = new IncidentReconstructor(reader, store, store, BuiltInSignatures.Catalog, _before, _after, new FakeTimeProvider(IncidentSamples.Base));

        // Act
        var exception = await Assert.ThrowsExactlyAsync<StoreException>(() => reconstructor.RunAsync(_ => Task.CompletedTask, TestContext.CancellationToken), "the page does not advance");

        // Assert
        Assert.AreEqual("store: reading records: page did not advance", exception.Message, "message");
        Assert.IsLessThan(5, reader.Calls, "the run stops after the page repeated");
    }

    /// <summary>
    /// The spike query of an incident longer than 744 hours covers its first 744 hours, and the query for new templates covers the whole window.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IncidentReconstructorClipsSpikeRangeToMaxHours()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var clock = new FakeTimeProvider(IncidentSamples.Base.AddYears(1));
        var templates = new RecordingTemplateReader(store);
        var window = TimeSpan.FromHours(24);
        var reconstructor = new IncidentReconstructor(store, templates, store, BuiltInSignatures.Catalog, window, window, clock);
        var kills = Enumerable.Range(0, 20).Select(index => IncidentSamples.Kill(IncidentSamples.Base.AddHours(40 * index))).ToArray();
        var start = IncidentSamples.Base - window;
        var end = IncidentSamples.Base.AddHours(40 * 19) + window;

        await IncidentSamples.WriteAsync(store, TestContext.CancellationToken, kills);

        // Act
        var result = await reconstructor.RunAsync(_ => Task.CompletedTask, TestContext.CancellationToken);
        var stored = await ReadAllAsync(store);

        // Assert
        Assert.AreEqual(1L, result.Incidents, "the chained kills form one incident");
        Assert.HasCount(1, stored, "one stored incident");
        Assert.AreEqual(20L, stored[0].Incident.KillCount, "kills");
        Assert.IsGreaterThan(TimeSpan.FromHours(744), stored[0].Incident.End - stored[0].Incident.Start, "the window is longer than 744 hours");
        Assert.HasCount(1, templates.SpikeQueries, "one spike query");
        Assert.AreEqual(start, templates.SpikeQueries[0].From, "the spike query starts with the incident");
        Assert.AreEqual(start.AddHours(StorageLimits.MaxSpikeRangeHours), templates.SpikeQueries[0].To, "the spike query is clipped to 744 hours");
        Assert.AreEqual(20, templates.SpikeQueries[0].Limit, "spike limit");
        Assert.HasCount(1, templates.NewTemplateQueries, "one query for new templates");
        Assert.AreEqual(start, templates.NewTemplateQueries[0].From, "the new template query starts with the incident");
        Assert.AreEqual(end, templates.NewTemplateQueries[0].To, "the new template query covers the whole window");
        Assert.AreEqual(20, templates.NewTemplateQueries[0].Limit, "new template limit");
    }

    /// <summary>
    /// Snapshots are read one per page, lines and metrics a thousand per page, and every snapshot once per incident window.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IncidentReconstructorReadsSnapshotsOnePerPage()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var clock = new FakeTimeProvider(IncidentSamples.Base.AddDays(1));
        var recording = new RecordingRecordReader(store);
        var reconstructor = new IncidentReconstructor(recording, store, store, BuiltInSignatures.Catalog, _before, _after, clock);
        var records = new List<DataRecord>();

        foreach (var offset in new[] { 0, 300 })
        {
            records.Add(IncidentSamples.Kill(At(offset)));
            records.Add(IncidentSamples.Processes(At(offset - 30), ("web", 100)));
            records.Add(IncidentSamples.Processes(At(offset - 20), ("web", 200)));
            records.Add(IncidentSamples.Processes(At(offset - 10), ("web", 300)));
            records.Add(IncidentSamples.Connections(At(offset - 20), 5, "web"));
            records.Add(IncidentSamples.Connections(At(offset - 10), 9, "web"));
            records.Add(IncidentSamples.Metric(At(offset - 5), "load1", 1.5));
        }

        await IncidentSamples.WriteAsync(store, TestContext.CancellationToken, [.. records]);

        // Act
        var result = await reconstructor.RunAsync(_ => Task.CompletedTask, TestContext.CancellationToken);

        // Assert
        var processQueries = recording.Queries.Where(query => query.Kind == RecordKind.ProcessSnapshot).ToList();
        var connectionQueries = recording.Queries.Where(query => query.Kind == RecordKind.ConnectionSnapshot).ToList();
        var lineQueries = recording.Queries.Where(query => query.Kind == RecordKind.LogLine).ToList();
        var metricQueries = recording.Queries.Where(query => query.Kind == RecordKind.Metric).ToList();
        var processIds = recording.Returned.Where(row => row.Kind == RecordKind.ProcessSnapshot).Select(row => row.Id).ToList();
        var connectionIds = recording.Returned.Where(row => row.Kind == RecordKind.ConnectionSnapshot).Select(row => row.Id).ToList();

        Assert.AreEqual(2L, result.Created, "two incidents");
        Assert.IsNotEmpty(processQueries, "process snapshots were read");
        Assert.IsNotEmpty(connectionQueries, "connection snapshots were read");
        Assert.IsNotEmpty(lineQueries, "log lines were read");
        Assert.IsNotEmpty(metricQueries, "metrics were read");
        Assert.IsTrue(processQueries.All(query => query.Limit == 1), "process snapshots are read one per page");
        Assert.IsTrue(connectionQueries.All(query => query.Limit == 1), "connection snapshots are read one per page");
        Assert.IsTrue(lineQueries.All(query => query.Limit == 1000), "log lines are read a thousand per page");
        Assert.IsTrue(metricQueries.All(query => query.Limit == 1000), "metrics are read a thousand per page");
        Assert.HasCount(6, processIds, "every process snapshot was returned");
        Assert.HasCount(6, processIds.Distinct().ToList(), "every process snapshot was read exactly once");
        Assert.HasCount(4, connectionIds, "every connection snapshot was returned");
        Assert.HasCount(4, connectionIds.Distinct().ToList(), "every connection snapshot was read exactly once");
    }

    /// <summary>
    /// Returns a time relative to the first kill of the samples.
    /// </summary>
    /// <param name="minutes">Minutes after the first kill, negative before it</param>
    /// <returns>The time</returns>
    private static DateTimeOffset At(int minutes)
    {
        return IncidentSamples.Base.AddMinutes(minutes);
    }

    /// <summary>
    /// Runs the reconstruction with the built-in catalog and the default windows.
    /// </summary>
    /// <param name="store">The store that is read</param>
    /// <param name="incidents">The store that saves the incidents</param>
    /// <param name="clock">The clock</param>
    /// <param name="saved">Receives every saved incident</param>
    /// <returns>A task that returns the result</returns>
    private Task<ReconstructionResult> RunAsync(SqliteStore store, IIncidentStore incidents, FakeTimeProvider clock, List<SavedIncident> saved)
    {
        var reconstructor = new IncidentReconstructor(store, store, incidents, BuiltInSignatures.Catalog, _before, _after, clock);

        return reconstructor.RunAsync(item =>
                                      {
                                          saved.Add(item);

                                          return Task.CompletedTask;
                                      },
                                      TestContext.CancellationToken);
    }

    /// <summary>
    /// Reads every stored incident.
    /// </summary>
    /// <param name="store">The store</param>
    /// <returns>A task that returns the incidents</returns>
    private Task<IReadOnlyList<StoredIncident>> ReadAllAsync(SqliteStore store)
    {
        return store.IncidentsAsync(new IncidentQuery
                                    {
                                        From = StorableTime.Min,
                                        To = StorableTime.Max,
                                        Limit = 100
                                    },
                                    TestContext.CancellationToken);
    }

    #endregion // Methods
}