using System.Text.Json;

using Microsoft.Data.Sqlite;

using Vandox.Core.Incidents;
using Vandox.Core.Model;

namespace Vandox.Storage.Tests;

/// <summary>
/// Tests for the incident browser methods of <see cref="SqliteStore"/>: the summaries, one incident and the notes
/// </summary>
[TestClass]
public class SqliteStoreIncidentBrowserTests
{
    #region Constants

    private const string NotePrefix = "store: invalid note: ";

    private const string QueryPrefix = "store: invalid query: ";

    private const string DecodingMessage = "store: reading incidents: decoding incident data";

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// A note is stored, read back, replaced under the same number and accepted at 1,800 characters of one and of three UTF-8 bytes.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreSaveIncidentNoteStoresReadsAndReplacesNote()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var id = (await SaveIncidentAsync(store, 0, 2)).Id;
        var firstSave = Samples.Base.AddDays(2);
        var secondSave = firstSave.AddMinutes(1);
        var euros = new string((char)0x20AC, StorageLimits.MaxNoteChars);

        // Act
        var stored = await store.SaveIncidentNoteAsync(Edit(id, id, "first note", firstSave), TestContext.CancellationToken);
        var afterFirst = await store.IncidentNotesAsync(id, TestContext.CancellationToken);
        var replaced = await store.SaveIncidentNoteAsync(Edit(id, id, "second note\nwith two lines", secondSave), TestContext.CancellationToken);
        var afterSecond = await store.IncidentNotesAsync(id, TestContext.CancellationToken);
        var longest = await store.SaveIncidentNoteAsync(Edit(id, id, new string('a', StorageLimits.MaxNoteChars), secondSave), TestContext.CancellationToken);
        var widest = await store.SaveIncidentNoteAsync(Edit(id, id, euros, secondSave), TestContext.CancellationToken);
        var afterWidest = await store.IncidentNotesAsync(id, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(IncidentNoteOutcome.Saved, stored, "the first save");
        Assert.HasCount(1, afterFirst.Notes, "one note after the first save");
        Assert.AreEqual("first note", afterFirst.Notes[0].Text, "text of the first save");
        Assert.AreEqual(id, afterFirst.Notes[0].IncidentId, "the incident of the note");
        Assert.AreEqual(id, afterFirst.Notes[0].WrittenFor, "the number the note was written for");
        Assert.AreEqual(firstSave, afterFirst.Notes[0].UpdatedAt, "the time of the first save");
        Assert.IsFalse(afterFirst.More, "nothing more to read");
        Assert.AreEqual(IncidentNoteOutcome.Saved, replaced, "the second save replaces");
        Assert.HasCount(1, afterSecond.Notes, "still one note");
        Assert.AreEqual("second note\nwith two lines", afterSecond.Notes[0].Text, "text of the second save");
        Assert.AreEqual(secondSave, afterSecond.Notes[0].UpdatedAt, "the time of the second save");
        Assert.AreEqual(IncidentNoteOutcome.Saved, longest, "1,800 characters of one byte");
        Assert.AreEqual(IncidentNoteOutcome.Saved, widest, "1,800 characters of three bytes");
        Assert.AreEqual(euros, afterWidest.Notes.Single().Text, "the widest note is read back as it was saved");
    }

    /// <summary>
    /// A save with an empty text deletes the note, also when there is none.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreSaveIncidentNoteWithEmptyTextDeletesNote()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var id = (await SaveIncidentAsync(store, 0, 2)).Id;

        await store.SaveIncidentNoteAsync(Edit(id, id, "to be removed"), TestContext.CancellationToken);

        // Act
        var deleted = await store.SaveIncidentNoteAsync(Edit(id, id, string.Empty), TestContext.CancellationToken);
        var afterDelete = await store.IncidentNotesAsync(id, TestContext.CancellationToken);
        var deletedAgain = await store.SaveIncidentNoteAsync(Edit(id, id, string.Empty), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(IncidentNoteOutcome.Deleted, deleted, "an empty text deletes the note");
        Assert.IsEmpty(afterDelete.Notes, "no note is left");
        Assert.IsFalse(afterDelete.More, "nothing more to read");
        Assert.AreEqual(IncidentNoteOutcome.Deleted, deletedAgain, "an empty text for an incident without a note is Deleted as well");
    }

