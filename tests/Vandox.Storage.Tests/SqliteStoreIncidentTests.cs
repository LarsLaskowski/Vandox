using System.Text.Json;

using Microsoft.Data.Sqlite;

using Vandox.Core.Incidents;
using Vandox.Core.Model;
using Vandox.Core.Signatures;

namespace Vandox.Storage.Tests;

/// <summary>
/// Tests for the incident methods of <see cref="SqliteStore"/>
/// </summary>
[TestClass]
public class SqliteStoreIncidentTests
{
    #region Constants

    private const string Prefix = "store: invalid incident: ";

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// The first save creates an incident, an equal one leaves it untouched, and changed content updates it under the same ID.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreSaveIncidentCreatesUpdatesAndLeavesUnchanged()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var firstSave = Samples.Base.AddDays(1);

        // Act
        var created = await store.SaveIncidentAsync(Sample(0, 2), firstSave, TestContext.CancellationToken);
        var unchanged = await store.SaveIncidentAsync(Sample(0, 2), firstSave.AddMinutes(5), TestContext.CancellationToken);
        var afterUnchanged = await ReadAllAsync(store);
        var changedIncident = Sample(0, 2);

        changedIncident.KillCount = 2;
        changedIncident.Timeline.Add(new TimelineEntry
                                     {
                                         At = Hour(1),
                                         Type = "boot"
                                     });

        var updated = await store.SaveIncidentAsync(changedIncident, firstSave.AddMinutes(10), TestContext.CancellationToken);
        var afterUpdate = await ReadAllAsync(store);

