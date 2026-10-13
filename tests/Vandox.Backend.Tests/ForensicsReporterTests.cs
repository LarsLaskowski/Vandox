using Microsoft.Extensions.Time.Testing;

using Vandox.Backend.Reports;
using Vandox.Core.Incidents;
using Vandox.Core.Signatures;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="ForensicsReporter"/>
/// </summary>
[TestClass]
public class ForensicsReporterTests
{
    #region Constants

    private const ulong Mib = 1048576;

    private static readonly DateTimeOffset _now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Every stored incident gets a section, in the order of its window, with its victims and hypothesis.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ForensicsReporterListsEveryOutageWithVictimsAndHypothesis()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var first = Outage(IncidentSamples.Base, "mariadbd");

        first.Hypothesis = new IncidentHypothesis
                           {
                               SnapshotCount = 2,
                               GrowthKnown = true,
                               Growth = [
                                            new ProgramGrowth
                                            {
                                                Program = "mariadbd",
                                                StartRssBytes = 10 * Mib,
                                                EndRssBytes = 13 * Mib,
                                                GrowthBytes = 3 * Mib
                                            }
                                        ]
                           };

        await SaveAsync(store, first, Outage(IncidentSamples.Base.AddDays(1), "nginx"), Outage(IncidentSamples.Base.AddDays(2), "php-fpm"));

        // Act
        var (output, result) = await RunAsync(store, store);

        var one = output.IndexOf("### Outage #1 at 2026-10-02 04:59 UTC", StringComparison.Ordinal);
        var two = output.IndexOf("### Outage #2 at 2026-10-03 04:59 UTC", StringComparison.Ordinal);
        var three = output.IndexOf("### Outage #3 at 2026-10-04 04:59 UTC", StringComparison.Ordinal);

