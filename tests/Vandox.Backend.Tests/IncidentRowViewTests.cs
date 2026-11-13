using Vandox.Backend.Incidents;
using Vandox.Backend.Logs;
using Vandox.Core.Incidents;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="IncidentRowView"/>
/// </summary>
[TestClass]
public class IncidentRowViewTests
{
    #region Fields

    private static readonly DateTimeOffset _firstKill = new(2026, 10, 12, 4, 30, 0, TimeSpan.Zero);

    #endregion // Fields

    #region Methods

    /// <summary>
    /// The row shows the link, the duration from the first to the last kill, the victim as escaped text, the further kills, the growth, the backup state and the note flag.
    /// </summary>
    [TestMethod]
    public void IncidentRowViewFormatsDurationVictimAndHypothesis()
    {
        // Arrange
        var bidi = char.ConvertFromUtf32(0x202e);
        var victim = $"ev{bidi}il";
        var rich = Summary(7, victim, 3, TimeSpan.FromSeconds((62 * 60) + 3));

        rich.Hypothesis.GrowthKnown = true;
        rich.Hypothesis.BackupRunning = true;
        rich.Hypothesis.Growth.Add(new ProgramGrowth
                                   {
                                       Program = "mariadbd",
                                       StartRssBytes = 1,
                                       EndRssBytes = 805306369,
                                       GrowthBytes = 805306368
                                   });
        rich.Hypothesis.Growth.Add(new ProgramGrowth
                                   {
                                       Program = "pzstd",
                                       StartRssBytes = 1,
                                       EndRssBytes = 1572865,
                                       GrowthBytes = 1572864
                                   });
        rich.HasNotes = true;

        var none = Summary(8, "mariadbd", 1, TimeSpan.Zero);

        none.Hypothesis.GrowthKnown = true;
        none.Hypothesis.BackupRunning = false;

        var unknown = Summary(9, "mariadbd", 1, TimeSpan.FromHours(100));

        unknown.Hypothesis.GrowthKnown = false;
        unknown.Hypothesis.BackupRunning = null;

        var negative = Summary(10, "mariadbd", 2, TimeSpan.FromSeconds(-5));

        // Act
        var richRow = IncidentRowView.From(rich);
        var noneRow = IncidentRowView.From(none);
        var unknownRow = IncidentRowView.From(unknown);
        var negativeRow = IncidentRowView.From(negative);

        // Assert
        Assert.Contains(bidi, victim, "the input holds U+202E");
        Assert.AreEqual(7L, richRow.Id, "the number");
        Assert.AreEqual("incidents/7", richRow.Href, "the link to the incident page");
        Assert.AreEqual(_firstKill, richRow.FirstKillAt, "the time of the first kill");
        Assert.AreEqual("1:02:03", richRow.Duration, "the duration from the first to the last kill");
        Assert.AreEqual(3L, richRow.KillCount, "the number of kills");
        Assert.IsTrue(richRow.MoreKills, "further kills");
        Assert.AreEqual(string.Concat(LogText.Runs(victim, LogText.MaxShortChars).Select(run => run.Text)), string.Concat(richRow.Victim.Select(run => run.Text)), "the victim is the escaped text of the command");
        Assert.Contains(run => run.Escaped && run.Text.Contains(string.Concat((char)92, "u202e"), StringComparison.Ordinal), richRow.Victim, "the direction character is an escaped run");
        Assert.HasCount(2, richRow.Growth, "two programs grew");
        Assert.AreEqual("mariadbd", string.Concat(richRow.Growth[0].Program.Select(run => run.Text)), "the program that grew most");
        Assert.AreEqual("+768 MiB", richRow.Growth[0].Amount, "its growth");
        Assert.AreEqual("+2 MiB", richRow.Growth[1].Amount, "1.5 MiB is rounded half away from zero");
        Assert.AreEqual("running", richRow.Backup, "a running backup");
        Assert.IsTrue(richRow.HasNotes, "the note flag");
        Assert.AreEqual("0:00:00", noneRow.Duration, "a single kill lasts 0:00:00");
        Assert.IsFalse(noneRow.MoreKills, "no further kills");
        Assert.IsEmpty(noneRow.Growth, "no growth");
        Assert.AreEqual("none", noneRow.GrowthState, "growth known and empty");
        Assert.AreEqual("not running", noneRow.Backup, "no backup");
        Assert.IsFalse(noneRow.HasNotes, "no note");
        Assert.AreEqual("100:00:00", unknownRow.Duration, "hours are not bounded");
        Assert.AreEqual("unknown", unknownRow.GrowthState, "growth unknown");
        Assert.AreEqual("unknown", unknownRow.Backup, "backup unknown");
        Assert.AreEqual("0:00:00", negativeRow.Duration, "a negative duration is 0:00:00");
    }