        // Assert
        Assert.AreEqual(IncidentSaveOutcome.Created, created.Outcome, "first save");
        Assert.IsGreaterThan(0, created.Id, "ID of the new incident");
        Assert.AreEqual(0L, created.Merged, "nothing merged");
        Assert.AreEqual(IncidentSaveOutcome.Unchanged, unchanged.Outcome, "equal save");
        Assert.AreEqual(created.Id, unchanged.Id, "ID of the unchanged incident");
        Assert.HasCount(1, afterUnchanged, "one incident after the equal save");
        Assert.AreEqual(firstSave, afterUnchanged[0].UpdatedAt, "updated_at is untouched by an equal save");
        Assert.AreEqual(IncidentSaveOutcome.Updated, updated.Outcome, "changed content");
        Assert.AreEqual(created.Id, updated.Id, "the ID is kept");
        Assert.AreEqual(0L, updated.Merged, "nothing merged");
        Assert.HasCount(1, afterUpdate, "one incident after the update");
        Assert.AreEqual(2L, afterUpdate[0].Incident.KillCount, "new content");
        Assert.AreEqual(firstSave, afterUpdate[0].CreatedAt, "created_at is kept");
        Assert.AreEqual(firstSave.AddMinutes(10), afterUpdate[0].UpdatedAt, "updated_at is the time of the update");
    }

    /// <summary>
    /// An incident that overlaps several stored ones takes the oldest ID and removes the others; one that only touches is not overlapped.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreSaveIncidentMergesOverlappingIntoOldest()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var saved = Samples.Base.AddDays(1);
        var first = await store.SaveIncidentAsync(Sample(0, 2), saved, TestContext.CancellationToken);
        var second = await store.SaveIncidentAsync(Sample(3, 5), saved, TestContext.CancellationToken);
        var bridge = Sample(1, 4);

        bridge.KillCount = 2;

        // Act
        var merged = await store.SaveIncidentAsync(bridge, saved.AddMinutes(1), TestContext.CancellationToken);
        var afterMerge = await ReadAllAsync(store);
        var touchingEnd = await store.SaveIncidentAsync(Sample(4, 6), saved.AddMinutes(2), TestContext.CancellationToken);
        var touchingStart = await store.SaveIncidentAsync(Sample(0, 1), saved.AddMinutes(3), TestContext.CancellationToken);
        var afterTouching = await ReadAllAsync(store);

        // Assert
        Assert.AreNotEqual(first.Id, second.Id, "two separate incidents");
        Assert.AreEqual(IncidentSaveOutcome.Updated, merged.Outcome, "the bridge updates the oldest");
        Assert.AreEqual(first.Id, merged.Id, "the oldest ID is kept");
        Assert.AreEqual(1L, merged.Merged, "one incident merged away");
        Assert.HasCount(1, afterMerge, "one incident left");
        Assert.AreEqual(first.Id, afterMerge[0].Id, "the remaining ID");
        Assert.AreEqual(2L, afterMerge[0].Incident.KillCount, "content of the bridge");
        Assert.AreEqual(IncidentSaveOutcome.Created, touchingEnd.Outcome, "an incident that starts where the stored one ends is new");
        Assert.AreEqual(IncidentSaveOutcome.Created, touchingStart.Outcome, "an incident that ends where the stored one starts is new");
        Assert.AreEqual(0L, touchingEnd.Merged, "nothing merged for the touching end");
        Assert.AreEqual(0L, touchingStart.Merged, "nothing merged for the touching start");
        Assert.HasCount(3, afterTouching, "three incidents");
    }

    /// <summary>
    /// The number of an incident that was merged away is never given to a later incident.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreSaveIncidentNeverReusesNumbers()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var saved = Samples.Base.AddDays(1);
        var first = await store.SaveIncidentAsync(Sample(0, 2), saved, TestContext.CancellationToken);
        var second = await store.SaveIncidentAsync(Sample(3, 5), saved, TestContext.CancellationToken);

        await store.SaveIncidentAsync(Sample(1, 4), saved, TestContext.CancellationToken);

        // Act
        var later = await store.SaveIncidentAsync(Sample(10, 12), saved, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(1L, first.Id, "first number");
        Assert.AreEqual(2L, second.Id, "second number");
        Assert.AreEqual(3L, later.Id, "the number 2 of the incident merged away is not given again");
    }

    /// <summary>
    /// An incident that breaks a rule is refused with a fixed text and not stored.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreSaveIncidentRefusesInvalidIncident()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var saved = Samples.Base.AddDays(1);
        var outside = new DateTimeOffset(1600, 1, 1, 0, 0, 0, TimeSpan.Zero);
        List<(string Name, Incident Incident, DateTimeOffset SavedAt, string Rule)> cases = [
                                                                                                ("start outside", Mutated(incident => incident.Start = outside), saved, "start and end must be within the storable range"),
                                                                                                ("end outside", Mutated(incident => incident.End = DateTimeOffset.MaxValue), saved, "start and end must be within the storable range"),
                                                                                                ("start after end", Mutated(incident => incident.Start = incident.End.AddHours(1)), saved, "start must be before end"),
                                                                                                ("first kill at the end", Mutated(incident => incident.FirstKillAt = incident.End), saved, "the first kill must lie in the window"),
                                                                                                ("first kill before the start", Mutated(incident => incident.FirstKillAt = incident.Start.AddSeconds(-1)), saved, "the first kill must lie in the window"),
                                                                                                ("kill at the last storable instant", LastInstant(), saved, "the first kill must lie in the window"),
                                                                                                ("no kill", Mutated(incident => incident.KillCount = 0), saved, "kill count must be at least 1"),
                                                                                                ("negative kill count", Mutated(incident => incident.KillCount = -1), saved, "kill count must be at least 1"),
                                                                                                ("saved at outside", Sample(0, 2), outside, "saved at must be within the storable range"),
                                                                                                ("too much data", TooLarge(), saved, "data is larger than 8388608 bytes")
                                                                                            ];

        // Act and assert
        foreach (var (name, incident, savedAt, rule) in cases)
        {
            var exception = await Assert.ThrowsExactlyAsync<InvalidIncidentException>(() => store.SaveIncidentAsync(incident, savedAt, TestContext.CancellationToken), name);

            Assert.AreEqual(Prefix + rule, exception.Message, $"message for {name}");
        }

        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => store.SaveIncidentAsync(null!, saved, TestContext.CancellationToken), "null incident");

        Assert.AreEqual(0L, await store.CountIncidentsAsync(TestContext.CancellationToken), "no refused incident is stored");
    }

    /// <summary>
    /// The incident that the builder produces from the largest possible escaped text still fits the data bound.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreSaveIncidentAcceptsWorstCaseIncident()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var incident = WorstCase();
        var json = JsonSerializer.Serialize(incident, IncidentJson.Options);
        var escaped = string.Concat((char)92, "u003C");

        // Act
        var result = await store.SaveIncidentAsync(incident, Samples.Base.AddDays(1), TestContext.CancellationToken);
        var size = await DataBytesAsync(Path.Combine(directory.Path, StorageLimits.FileName));

        // Assert
        Assert.Contains(escaped, json, "the serialized incident holds the six characters of an escaped <");
        Assert.IsGreaterThan(0L, incident.TimelineOmitted, "the timeline budget cut events");
        Assert.HasCount(3, incident.Hypothesis.Growth, "full growth list");
        Assert.HasCount(3, incident.Hypothesis.Consumers, "full consumers list");
        Assert.HasCount(16, incident.Kills, "kept kills");
        Assert.HasCount(20, incident.NewTemplates, "kept new templates");
        Assert.HasCount(20, incident.TemplateSpikes, "kept spikes");
        Assert.AreEqual(IncidentSaveOutcome.Created, result.Outcome, "the worst case is saved");
        Assert.IsLessThanOrEqualTo(StorageLimits.MaxIncidentDataBytes, size, "stored data bytes");
        Assert.IsGreaterThan(1_000_000L, size, "the stored document is large");
    }

    /// <summary>
    /// Incidents overlapping a range are read ordered by start and ID, limited, and equal to what was saved; the window rules apply.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreIncidentsReadsOverlappingInOrder()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var saved = Samples.Base.AddDays(1);
        var late = Sample(10, 12);
        var early = Sample(2, 4);
        var middle = Sample(6, 8);

        early.Kills.Add(new IncidentKill
                        {
                            At = Hour(3),
                            Host = "web-1",
                            VictimCommand = "mariadbd",
                            VictimPid = 4242,
                            RecordIds = [7, 8],
                            LineCount = 2
                        });
        await store.SaveIncidentAsync(late, saved, TestContext.CancellationToken);
        await store.SaveIncidentAsync(early, saved, TestContext.CancellationToken);
        await store.SaveIncidentAsync(middle, saved, TestContext.CancellationToken);

        // Act
        var all = await ReadAsync(store, StorableTime.Min, StorableTime.Max, 100);
        var between = await ReadAsync(store, Hour(4), Hour(10), 100);
        var inside = await ReadAsync(store, Hour(3), Hour(7), 100);
        var limited = await ReadAsync(store, StorableTime.Min, StorableTime.Max, 1);
        var count = await store.CountIncidentsAsync(TestContext.CancellationToken);
        var missing = await Assert.ThrowsExactlyAsync<InvalidQueryException>(() => ReadAsync(store, default, Hour(1), 10), "from missing");
        var order = await Assert.ThrowsExactlyAsync<InvalidQueryException>(() => ReadAsync(store, Hour(1), Hour(1), 10), "from not before to");
        var tooMany = await Assert.ThrowsExactlyAsync<InvalidQueryException>(() => ReadAsync(store, Hour(1), Hour(2), 0), "limit zero");

        // Assert
        Assert.AreSequenceEqual([2L, 3L, 1L], all.Select(row => row.Id), "ordered by start, not by ID");
        Assert.AreSequenceEqual([3L], between.Select(row => row.Id), "incidents that only touch the range are not read");
        Assert.AreSequenceEqual([2L, 3L], inside.Select(row => row.Id), "incidents overlapping the range");
        Assert.AreSequenceEqual([2L], limited.Select(row => row.Id), "the limit keeps the first");
        Assert.AreEqual(3L, count, "number of incidents");
        Assert.AreEqual(JsonSerializer.Serialize(early), JsonSerializer.Serialize(all[0].Incident), "the incident is read back as it was saved");
        Assert.AreEqual(saved, all[0].CreatedAt, "created_at");
        Assert.AreEqual(saved, all[0].UpdatedAt, "updated_at");
        Assert.AreEqual("store: invalid query: from and to are required", missing.Message, "message of a missing from");
        Assert.AreEqual("store: invalid query: from must be before to", order.Message, "message of an empty range");
        Assert.AreEqual("store: invalid query: limit must be between 1 and 10000", tooMany.Message, "message of a limit of zero");
    }

    /// <summary>
    /// Returns a time in hours after the base time of the samples.
    /// </summary>
    /// <param name="hours">The hours</param>
    /// <returns>The time</returns>
    private static DateTimeOffset Hour(int hours)
    {
        return Samples.Base.AddHours(hours);
    }

    /// <summary>
    /// Creates a small incident whose window runs between two hours, with its first kill one hour after the start.
    /// </summary>
    /// <param name="startHour">The hour of the start</param>
    /// <param name="endHour">The hour of the end</param>
    /// <returns>The incident</returns>
    private static Incident Sample(int startHour, int endHour)
    {
        return new Incident
               {
                   Start = Hour(startHour),
                   End = Hour(endHour),
                   WindowBeforeSeconds = 3600,
                   WindowAfterSeconds = 1800,
                   FirstKillAt = Hour(startHour).AddMinutes(30),
                   LastKillAt = Hour(startHour).AddMinutes(30),
                   KillCount = 1,
                   Hypothesis = new IncidentHypothesis
                                {
                                    SnapshotCount = 2,
                                    GrowthKnown = true
                                }
               };
    }

    /// <summary>
    /// Creates a small incident and changes it.
    /// </summary>
    /// <param name="change">The change</param>
    /// <returns>The incident</returns>
    private static Incident Mutated(Action<Incident> change)
    {
        var incident = Sample(0, 2);

        change(incident);

        return incident;
    }

    /// <summary>
    /// Creates an incident that ends at the last storable instant and whose first kill lies at that instant.
    /// </summary>
    /// <returns>The incident</returns>
    private static Incident LastInstant()
    {
        return Mutated(incident =>
                       {
                           incident.Start = StorableTime.Max.AddHours(-1);
                           incident.End = StorableTime.Max;
                           incident.FirstKillAt = StorableTime.Max;
                           incident.LastKillAt = StorableTime.Max;
                       });
    }

    /// <summary>
    /// Creates an incident whose serialized data is larger than the bound.
    /// </summary>
    /// <returns>The incident</returns>
    private static Incident TooLarge()
    {
        var text = new string('a', 1024);

        return Mutated(incident =>
                       {
                           for (var index = 0; index < 3000; index++)
                           {
                               incident.Timeline.Add(new TimelineEntry
                                                     {
                                                         At = Hour(1),
                                                         Type = "service_start",
                                                         Host = text,
                                                         Texts = new Dictionary<string, string>
                                                                 {
                                                                     ["unit"] = text,
                                                                     ["description"] = text
                                                                 }
                                                     });
                           }
                       });
    }

    /// <summary>
    /// Builds the largest incident the builder can produce from texts that the JSON encoder writes with six bytes per character.
    /// </summary>
    /// <returns>The incident</returns>
    private static Incident WorstCase()
    {
        var text = new string('<', 1024);
        var kill = Hour(1);
        var cluster = new KillCluster
                      {
                          Start = kill.AddHours(-1),
                          End = kill.AddMinutes(45),
                          FirstKillAt = kill,
                          LastKillAt = kill.AddMinutes(15),
                          KillCount = 20,
                          Kills = Enumerable.Range(0, 16)
                                            .Select(index => new IncidentKill
                                                             {
                                                                 At = kill.AddSeconds(index),
                                                                 Host = text,
                                                                 VictimPid = long.MaxValue,
                                                                 VictimCommand = text,
                                                                 AnonRssBytes = long.MaxValue,
                                                                 OomScoreAdj = long.MinValue,
                                                                 RecordIds = Enumerable.Repeat(long.MaxValue, 16).ToList(),
                                                                 LineCount = long.MaxValue
                                                             })
                                            .ToList()
                      };
        var builder = new IncidentBuilder(cluster, TimeSpan.FromHours(1), TimeSpan.FromMinutes(30));

        for (var index = 0; index < 1100; index++)
        {
            builder.AddEvent(HeavyEvent(index == 0 ? IncidentSignatures.Boot : IncidentSignatures.ServiceStart, kill.AddMinutes(-59).AddSeconds(index), text));
        }

        builder.AddProcessSnapshot(1, kill.AddMinutes(-30), HeavyPrograms(0));
        builder.AddProcessSnapshot(2, kill.AddMinutes(-10), HeavyPrograms(1));
        builder.AddConnectionSnapshot(3, kill.AddMinutes(-20), HeavyConnections(text));
        AddHeavyMetrics(builder, kill);
        AddHeavyTemplates(builder, kill, text);

        return builder.Build();
    }

    /// <summary>
    /// Creates a detected event whose texts, host and record IDs are at their caps.
    /// </summary>
    /// <param name="signatureId">The signature ID</param>
    /// <param name="at">The time</param>
    /// <param name="text">The text of 1,024 bytes</param>
    /// <returns>The event</returns>
    private static DetectedEvent HeavyEvent(string signatureId, DateTimeOffset at, string text)
    {
        return new DetectedEvent
               {
                   SignatureId = signatureId,
                   CapturedAt = at,
                   Host = text,
                   Texts = new Dictionary<string, string>
                           {
                               ["unit"] = text,
                               ["description"] = text
                           },
                   RecordIds = Enumerable.Repeat(long.MaxValue, 256).ToList(),
                   LineCount = long.MaxValue
               };
    }

    /// <summary>
    /// Creates a process snapshot of 4,096 programs with names of 1,024 bytes of <c>&lt;</c> and a backup program.
    /// </summary>
    /// <param name="step">0 for the first snapshot, 1 for the later one in which every program grew</param>
    /// <returns>The snapshot</returns>
    private static ProcessSnapshot HeavyPrograms(int step)
    {
        var programs = Enumerable.Range(0, 4096)
                                 .Select(index => new ProgramAggregate
                                                  {
                                                      Program = new string('<', 1018) + index.ToString("D6", System.Globalization.CultureInfo.InvariantCulture),
                                                      Count = 1,
                                                      RssBytes = (ulong)(1000 + index + (step * ((index % 7) + 1)))
                                                  })
                                 .ToList();

        programs.Add(new ProgramAggregate
                     {
                         Program = "pzstd",
                         Count = 1,
                         RssBytes = 200
                     });

        return new ProcessSnapshot
               {
                   Complete = true,
                   Programs = programs
               };
    }

    /// <summary>
    /// Creates a connection snapshot with three processes whose commands are at their cap.
    /// </summary>
    /// <param name="text">The text of 1,024 bytes</param>
    /// <returns>The snapshot</returns>
    private static ConnectionSnapshot HeavyConnections(string text)
    {
        return new ConnectionSnapshot
               {
                   Complete = true,
                   States = [
                                new StateCount
                                {
                                    Proto = Proto.Tcp,
                                    State = "ESTABLISHED",
                                    Count = 5
                                }
                            ],
                   Processes = [
                                   new ProcessConnections
                                   {
                                       Pid = 1,
                                       Command = text,
                                       Count = 3
                                   },
                                   new ProcessConnections
                                   {
                                       Pid = 2,
                                       Command = text + "a",
                                       Count = 2
                                   },
                                   new ProcessConnections
                                   {
                                       Pid = 3,
                                       Command = text + "b",
                                       Count = 1
                                   }
                               ]
               };
    }

    /// <summary>
    /// Adds a sample of each metric definition, with a source of 128 bytes of <c>&lt;</c>.
    /// </summary>
    /// <param name="builder">The builder</param>
    /// <param name="kill">The time of the first kill</param>
    private static void AddHeavyMetrics(IncidentBuilder builder, DateTimeOffset kill)
    {
        var source = new string('<', 128);
        var memory = new Dictionary<string, string>
                     {
                         ["resource"] = "memory",
                         ["kind"] = "some"
                     };
        var full = new Dictionary<string, string>
                   {
                       ["resource"] = "memory",
                       ["kind"] = "full"
                   };

        builder.AddMetric(source,
                          kill.AddMinutes(-20),
                          new MetricPoint
                          {
                              Name = "mem_available_bytes",
                              Value = double.MaxValue
                          });
        builder.AddMetric(source,
                          kill.AddMinutes(-20),
                          new MetricPoint
                          {
                              Name = "swap_used_bytes",
                              Value = double.MaxValue
                          });
        builder.AddMetric(source,
                          kill.AddMinutes(-20),
                          new MetricPoint
                          {
                              Name = "pressure_avg10",
                              Value = double.MaxValue,
                              Labels = memory
                          });
        builder.AddMetric(source,
                          kill.AddMinutes(-20),
                          new MetricPoint
                          {
                              Name = "pressure_avg10",
                              Value = double.MaxValue,
                              Labels = full
                          });
        builder.AddMetric(source,
                          kill.AddMinutes(-20),
                          new MetricPoint
                          {
                              Name = "load1",
                              Value = double.MaxValue
                          });
    }

    /// <summary>
    /// Adds 25 new templates and 25 spikes with texts at their caps.
    /// </summary>
    /// <param name="builder">The builder</param>
    /// <param name="kill">The time of the first kill</param>
    /// <param name="text">The text of 1,024 bytes</param>
    private static void AddHeavyTemplates(IncidentBuilder builder, DateTimeOffset kill, string text)
    {
        var source = new string('<', 128);

        for (var index = 0; index < 25; index++)
        {
            builder.AddNewTemplate(new IncidentNewTemplate
                                   {
                                       Source = source,
                                       Program = text,
                                       Template = text,
                                       FirstSeen = kill,
                                       Count = long.MaxValue
                                   });
            builder.AddTemplateSpike(new IncidentTemplateSpike
                                     {
                                         Source = source,
                                         Program = text,
                                         Template = text,
                                         Hour = kill,
                                         Count = long.MaxValue,
                                         UsualLevel = double.MaxValue
                                     });
        }
    }

    /// <summary>
    /// Reads every stored incident.
    /// </summary>
    /// <param name="store">The store</param>
    /// <returns>A task that returns the incidents</returns>
    private Task<IReadOnlyList<StoredIncident>> ReadAllAsync(SqliteStore store)
    {
        return ReadAsync(store, StorableTime.Min, StorableTime.Max, 100);
    }

    /// <summary>
    /// Reads the incidents that overlap a range.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="from">The start of the range</param>
    /// <param name="to">The end of the range</param>
    /// <param name="limit">The limit</param>
    /// <returns>A task that returns the incidents</returns>
    private Task<IReadOnlyList<StoredIncident>> ReadAsync(SqliteStore store, DateTimeOffset from, DateTimeOffset to, int limit)
    {
        return store.IncidentsAsync(new IncidentQuery
                                    {
                                        From = from,
                                        To = to,
                                        Limit = limit
                                    },
                                    TestContext.CancellationToken);
    }

    /// <summary>
    /// Reads the size in UTF-8 bytes of the stored data of the only incident.
    /// </summary>
    /// <param name="path">The database file</param>
    /// <returns>A task that returns the size</returns>
    private async Task<long> DataBytesAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False;Mode=ReadOnly");

        await connection.OpenAsync(TestContext.CancellationToken);

        using var command = connection.CreateCommand();

        command.CommandText = "SELECT length(CAST(data AS BLOB)) FROM incidents";

        return (long)(await command.ExecuteScalarAsync(TestContext.CancellationToken))!;
    }

    #endregion // Methods
}