    /// <summary>
    /// An unknown incident, a note of another incident and a number without a note are <see cref="IncidentNoteOutcome.NotFound"/> and change nothing.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreSaveIncidentNoteForUnknownIncidentOrForeignNoteIsNotFound()
    {
        // Arrange
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, StorageLimits.FileName);

        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await SaveIncidentAsync(store, 0, 2);
        await SaveIncidentAsync(store, 3, 5);
        await SaveIncidentAsync(store, 6, 8);
        await ExecuteAsync(path, "INSERT INTO incident_notes (written_for, incident_id, text, updated_at) VALUES (2, 1, 'foreign', 0)");

        // Act
        var unknownOwn = await store.SaveIncidentNoteAsync(Edit(99, 99, "x"), TestContext.CancellationToken);
        var unknownForeign = await store.SaveIncidentNoteAsync(Edit(99, 2, "x"), TestContext.CancellationToken);
        var otherIncident = await store.SaveIncidentNoteAsync(Edit(3, 2, "taken over"), TestContext.CancellationToken);
        var otherIncidentDelete = await store.SaveIncidentNoteAsync(Edit(3, 2, string.Empty), TestContext.CancellationToken);
        var conflict = await store.SaveIncidentNoteAsync(Edit(2, 2, "conflicting"), TestContext.CancellationToken);
        var noRow = await store.SaveIncidentNoteAsync(Edit(3, 77, "no row"), TestContext.CancellationToken);
        var afterRefusals = await store.IncidentNotesAsync(1, TestContext.CancellationToken);
        var rowsAfterRefusals = await ScalarAsync(path, "SELECT count(*) FROM incident_notes");
        var edited = await store.SaveIncidentNoteAsync(Edit(1, 2, "edited through the incident it belongs to"), TestContext.CancellationToken);
        var afterEdit = await store.IncidentNotesAsync(1, TestContext.CancellationToken);
        var removed = await store.SaveIncidentNoteAsync(Edit(1, 2, string.Empty), TestContext.CancellationToken);
        var afterRemoval = await store.IncidentNotesAsync(1, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(IncidentNoteOutcome.NotFound, unknownOwn, "an unknown incident");
        Assert.AreEqual(IncidentNoteOutcome.NotFound, unknownForeign, "an unknown incident with a number of another one");
        Assert.AreEqual(IncidentNoteOutcome.NotFound, otherIncident, "a note of another incident is not changed");
        Assert.AreEqual(IncidentNoteOutcome.NotFound, otherIncidentDelete, "a note of another incident is not deleted");
        Assert.AreEqual(IncidentNoteOutcome.NotFound, conflict, "an upsert that conflicts with the row of another incident updates nothing");
        Assert.AreEqual(IncidentNoteOutcome.NotFound, noRow, "a number without a note of that incident is no new note");
        Assert.HasCount(1, afterRefusals.Notes, "the foreign note is the only one");
        Assert.AreEqual("foreign", afterRefusals.Notes[0].Text, "the foreign note is unchanged");
        Assert.AreEqual(1L, afterRefusals.Notes[0].IncidentId, "the foreign note still belongs to its incident");
        Assert.AreEqual("1", rowsAfterRefusals, "no row was added by a refused save");
        Assert.AreEqual(IncidentNoteOutcome.Saved, edited, "the incident the note belongs to can change it");
        Assert.AreEqual("edited through the incident it belongs to", afterEdit.Notes.Single().Text, "the changed text");
        Assert.AreEqual(2L, afterEdit.Notes.Single().WrittenFor, "the note keeps the number it was written for");
        Assert.AreEqual(IncidentNoteOutcome.Deleted, removed, "the incident the note belongs to can delete it");
        Assert.IsEmpty(afterRemoval.Notes, "the note is gone");
    }