        // Assert
        Assert.AreEqual(3, result.IncidentsListed, "incidents listed");
        Assert.IsGreaterThan(-1, one, "section of the first outage");
        Assert.IsGreaterThan(one, two, "the second outage follows the first");
        Assert.IsGreaterThan(two, three, "the third outage follows the second");
        Assert.Contains("` mariadbd `", output[one..two], "victim of the first outage");
        Assert.Contains("+3", output[one..two], "growth of the first outage");
        Assert.Contains("` nginx `", output[two..three], "victim of the second outage");
        Assert.Contains("` php-fpm `", output[three..], "victim of the third outage");
        Assert.DoesNotContain("` nginx `", output[one..two], "victims stay in their section");
    }

    /// <summary>
    /// The incidents are asked for with limit 1, each page starting at the end of the previous incident, in two passes.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ForensicsReporterReadsIncidentsOneAtATime()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var incidents = new[] { Outage(IncidentSamples.Base, "mariadbd"), Outage(IncidentSamples.Base.AddDays(1), "nginx"), Outage(IncidentSamples.Base.AddDays(2), "php-fpm") };
        var recording = new RecordingIncidentStore(store);

        await SaveAsync(store, incidents);

        // Act
        var (_, result) = await RunAsync(store, recording);

        var queries = recording.Queries;

        // Assert
        Assert.AreEqual(3, result.IncidentsListed, "incidents listed");
        Assert.HasCount(8, queries, "three incidents and the empty page, twice");
        Assert.IsTrue(queries.All(query => query.Limit == 1), "every query asks for one incident");
        Assert.AreEqual(incidents[0].End, queries[1].From, "the second page starts at the end of the first incident");
        Assert.AreEqual(incidents[1].End, queries[2].From, "the third page starts at the end of the second incident");
        Assert.AreEqual(incidents[2].End, queries[3].From, "the last page starts at the end of the third incident");
        Assert.AreEqual(queries[0].From, queries[4].From, "the second pass starts where the first did");
        Assert.AreEqual(incidents[0].End, queries[5].From, "the second pass pages the same way");
        Assert.AreEqual(incidents[2].End, queries[7].From, "the second pass ends the same way");
        Assert.IsTrue(queries.All(query => query.To == queries[0].To), "the same end of the range");
    }

    /// <summary>
    /// A page that does not lead past the previous incident is an error with the store's text.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ForensicsReporterFailsWhenIncidentPageDoesNotAdvance()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var stuck = new ScriptedIncidentStore([[Stored(1, Outage(IncidentSamples.Base, "mariadbd"))]], true);

        // Act
        var exception = await Assert.ThrowsExactlyAsync<StoreException>(() => RunAsync(store, stuck), "the page repeats");

        // Assert
        Assert.AreEqual("store: reading incidents: page did not advance", exception.Message, "message");
        Assert.IsLessThan(5, stuck.Calls, "the run stops after the page repeated");
    }

    /// <summary>
    /// A different number of incidents in the second pass is noted, and an unchanged number is not.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ForensicsReporterNotesIncidentsChangedBetweenPasses()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var changing = new ScriptedIncidentStore([[Stored(1, Outage(IncidentSamples.Base, "mariadbd"))], [], []], false);

        await SaveAsync(store, Outage(IncidentSamples.Base, "mariadbd"));

        // Act
        var (changedOutput, changedResult) = await RunAsync(store, changing);
        var (stableOutput, stableResult) = await RunAsync(store, store);

        // Assert
        Assert.AreEqual(0, changedResult.IncidentsListed, "the second pass listed none");
        Assert.Contains(ReportText.English.IncidentsChangedNote, changedOutput, "the notes ask for a new run");
        Assert.AreEqual(1, stableResult.IncidentsListed, "the second pass listed the one incident");
        Assert.DoesNotContain(ReportText.English.IncidentsChangedNote, stableOutput, "an unchanged count is not noted");
    }

    /// <summary>
    /// The head gives the generation time from the clock, the log lines, the observed hours, the snapshots and the lookback.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ForensicsReporterWritesTheHead()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var day = new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);

        await IncidentSamples.WriteAsync(store,
                                         TestContext.CancellationToken,
                                         IncidentSamples.Line(day.AddHours(4).AddMinutes(10), "cron", "job one"),
                                         IncidentSamples.Line(day.AddHours(4).AddMinutes(50), "cron", "job two"),
                                         IncidentSamples.Line(day.AddHours(7).AddMinutes(20), "cron", "job three"),
                                         IncidentSamples.Processes(day.AddHours(4).AddMinutes(30), ("mariadbd", 100 * Mib)),
                                         IncidentSamples.Processes(day.AddHours(6).AddMinutes(45), ("mariadbd", 120 * Mib)));

        // Act
        var (output, result) = await RunAsync(store, store, TimeSpan.FromMinutes(75));

        var head = output[..output.IndexOf("## ", StringComparison.Ordinal)];
        var lines = head.Split('\n');

        // Assert
        Assert.AreEqual(3, result.LinesRead, "lines read");
        Assert.AreEqual(2, result.SnapshotsRead, "snapshots read");
        Assert.AreEqual(0, result.IncidentsListed, "incidents listed");
        Assert.StartsWith("# Vandox forensics report\n", output, "the title comes first");
        Assert.Contains("2026-10-03 12:00 UTC", head, "generation time from the clock");
        AssertLine(lines, ReportText.English.LogLines, "3", "2026-10-02 04:10 UTC", "2026-10-02 07:20 UTC");
        AssertLine(lines, ReportText.English.ObservedHours, "2");
        AssertLine(lines, ReportText.English.Snapshots, "2", "2026-10-02 04:30 UTC", "2026-10-02 06:45 UTC");
        AssertLine(lines, ReportText.English.Lookback, "75");
    }

    /// <summary>
    /// With an empty store the report completes with no outage, unknown rules and unknown consumers.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ForensicsReporterCompletesOnAnEmptyStore()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        // Act
        var (output, result) = await RunAsync(store, store);

        // Assert
        Assert.AreEqual(0, result.LinesRead, "lines read");
        Assert.AreEqual(0, result.SnapshotsRead, "snapshots read");
        Assert.AreEqual(0, result.IncidentsListed, "incidents listed");
        Assert.StartsWith("# Vandox forensics report\n", output, "the title");
        Assert.Contains("\n## Outages\n", output, "the outages heading");
        Assert.Contains("\n## Notes\n", output, "the notes heading");
        Assert.Contains(ReportText.English.NoOutages, output, "no outage is said");
        Assert.Contains(ReportText.English.NoSnapshots, output, "unknown consumers");
        Assert.Contains(ReportText.English.ReasonTooFewIncidents, output, "the rules are unknown for too few incidents");
        Assert.DoesNotContain("### Outage #", output, "no outage section");
        Assert.DoesNotContain("\r", output, "lines end with a line feed");
    }

    /// <summary>
    /// Asserts that a line of the head with the label holds all the given texts.
    /// </summary>
    /// <param name="lines">The lines of the head</param>
    /// <param name="label">The label of the line</param>
    /// <param name="texts">The texts the line holds; a number only as a whole number</param>
    private static void AssertLine(string[] lines, string label, params string[] texts)
    {
        var matching = lines.Where(line => line.Contains(label, StringComparison.Ordinal)).ToList();

        Assert.IsNotEmpty(matching, $"a line with {label}");

        foreach (var text in texts)
        {
            var found = matching.Any(line => text.Length > 3 ? line.Contains(text, StringComparison.Ordinal) : TextChecks.ContainsNumber(line, text));

            Assert.IsTrue(found, $"the line with {label} holds {text}");
        }
    }

    /// <summary>
    /// Creates an incident with one kill whose window ends 30 minutes after it.
    /// </summary>
    /// <param name="firstKillAt">The time of the kill</param>
    /// <param name="victim">The victim's command</param>
    /// <returns>The incident</returns>
    private static Incident Outage(DateTimeOffset firstKillAt, string victim)
    {
        return new Incident
               {
                   Start = firstKillAt.AddHours(-1),
                   End = firstKillAt.AddMinutes(30),
                   FirstKillAt = firstKillAt,
                   LastKillAt = firstKillAt,
                   KillCount = 1,
                   Kills = [
                               new IncidentKill
                               {
                                   At = firstKillAt,
                                   Host = "web-1",
                                   VictimCommand = victim,
                                   LineCount = 1
                               }
                           ]
               };
    }

    /// <summary>
    /// Wraps an incident in a stored incident.
    /// </summary>
    /// <param name="id">The ID</param>
    /// <param name="incident">The incident</param>
    /// <returns>The stored incident</returns>
    private static StoredIncident Stored(long id, Incident incident)
    {
        return new StoredIncident
               {
                   Id = id,
                   Incident = incident,
                   CreatedAt = _now,
                   UpdatedAt = _now
               };
    }

    /// <summary>
    /// Saves incidents in the given order.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="incidents">The incidents</param>
    /// <returns>A task that completes when the incidents are saved</returns>
    private async Task SaveAsync(SqliteStore store, params Incident[] incidents)
    {
        foreach (var incident in incidents)
        {
            var saved = await store.SaveIncidentAsync(incident, _now, TestContext.CancellationToken);

            Assert.AreEqual(IncidentSaveOutcome.Created, saved.Outcome, "the incident is created");
        }
    }

    /// <summary>
    /// Runs the reporter with the English table and a fixed clock.
    /// </summary>
    /// <param name="records">The record reader</param>
    /// <param name="incidents">The incident store</param>
    /// <param name="lookback">The lookback; one hour when <c>null</c></param>
    /// <returns>A task that returns the report with line feeds and the result</returns>
    private async Task<(string Output, ReportResult Result)> RunAsync(IRecordReader records, IIncidentStore incidents, TimeSpan? lookback = null)
    {
        var output = new StringWriter();
        var reporter = new ForensicsReporter(records, incidents, BuiltInSignatures.Catalog, lookback ?? TimeSpan.FromHours(1), new FakeTimeProvider(_now));
        var result = await reporter.RunAsync(new MarkdownReportWriter(output, ReportText.English), TestContext.CancellationToken);

        return (output.ToString().Replace("\r\n", "\n", StringComparison.Ordinal), result);
    }

    #endregion // Methods
}