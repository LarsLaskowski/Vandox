using Vandox.Backend.Cli;
using Vandox.Backend.Incidents;
using Vandox.Core.Incidents;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="IncidentSummaryWriter"/>
/// </summary>
[TestClass]
public class IncidentSummaryWriterTests
{
    #region Methods

    /// <summary>
    /// An incident line names the ID, the outcome, the first kill in UTC, the kill count and the quoted victim; merged appears only above zero.
    /// </summary>
    [TestMethod]
    public void IncidentSummaryWriterFormatsIncidentLines()
    {
        // Arrange
        var firstKill = new DateTimeOffset(2026, 10, 2, 6, 59, 0, TimeSpan.FromHours(2));
        var hostile = string.Concat("a\nb", char.ConvertFromUtf32(0x1B), "[31m", char.ConvertFromUtf32(0x202E), "c");
        var escapedControl = string.Concat((char)92, "u001b");
        var escapedFormat = string.Concat((char)92, "u202e");

        Assert.Contains(char.ConvertFromUtf32(0x1B), hostile, "the victim holds the escape character");
        Assert.Contains(char.ConvertFromUtf32(0x202E), hostile, "the victim holds the right-to-left override");

        // Act
        var created = IncidentSummaryWriter.FormatIncident(Saved(3, IncidentSaveOutcome.Created, 0, firstKill, 1, "mariadbd"));
        var updated = IncidentSummaryWriter.FormatIncident(Saved(4, IncidentSaveOutcome.Updated, 2, firstKill, 3, "php-fpm"));
        var unchanged = IncidentSummaryWriter.FormatIncident(Saved(5, IncidentSaveOutcome.Unchanged, 0, firstKill, 2, "mariadbd"));
        var quoted = IncidentSummaryWriter.FormatIncident(Saved(6, IncidentSaveOutcome.Created, 0, firstKill, 1, hostile));
        var withoutVictim = IncidentSummaryWriter.FormatIncident(Saved(7, IncidentSaveOutcome.Created, 0, firstKill, 1, null));

        // Assert
        Assert.AreEqual("#3 created 2026-10-02T04:59:00Z kills=1 victim=\"mariadbd\"", created, "created incident");
        Assert.AreEqual("#4 updated 2026-10-02T04:59:00Z kills=3 victim=\"php-fpm\" merged=2", updated, "updated incident with a merge");
        Assert.AreEqual("#5 unchanged 2026-10-02T04:59:00Z kills=2 victim=\"mariadbd\"", unchanged, "unchanged incident");
        Assert.AreEqual($"#6 created 2026-10-02T04:59:00Z kills=1 victim={Terminal.Quote(hostile)}", quoted, "the victim is quoted like every terminal text");
        Assert.DoesNotContain(char.ConvertFromUtf32(0x1B), quoted, "no escape character reaches the terminal");
        Assert.DoesNotContain(char.ConvertFromUtf32(0x202E), quoted, "no right-to-left override reaches the terminal");
        Assert.DoesNotContain("\n", quoted, "no line feed inside the line");
        Assert.Contains(escapedControl, quoted, "the escape character is written as an escape");
        Assert.Contains(escapedFormat, quoted, "the override is written as an escape");
        Assert.AreEqual("#7 created 2026-10-02T04:59:00Z kills=1 victim=-", withoutVictim, "no kill victim");
    }

    /// <summary>
    /// The summary lists the counts in a fixed block with the labels padded to 21 characters.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IncidentSummaryWriterWritesCounts()
    {
        // Arrange
        using var writer = new StringWriter();
        var result = new ReconstructionResult
                     {
                         Incidents = 8,
                         Created = 1,
                         Updated = 2,
                         Unchanged = 3,
                         Merged = 4,
                         Kept = 5,
                         Skipped = 6,
                         Kills = 7,
                         LinesRead = 9
                     };

        // Act
        await IncidentSummaryWriter.WriteAsync(writer, result);

        // Assert
        var expected = string.Concat(Row("Incidents:", 8), Row("  created:", 1), Row("  updated:", 2), Row("  unchanged:", 3), Row("  merged:", 4), Row("  kept:", 5), Row("  skipped:", 6), Row("OOM kills:", 7), Row("Log lines read:", 9));

        Assert.AreEqual(expected, writer.ToString().Replace("\r\n", "\n", StringComparison.Ordinal), "summary block");
    }

    /// <summary>
    /// Creates a line of the summary: the label padded to 21 characters, the value and a line feed.
    /// </summary>
    /// <param name="label">The label</param>
    /// <param name="value">The value</param>
    /// <returns>The line</returns>
    private static string Row(string label, int value)
    {
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{label.PadRight(21)}{value}\n");
    }

    /// <summary>
    /// Creates a saved incident.
    /// </summary>
    /// <param name="id">The incident number</param>
    /// <param name="outcome">The outcome</param>
    /// <param name="merged">The number of incidents merged away</param>
    /// <param name="firstKill">The time of the first kill</param>
    /// <param name="kills">The number of kills</param>
    /// <param name="victim">The command of the first kill's victim, or <c>null</c> for an incident without kill</param>
    /// <returns>The saved incident</returns>
    private static SavedIncident Saved(long id, IncidentSaveOutcome outcome, long merged, DateTimeOffset firstKill, long kills, string? victim)
    {
        var incident = new Incident
                       {
                           FirstKillAt = firstKill,
                           KillCount = kills
                       };

        if (victim is not null)
        {
            incident.Kills.Add(new IncidentKill
                               {
                                   At = firstKill,
                                   VictimCommand = victim
                               });
        }

        return new SavedIncident
               {
                   Result = new IncidentSaveResult
                            {
                                Id = id,
                                Outcome = outcome,
                                Merged = merged
                            },
                   Incident = incident
               };
    }

    #endregion // Methods
}