    /// <summary>
    /// Every rule of a note is refused with its exact text, nothing is stored, and the table itself refuses a text of more than 5,400 bytes or of none.
    /// </summary>
    /// <param name="incidentId">The incident ID of the edit</param>
    /// <param name="writtenFor">The number the note is written for</param>
    /// <param name="text">The kind of the text</param>
    /// <param name="savedAt">The kind of the time</param>
    /// <param name="rule">The rule that is broken</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow(0L, 1L, "plain", "ok", "incident ID must be positive")]
    [DataRow(-1L, 1L, "plain", "ok", "incident ID must be positive")]
    [DataRow(1L, 0L, "plain", "ok", "written for must be positive")]
    [DataRow(1L, -5L, "plain", "ok", "written for must be positive")]
    [DataRow(1L, 1L, "long", "ok", "text is longer than 1800 characters")]
    [DataRow(1L, 1L, "nul-leading", "ok", "text must not contain U+0000")]
    [DataRow(1L, 1L, "nul-embedded", "ok", "text must not contain U+0000")]
    [DataRow(1L, 1L, "lone-high-surrogate", "ok", "text is not valid UTF-16")]
    [DataRow(1L, 1L, "lone-low-surrogate", "ok", "text is not valid UTF-16")]
    [DataRow(1L, 1L, "plain", "before-minimum", "saved at must be within the storable range")]
    [DataRow(1L, 1L, "plain", "after-maximum", "saved at must be within the storable range")]
    public async Task SqliteStoreSaveIncidentNoteRefusesInvalidEdits(long incidentId, long writtenFor, string text, string savedAt, string rule)
    {
        // Arrange
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, StorageLimits.FileName);

        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await SaveIncidentAsync(store, 0, 2);

        var edit = new IncidentNoteEdit
                   {
                       IncidentId = incidentId,
                       WrittenFor = writtenFor,
                       Text = TextOf(text),
                       SavedAt = TimeOf(savedAt)
                   };