    /// <summary>
    /// The victim and the programs that grew of a row are built with at most 16 runs each, and the characters shown are those of the escaped text.
    /// </summary>
    [TestMethod]
    [TestCategory("AC1")]
    public void IncidentRowViewShowsVictimAndProgramsInAtMostSixteenRuns()
    {
        // Arrange
        var text = Alternating(1024);
        var summary = Summary(1, text, 1, TimeSpan.Zero);

        summary.Hypothesis.GrowthKnown = true;
        summary.Hypothesis.Growth.Add(new ProgramGrowth
                                      {
                                          Program = text,
                                          StartRssBytes = 1,
                                          EndRssBytes = 805306369,
                                          GrowthBytes = 805306368
                                      });

        var counts = new List<int>();

        // Act
        var row = IncidentRowView.From(summary);

        // Assert
        Assert.AreEqual(1, text[1], "the input holds U+0001");
        Assert.HasCount(1, row.Growth, "the program that grew is shown");
        CheckField("the victim", row.Victim, text, LogText.MaxShortChars, counts);
        CheckField("the program", row.Growth[0].Program, text, LogText.MaxShortChars, counts);
        Assert.AreEqual(IncidentsView.MaxTextRuns, counts.Max(), "a text that alternates fills all 16 runs");
    }

    /// <summary>
    /// Creates a summary.
    /// </summary>
    /// <param name="id">The number</param>
    /// <param name="victim">The command of the first victim</param>
    /// <param name="kills">The number of kills</param>
    /// <param name="duration">The time from the first to the last kill</param>
    /// <returns>The summary</returns>
    private static IncidentSummary Summary(long id, string victim, long kills, TimeSpan duration)
    {
        return new IncidentSummary
               {
                   Id = id,
                   Start = _firstKill.AddHours(-1),
                   End = _firstKill.AddHours(1),
                   FirstKillAt = _firstKill,
                   LastKillAt = _firstKill + duration,
                   KillCount = kills,
                   FirstVictim = victim,
                   Hypothesis = new IncidentHypothesis(),
                   CreatedAt = _firstKill.AddDays(1),
                   UpdatedAt = _firstKill.AddDays(1)
               };
    }

    /// <summary>
    /// Builds a text whose characters alternate between two characters.
    /// </summary>
    /// <param name="length">The length</param>
    /// <returns>The text</returns>
    private static string Alternating(int length)
    {
        var characters = new char[length];

        for (var position = 0; position < length; position++)
        {
            characters[position] = position % 2 == 0 ? 'a' : (char)1;
        }

        return new string(characters);
    }

    /// <summary>
    /// Checks that a built field has at most 16 runs and shows the same characters as the escaped text, and records its run count.
    /// </summary>
    /// <param name="name">The name of the field</param>
    /// <param name="runs">The runs of the field</param>
    /// <param name="text">The text the field was built from</param>
    /// <param name="cap">The character cap of the field</param>
    /// <param name="counts">The collected run counts</param>
    private static void CheckField(string name, IReadOnlyList<TextRun> runs, string text, int cap, List<int> counts)
    {
        Assert.IsLessThanOrEqualTo(IncidentsView.MaxTextRuns, runs.Count, $"{name} has at most 16 runs");
        Assert.AreEqual(LogText.Escape(text, cap), string.Concat(runs.Select(run => run.Text)), $"{name} shows the escaped text unchanged");
        counts.Add(runs.Count);
    }

    #endregion // Methods
}