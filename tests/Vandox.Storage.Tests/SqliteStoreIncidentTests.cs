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
        var incident = Samples.WorstCase();
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
    /// Stored incident data that is no incident is refused with a fixed text, with the decoding error as the cause when there is one.
    /// </summary>
    /// <param name="data">The stored data</param>
    /// <param name="hasCause">Whether the decoder gave an error</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("{", true, DisplayName = "text that is no JSON")]
    [DataRow("null", false, DisplayName = "the JSON text null")]
    public async Task SqliteStoreIncidentsRefusesUndecodableData(string data, bool hasCause)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await store.SaveIncidentAsync(Sample(0, 2), Samples.Base.AddDays(1), TestContext.CancellationToken);
        await ExecuteAsync(Path.Combine(directory.Path, StorageLimits.FileName), $"UPDATE incidents SET data = '{data}'");

        // Act
        var exception = await Assert.ThrowsExactlyAsync<StoreException>(() => ReadAsync(store, StorableTime.Min, StorableTime.Max, 10), "reading undecodable data");

        // Assert
        Assert.AreEqual("store: reading incidents: decoding incident data", exception.Message, "message");
        Assert.AreEqual(hasCause, exception.InnerException is JsonException, "the decoding error is the cause");
    }

    /// <summary>
    /// Stored incident data that is not valid UTF-8 is refused with the same fixed text and the decoding error as the cause.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreIncidentsRefusesInvalidUtf8Data()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await store.SaveIncidentAsync(Sample(0, 2), Samples.Base.AddDays(1), TestContext.CancellationToken);
        await ExecuteAsync(Path.Combine(directory.Path, StorageLimits.FileName), "UPDATE incidents SET data = CAST(x'7B226B696C6C73223A5B7B22686F7374223A22C328FF227D5D7D' AS TEXT)");

        // Act
        var exception = await Assert.ThrowsExactlyAsync<StoreException>(() => ReadAsync(store, StorableTime.Min, StorableTime.Max, 10), "reading data that is not UTF-8");

        // Assert
        Assert.AreEqual("store: reading incidents: decoding incident data", exception.Message, "message");
        Assert.IsInstanceOfType<JsonException>(exception.InnerException, "the decoding error is the cause");
    }

    /// <summary>
    /// An incident whose texts hold characters the JSON encoder escapes or that need several code units is read back equal to the saved one, and the stored data holds the escape.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreIncidentsRoundTripsEscapedText()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var text = string.Concat("<", ((char)0xE4).ToString(), ((char)0x202E).ToString(), char.ConvertFromUtf32(0x1F600));
        var incident = Sample(0, 2);

        incident.Kills.Add(new IncidentKill
                           {
                               At = Hour(1),
                               Host = text,
                               VictimCommand = text,
                               RecordIds = [1],
                               LineCount = 1
                           });
        incident.Timeline.Add(new TimelineEntry
                              {
                                  At = Hour(1),
                                  Type = "boot",
                                  Host = text,
                                  Texts = new Dictionary<string, string>
                                          {
                                              ["unit"] = text
                                          }
                              });

        var path = Path.Combine(directory.Path, StorageLimits.FileName);

        // Act
        await store.SaveIncidentAsync(incident, Samples.Base.AddDays(1), TestContext.CancellationToken);

        var read = await ReadAllAsync(store);
        var stored = await StoredDataAsync(path);

        // Assert
        Assert.HasCount(1, read, "one incident");
        Assert.AreEqual(text, read[0].Incident.Kills[0].Host, "kill host");
        Assert.AreEqual(text, read[0].Incident.Kills[0].VictimCommand, "victim command");
        Assert.AreEqual(text, read[0].Incident.Timeline[0].Host, "timeline host");
        Assert.AreEqual(text, read[0].Incident.Timeline[0].Texts["unit"], "timeline text");
        Assert.AreEqual(JsonSerializer.Serialize(incident, IncidentJson.Options), JsonSerializer.Serialize(read[0].Incident, IncidentJson.Options), "the incident is read back as it was saved");
        Assert.Contains(string.Concat((char)92, "u003C"), stored, "the stored data holds < as its six-character escape");
        Assert.DoesNotContain("<", stored, "the stored data holds no raw <");
    }

    /// <summary>
    /// A database error in a save, a read or a count becomes a store error with the database error as the cause.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreIncidentMethodsWrapDatabaseErrors()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await ExecuteAsync(Path.Combine(directory.Path, StorageLimits.FileName), "DROP TABLE incidents");

        // Act
        var save = await Assert.ThrowsExactlyAsync<StoreException>(() => store.SaveIncidentAsync(Sample(0, 2), Samples.Base.AddDays(1), TestContext.CancellationToken), "save without the table");
        var read = await Assert.ThrowsExactlyAsync<StoreException>(() => ReadAsync(store, StorableTime.Min, StorableTime.Max, 10), "read without the table");
        var count = await Assert.ThrowsExactlyAsync<StoreException>(() => store.CountIncidentsAsync(TestContext.CancellationToken), "count without the table");

        // Assert
        Assert.StartsWith("store: saving incident: ", save.Message, "message of the save");
        Assert.IsInstanceOfType<SqliteException>(save.InnerException, "cause of the save");
        Assert.StartsWith("store: reading incidents: ", read.Message, "message of the read");
        Assert.IsInstanceOfType<SqliteException>(read.InnerException, "cause of the read");
        Assert.StartsWith("store: reading incidents: ", count.Message, "message of the count");
        Assert.IsInstanceOfType<SqliteException>(count.InnerException, "cause of the count");
    }

    /// <summary>
    /// An updated and an unchanged save leave the notes of the incident as they are.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreSaveIncidentKeepsNotesWhenUpdatedOrUnchanged()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var firstSave = Samples.Base.AddDays(1);
        var created = await store.SaveIncidentAsync(Sample(0, 2), firstSave, TestContext.CancellationToken);
        var noteTime = firstSave.AddMinutes(1);

        await store.SaveIncidentNoteAsync(new IncidentNoteEdit
                                          {
                                              IncidentId = created.Id,
                                              WrittenFor = created.Id,
                                              Text = "found the cause",
                                              SavedAt = noteTime
                                          },
                                          TestContext.CancellationToken);

        var changed = Sample(0, 2);

        changed.KillCount = 2;

        // Act
        var unchanged = await store.SaveIncidentAsync(Sample(0, 2), firstSave.AddMinutes(5), TestContext.CancellationToken);
        var afterUnchanged = await store.IncidentNotesAsync(created.Id, TestContext.CancellationToken);
        var updated = await store.SaveIncidentAsync(changed, firstSave.AddMinutes(10), TestContext.CancellationToken);
        var afterUpdate = await store.IncidentNotesAsync(created.Id, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(IncidentSaveOutcome.Unchanged, unchanged.Outcome, "an equal save");
        Assert.HasCount(1, afterUnchanged.Notes, "the note survives an unchanged save");
        Assert.AreEqual(IncidentSaveOutcome.Updated, updated.Outcome, "a changed save");
        Assert.AreEqual(created.Id, updated.Id, "the number is kept");
        Assert.HasCount(1, afterUpdate.Notes, "the note survives an update");
        Assert.AreEqual("found the cause", afterUpdate.Notes[0].Text, "the text of the note");
        Assert.AreEqual(created.Id, afterUpdate.Notes[0].IncidentId, "the note still belongs to the incident");
        Assert.AreEqual(noteTime, afterUpdate.Notes[0].UpdatedAt, "the note is not touched by the update");
    }

    /// <summary>
    /// A save that merges incidents moves the notes of every incident it deletes to the kept one, each note keeping the number it was written for.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreSaveIncidentMovesNotesOfMergedIncidents()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var saved = Samples.Base.AddDays(1);
        var first = await store.SaveIncidentAsync(Sample(0, 2), saved, TestContext.CancellationToken);
        var second = await store.SaveIncidentAsync(Sample(3, 5), saved, TestContext.CancellationToken);
        var third = await store.SaveIncidentAsync(Sample(6, 8), saved, TestContext.CancellationToken);
        var untouched = await store.SaveIncidentAsync(Sample(20, 22), saved, TestContext.CancellationToken);
        var noteTime = saved.AddMinutes(1);

        foreach (var incident in new[] { first, second, third, untouched })
        {
            await store.SaveIncidentNoteAsync(new IncidentNoteEdit
                                              {
                                                  IncidentId = incident.Id,
                                                  WrittenFor = incident.Id,
                                                  Text = $"note of {incident.Id}",
                                                  SavedAt = noteTime
                                              },
                                              TestContext.CancellationToken);
        }

        var bridge = Sample(1, 7);

        bridge.KillCount = 2;

        // Act
        var merged = await store.SaveIncidentAsync(bridge, saved.AddMinutes(2), TestContext.CancellationToken);
        var kept = await store.IncidentNotesAsync(first.Id, TestContext.CancellationToken);
        var gone = await store.IncidentNotesAsync(second.Id, TestContext.CancellationToken);
        var other = await store.IncidentNotesAsync(untouched.Id, TestContext.CancellationToken);
        var edited = await store.SaveIncidentNoteAsync(new IncidentNoteEdit
                                                       {
                                                           IncidentId = first.Id,
                                                           WrittenFor = third.Id,
                                                           Text = "edited after the merge",
                                                           SavedAt = noteTime.AddMinutes(1)
                                                       },
                                                       TestContext.CancellationToken);
        var afterEdit = await store.IncidentNotesAsync(first.Id, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(2L, merged.Merged, "two incidents merged away");
        Assert.AreEqual(first.Id, merged.Id, "the oldest number is kept");
        Assert.AreSequenceEqual(new[] { first.Id, second.Id, third.Id }, kept.Notes.Select(note => note.WrittenFor).ToArray(), "the kept incident holds the notes written for the three incidents");
        Assert.IsTrue(kept.Notes.All(note => note.IncidentId == first.Id), "every note belongs to the kept incident");
        Assert.AreSequenceEqual(new[] { $"note of {first.Id}", $"note of {second.Id}", $"note of {third.Id}" }, kept.Notes.Select(note => note.Text).ToArray(), "no text is lost or joined");
        Assert.IsTrue(kept.Notes.All(note => note.UpdatedAt == noteTime), "the move does not touch the time of a note");
        Assert.IsEmpty(gone.Notes, "the deleted incident holds no note");
        Assert.AreEqual($"note of {untouched.Id}", other.Notes.Single().Text, "the note of an incident that is not merged stays");
        Assert.AreEqual(untouched.Id, other.Notes.Single().IncidentId, "its owner is unchanged");
        Assert.AreEqual(IncidentNoteOutcome.Saved, edited, "a moved note is changed through the incident it belongs to now");
        Assert.AreEqual("edited after the merge", afterEdit.Notes.Single(note => note.WrittenFor == third.Id).Text, "the changed text");
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
    /// Runs a statement on the database file through its own connection.
    /// </summary>
    /// <param name="path">The database file</param>
    /// <param name="sql">The statement</param>
    /// <returns>A task that completes when the statement ran</returns>
    private async Task ExecuteAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");

        await connection.OpenAsync(TestContext.CancellationToken);

        using var command = connection.CreateCommand();

        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(TestContext.CancellationToken);
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

    /// <summary>
    /// Reads the stored data text of the only incident.
    /// </summary>
    /// <param name="path">The database file</param>
    /// <returns>A task that returns the text</returns>
    private async Task<string> StoredDataAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False;Mode=ReadOnly");

        await connection.OpenAsync(TestContext.CancellationToken);

        using var command = connection.CreateCommand();

        command.CommandText = "SELECT data FROM incidents";

        return (string)(await command.ExecuteScalarAsync(TestContext.CancellationToken))!;
    }

    #endregion // Methods
}