        // Act
        var exception = await Assert.ThrowsExactlyAsync<InvalidNoteException>(() => store.SaveIncidentNoteAsync(edit, TestContext.CancellationToken), "the invalid edit");
        var nullEdit = await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => store.SaveIncidentNoteAsync(null!, TestContext.CancellationToken), "no edit");
        var nullText = await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => store.SaveIncidentNoteAsync(Edit(1, 1, null!), TestContext.CancellationToken), "no text");
        var tooManyBytes = await Assert.ThrowsExactlyAsync<SqliteException>(() => InsertNoteAsync(path, 5401, new string('a', 5401)), "5,401 bytes past the store");
        var tooManyBytesWide = await Assert.ThrowsExactlyAsync<SqliteException>(() => InsertNoteAsync(path, 5402, new string((char)0x20AC, 1801)), "1,801 characters of three bytes past the store");
        var noText = await Assert.ThrowsExactlyAsync<SqliteException>(() => InsertNoteAsync(path, 5403, string.Empty), "no text past the store");

        await InsertNoteAsync(path, 5404, new string('a', 5400));

        var rows = await ScalarAsync(path, "SELECT count(*) FROM incident_notes");

        // Assert
        Assert.AreEqual(NotePrefix + rule, exception.Message, "the message of the rule");
        Assert.IsNotNull(nullEdit, "a null edit is an ArgumentNullException");
        Assert.IsNotNull(nullText, "a null text is an ArgumentNullException");
        Assert.IsNotNull(tooManyBytes, "the table refuses 5,401 bytes");
        Assert.IsNotNull(tooManyBytesWide, "the table counts bytes, not characters");
        Assert.IsNotNull(noText, "the table refuses an empty text");
        Assert.AreEqual("1", rows, "only the row of 5,400 bytes that the table accepts is stored");
    }

    /// <summary>
    /// At most 64 notes of an incident are read, in the order of the numbers they were written for, and a 65th is reported as more.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreIncidentNotesReadsAtMostSixtyFour()
    {
        // Arrange
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, StorageLimits.FileName);

        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await SaveIncidentAsync(store, 0, 2);
        await ExecuteAsync(path, "WITH RECURSIVE numbers(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM numbers WHERE n < 65) INSERT INTO incident_notes (written_for, incident_id, text, updated_at) SELECT n, 1, 'note ' || n, 0 FROM numbers");

        // Act
        var full = await store.IncidentNotesAsync(1, TestContext.CancellationToken);

        await ExecuteAsync(path, "DELETE FROM incident_notes WHERE written_for = 65");

        var exact = await store.IncidentNotesAsync(1, TestContext.CancellationToken);

        // Assert
        Assert.HasCount(StorageLimits.MaxNotesRead, full.Notes, "64 notes are returned");
        Assert.IsTrue(full.More, "the 65th is reported as more");
        Assert.AreEqual(1L, full.Notes[0].WrittenFor, "the first number");
        Assert.AreEqual(64L, full.Notes[^1].WrittenFor, "the last number");
        Assert.AreSequenceEqual(Enumerable.Range(1, 64).Select(number => (long)number).ToArray(), full.Notes.Select(note => note.WrittenFor).ToArray(), "ordered by the number written for");
        Assert.HasCount(StorageLimits.MaxNotesRead, exact.Notes, "64 notes are still 64");
        Assert.IsFalse(exact.More, "exactly 64 notes are no more");
    }

    /// <summary>
    /// The summaries come newest first by window start and then by number, and a page that continues after a position leaves no gap and repeats no incident, also across equal starts.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreIncidentSummariesPageNewestFirst()
    {
        // Arrange
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, StorageLimits.FileName);

        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        foreach (var hour in new[] { 0, 3, 6, 9, 12, -10 })
        {
            await SaveIncidentAsync(store, hour, hour + 2);
        }

        await ExecuteAsync(path, "UPDATE incidents SET started_at = (SELECT started_at FROM incidents WHERE id = 1) WHERE id IN (2, 3)");

        // Act
        var all = await store.IncidentSummariesAsync(new IncidentListQuery
                                                     {
                                                         Limit = 100
                                                     },
                                                     TestContext.CancellationToken);
        var first = await store.IncidentSummariesAsync(new IncidentListQuery
                                                       {
                                                           Limit = 2
                                                       },
                                                       TestContext.CancellationToken);
        var second = await store.IncidentSummariesAsync(new IncidentListQuery
                                                        {
                                                            Limit = 2,
                                                            Before = PositionOf(first[^1])
                                                        },
                                                        TestContext.CancellationToken);
        var third = await store.IncidentSummariesAsync(new IncidentListQuery
                                                       {
                                                           Limit = 2,
                                                           Before = PositionOf(second[^1])
                                                       },
                                                       TestContext.CancellationToken);
        var fourth = await store.IncidentSummariesAsync(new IncidentListQuery
                                                        {
                                                            Limit = 2,
                                                            Before = PositionOf(third[^1])
                                                        },
                                                        TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual(new long[] { 5, 4, 3, 2, 1, 6 }, all.Select(summary => summary.Id).ToArray(), "newest start first, equal starts by descending number");
        Assert.AreSequenceEqual(new long[] { 5, 4 }, first.Select(summary => summary.Id).ToArray(), "the first page");
        Assert.AreSequenceEqual(new long[] { 3, 2 }, second.Select(summary => summary.Id).ToArray(), "the second page continues across equal starts");
        Assert.AreSequenceEqual(new long[] { 1, 6 }, third.Select(summary => summary.Id).ToArray(), "the third page");
        Assert.IsEmpty(fourth, "nothing is left after the last incident");
        Assert.AreEqual(Hour(12), all[0].Start, "start of the newest incident");
        Assert.AreEqual(Hour(14), all[0].End, "end of the newest incident");
        Assert.AreEqual(Hour(12).AddMinutes(30), all[0].FirstKillAt, "first kill of the newest incident");
    }

    /// <summary>
    /// A summary carries the first victim, the last kill, the hypothesis, the times and whether the incident has a note, without the rest of the document.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreIncidentSummariesCarryVictimHypothesisAndNoteFlag()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var saved = Samples.Base.AddDays(1);
        var rich = Sample(0, 3);

        rich.KillCount = 2;
        rich.LastKillAt = Hour(0).AddMinutes(50);
        rich.Kills.Add(Kill(Hour(0).AddMinutes(30), "mariadbd"));
        rich.Kills.Add(Kill(Hour(0).AddMinutes(50), "php-fpm"));
        rich.Hypothesis.BackupRunning = true;
        rich.Hypothesis.GrowthKnown = true;
        rich.Hypothesis.Growth.Add(new ProgramGrowth
                                   {
                                       Program = "mariadbd",
                                       StartRssBytes = 100,
                                       EndRssBytes = 900,
                                       GrowthBytes = 800
                                   });
        rich.Hypothesis.Consumers.Add(new ProgramUsage
                                      {
                                          Program = "mariadbd",
                                          RssBytes = 900,
                                          Count = 1
                                      });
        rich.Hypothesis.ServiceEvents = 3;
        rich.Timeline.Add(new TimelineEntry
                          {
                              At = Hour(0).AddMinutes(10),
                              Type = "boot",
                              Host = "web-1"
                          });
        rich.Metrics.Add(new MetricExtreme
                         {
                             Name = "mem_available_bytes",
                             Extreme = "min",
                             Source = "proc.meminfo",
                             At = Hour(0).AddMinutes(20),
                             Value = 1,
                             Samples = 4
                         });

        var plain = Sample(5, 7);

        await store.SaveIncidentAsync(rich, saved, TestContext.CancellationToken);
        await store.SaveIncidentAsync(plain, saved.AddMinutes(1), TestContext.CancellationToken);
        await store.SaveIncidentNoteAsync(Edit(1, 1, "a note"), TestContext.CancellationToken);

        // Act
        var summaries = await store.IncidentSummariesAsync(new IncidentListQuery
                                                           {
                                                               Limit = 10
                                                           },
                                                           TestContext.CancellationToken);

        // Assert
        var withNote = summaries.Single(summary => summary.Id == 1);
        var withoutNote = summaries.Single(summary => summary.Id == 2);

        Assert.AreEqual("mariadbd", withNote.FirstVictim, "command of the first kill");
        Assert.AreEqual(Hour(0).AddMinutes(30), withNote.FirstKillAt, "time of the first kill");
        Assert.AreEqual(Hour(0).AddMinutes(50), withNote.LastKillAt, "time of the last kill");
        Assert.AreEqual(2L, withNote.KillCount, "number of kills");
        Assert.AreEqual(JsonSerializer.Serialize(rich.Hypothesis, IncidentJson.Options), JsonSerializer.Serialize(withNote.Hypothesis, IncidentJson.Options), "the hypothesis is read as it was saved");
        Assert.IsTrue(withNote.HasNotes, "the incident with a note");
        Assert.AreEqual(Hour(0), withNote.Start, "start");
        Assert.AreEqual(Hour(3), withNote.End, "end");
        Assert.AreEqual(saved, withNote.CreatedAt, "created at");
        Assert.AreEqual(saved, withNote.UpdatedAt, "updated at");
        Assert.AreEqual(string.Empty, withoutNote.FirstVictim, "an incident without a kept kill has no victim");
        Assert.AreEqual(Hour(5).AddMinutes(30), withoutNote.LastKillAt, "the last kill of the incident without kept kills");
        Assert.IsFalse(withoutNote.HasNotes, "the incident without a note");
        Assert.AreEqual(saved.AddMinutes(1), withoutNote.CreatedAt, "created at of the second incident");
    }

    /// <summary>
    /// A list query that breaks a rule, an incident number that is not positive and an absent query are refused with the exact text.
    /// </summary>
    /// <param name="kind">The kind of the invalid call</param>
    /// <param name="rule">The rule that is broken</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("limit-zero", "limit must be between 1 and 100")]
    [DataRow("limit-negative", "limit must be between 1 and 100")]
    [DataRow("limit-101", "limit must be between 1 and 100")]
    [DataRow("before-too-early", "before must be within the storable range")]
    [DataRow("before-too-late", "before must be within the storable range")]
    [DataRow("before-id-zero", "before id must be positive")]
    [DataRow("before-id-negative", "before id must be positive")]
    [DataRow("incident-zero", "incident ID must be positive")]
    [DataRow("incident-negative", "incident ID must be positive")]
    [DataRow("notes-zero", "incident ID must be positive")]
    [DataRow("notes-negative", "incident ID must be positive")]
    public async Task SqliteStoreIncidentSummariesRefuseInvalidQueries(string kind, string rule)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        Func<Task> call = kind switch
                          {
                              "limit-zero" => () => store.IncidentSummariesAsync(new IncidentListQuery
                                                                                 {
                                                                                     Limit = 0
                                                                                 },
                                                                                 TestContext.CancellationToken),
                              "limit-negative" => () => store.IncidentSummariesAsync(new IncidentListQuery
                                                                                     {
                                                                                         Limit = -1
                                                                                     },
                                                                                     TestContext.CancellationToken),
                              "limit-101" => () => store.IncidentSummariesAsync(new IncidentListQuery
                                                                                {
                                                                                    Limit = StorageLimits.MaxIncidentListLimit + 1
                                                                                },
                                                                                TestContext.CancellationToken),
                              "before-too-early" => () => store.IncidentSummariesAsync(Before(StorableTime.Min.AddTicks(-1), 1), TestContext.CancellationToken),
                              "before-too-late" => () => store.IncidentSummariesAsync(Before(StorableTime.Max.AddTicks(1), 1), TestContext.CancellationToken),
                              "before-id-zero" => () => store.IncidentSummariesAsync(Before(Hour(0), 0), TestContext.CancellationToken),
                              "before-id-negative" => () => store.IncidentSummariesAsync(Before(Hour(0), -1), TestContext.CancellationToken),
                              "incident-zero" => () => store.IncidentAsync(0, TestContext.CancellationToken),
                              "incident-negative" => () => store.IncidentAsync(-1, TestContext.CancellationToken),
                              "notes-zero" => () => store.IncidentNotesAsync(0, TestContext.CancellationToken),
                              _ => () => store.IncidentNotesAsync(-1, TestContext.CancellationToken)
                          };

        // Act
        var exception = await Assert.ThrowsExactlyAsync<InvalidQueryException>(call, kind);
        var nullQuery = await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => store.IncidentSummariesAsync(null!, TestContext.CancellationToken), "no query");

        // Assert
        Assert.AreEqual(QueryPrefix + rule, exception.Message, "the message of the rule");
        Assert.IsNotNull(nullQuery, "a null query is an ArgumentNullException");
    }

    /// <summary>
    /// One incident is read by its number, as it was saved, and an unknown number gives <c>null</c>.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreIncidentReadsOneIncidentOrNone()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var saved = Samples.Base.AddDays(1);
        var incident = Sample(0, 3);

        incident.Kills.Add(Kill(Hour(0).AddMinutes(30), "mariadbd"));
        incident.Timeline.Add(new TimelineEntry
                              {
                                  At = Hour(0).AddMinutes(10),
                                  Type = "boot",
                                  Host = "web-1"
                              });

        await store.SaveIncidentAsync(Sample(10, 12), saved, TestContext.CancellationToken);
        await store.SaveIncidentAsync(incident, saved.AddMinutes(1), TestContext.CancellationToken);

        // Act
        var found = await store.IncidentAsync(2, TestContext.CancellationToken);
        var missing = await store.IncidentAsync(3, TestContext.CancellationToken);

        // Assert
        Assert.IsNotNull(found, "the second incident is found");
        Assert.AreEqual(2L, found.Id, "its number");
        Assert.AreEqual(JsonSerializer.Serialize(incident, IncidentJson.Options), JsonSerializer.Serialize(found.Incident, IncidentJson.Options), "the incident is read back as it was saved");
        Assert.AreEqual(saved.AddMinutes(1), found.CreatedAt, "created at");
        Assert.AreEqual(saved.AddMinutes(1), found.UpdatedAt, "updated at");
        Assert.IsNull(missing, "an unknown number gives no incident");
    }

    /// <summary>
    /// Stored incident data that is no incident is refused with a fixed text by the list and by the read of one incident.
    /// </summary>
    /// <param name="data">The stored data</param>
    /// <param name="hasCause">Whether the decoder gave an error</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("{", true, DisplayName = "text that is no JSON")]
    [DataRow("null", false, DisplayName = "the JSON text null")]
    public async Task SqliteStoreIncidentSummariesReportUndecodableData(string data, bool hasCause)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await SaveIncidentAsync(store, 0, 2);
        await ExecuteAsync(Path.Combine(directory.Path, StorageLimits.FileName), $"UPDATE incidents SET data = '{data}'");

        // Act
        var list = await Assert.ThrowsExactlyAsync<StoreException>(() => store.IncidentSummariesAsync(new IncidentListQuery
                                                                                                      {
                                                                                                          Limit = 10
                                                                                                      },
                                                                                                      TestContext.CancellationToken),
                                                                   "listing undecodable data");
        var one = await Assert.ThrowsExactlyAsync<StoreException>(() => store.IncidentAsync(1, TestContext.CancellationToken), "reading undecodable data");

        // Assert
        Assert.AreEqual(DecodingMessage, list.Message, "message of the list");
        Assert.AreEqual(hasCause, list.InnerException is JsonException, "the decoding error is the cause of the list error");
        Assert.AreEqual(DecodingMessage, one.Message, "message of the read of one incident");
        Assert.AreEqual(hasCause, one.InnerException is JsonException, "the decoding error is the cause of the read error");
    }

    /// <summary>
    /// A database error in a note save becomes a store error with the database error as the cause, and an invalid note error can carry a cause.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreSaveIncidentNoteWrapsDatabaseErrors()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await SaveIncidentAsync(store, 0, 2);
        await ExecuteAsync(Path.Combine(directory.Path, StorageLimits.FileName), "DROP TABLE incident_notes");

        var cause = new InvalidOperationException("cause");

        // Act
        var failure = await Assert.ThrowsExactlyAsync<StoreException>(() => store.SaveIncidentNoteAsync(Edit(1, 1, "text"), TestContext.CancellationToken), "save without the table");
        var invalid = new InvalidNoteException("store: invalid note: x", cause);

        // Assert
        Assert.StartsWith("store: saving note: ", failure.Message, "message of the save");
        Assert.IsInstanceOfType<SqliteException>(failure.InnerException, "cause of the save");
        Assert.AreSame(cause, invalid.InnerException, "the cause of an invalid note error");
        Assert.AreEqual("store: invalid note: x", invalid.Message, "its message");
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
    /// Creates a small incident whose window runs between two hours, with its first and last kill 30 minutes after the start.
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
    /// Creates a kill.
    /// </summary>
    /// <param name="at">The time</param>
    /// <param name="command">The command of the victim</param>
    /// <returns>The kill</returns>
    private static IncidentKill Kill(DateTimeOffset at, string command)
    {
        return new IncidentKill
               {
                   At = at,
                   Host = "web-1",
                   VictimPid = 4242,
                   VictimCommand = command,
                   RecordIds = [1],
                   LineCount = 1
               };
    }

    /// <summary>
    /// Creates a note edit.
    /// </summary>
    /// <param name="incidentId">The incident that is edited</param>
    /// <param name="writtenFor">The number the note is written for</param>
    /// <param name="text">The text</param>
    /// <param name="savedAt">The time of the save, or <c>null</c> for a fixed time</param>
    /// <returns>The edit</returns>
    private static IncidentNoteEdit Edit(long incidentId, long writtenFor, string text, DateTimeOffset? savedAt = null)
    {
        return new IncidentNoteEdit
               {
                   IncidentId = incidentId,
                   WrittenFor = writtenFor,
                   Text = text,
                   SavedAt = savedAt ?? Samples.Base.AddDays(2)
               };
    }

    /// <summary>
    /// Creates a list query that continues after a position.
    /// </summary>
    /// <param name="startedAt">The start of the position</param>
    /// <param name="id">The number of the position</param>
    /// <returns>The query</returns>
    private static IncidentListQuery Before(DateTimeOffset startedAt, long id)
    {
        return new IncidentListQuery
               {
                   Limit = 10,
                   Before = new IncidentPosition
                            {
                                StartedAt = startedAt,
                                Id = id
                            }
               };
    }

    /// <summary>
    /// Returns the position of a summary in the list.
    /// </summary>
    /// <param name="summary">The summary</param>
    /// <returns>The position</returns>
    private static IncidentPosition PositionOf(IncidentSummary summary)
    {
        return new IncidentPosition
               {
                   StartedAt = summary.Start,
                   Id = summary.Id
               };
    }

    /// <summary>
    /// Builds the text of a kind: no escape sequence is written in this file, so the characters are built from their numbers.
    /// </summary>
    /// <param name="kind">The kind</param>
    /// <returns>The text</returns>
    private static string TextOf(string kind)
    {
        return kind switch
               {
                   "long" => new string('a', StorageLimits.MaxNoteChars + 1),
                   "nul-leading" => (char)0 + "note",
                   "nul-embedded" => "no" + (char)0 + "te",
                   "lone-high-surrogate" => "note" + (char)0xD800,
                   "lone-low-surrogate" => (char)0xDC00 + "note",
                   _ => "note"
               };
    }

    /// <summary>
    /// Builds the time of a kind.
    /// </summary>
    /// <param name="kind">The kind</param>
    /// <returns>The time</returns>
    private static DateTimeOffset TimeOf(string kind)
    {
        return kind switch
               {
                   "before-minimum" => StorableTime.Min.AddTicks(-1),
                   "after-maximum" => StorableTime.Max.AddTicks(1),
                   _ => Samples.Base.AddDays(2)
               };
    }

    /// <summary>
    /// Saves a small incident.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="startHour">The hour of the start</param>
    /// <param name="endHour">The hour of the end</param>
    /// <returns>A task that returns the result of the save</returns>
    private Task<IncidentSaveResult> SaveIncidentAsync(SqliteStore store, int startHour, int endHour)
    {
        return store.SaveIncidentAsync(Sample(startHour, endHour), Samples.Base.AddDays(1), TestContext.CancellationToken);
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
    /// Reads the first value of a query as text.
    /// </summary>
    /// <param name="path">The database file</param>
    /// <param name="sql">The query</param>
    /// <returns>A task that returns the value</returns>
    private async Task<string> ScalarAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");

        await connection.OpenAsync(TestContext.CancellationToken);

        using var command = connection.CreateCommand();

        command.CommandText = sql;

        return Convert.ToString(await command.ExecuteScalarAsync(TestContext.CancellationToken), System.Globalization.CultureInfo.InvariantCulture)!;
    }

    /// <summary>
    /// Inserts a note of the first incident past the store, with the text bound as a parameter.
    /// </summary>
    /// <param name="path">The database file</param>
    /// <param name="writtenFor">The number the note is written for</param>
    /// <param name="text">The text</param>
    /// <returns>A task that completes when the row is inserted</returns>
    private async Task InsertNoteAsync(string path, long writtenFor, string text)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");

        await connection.OpenAsync(TestContext.CancellationToken);

        using var command = connection.CreateCommand();

        command.CommandText = "INSERT INTO incident_notes (written_for, incident_id, text, updated_at) VALUES ($written, 1, $text, 0)";
        command.Parameters.AddWithValue("$written", writtenFor);
        command.Parameters.AddWithValue("$text", text);
        await command.ExecuteNonQueryAsync(TestContext.CancellationToken);
    }

    #endregion // Methods
}