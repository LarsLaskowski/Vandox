using Vandox.Backend.Components.Charts;
using Vandox.Backend.Incidents;
using Vandox.Backend.Logs;
using Vandox.Core.Incidents;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="ProgramMemorySeries"/>
/// </summary>
[TestClass]
public class ProgramMemorySeriesTests
{
    #region Constants

    private const ulong Mib = 1048576UL;

    #endregion // Constants

    #region Fields

    private static readonly DateTimeOffset _start = new(2026, 10, 12, 4, 0, 0, TimeSpan.Zero);

    #endregion // Fields

    #region Methods

    /// <summary>
    /// The chart is stacked and has one point per snapshot, the five programs with the highest peak by descending peak and the rest as Other programs, in MiB.
    /// </summary>
    [TestMethod]
    public void ProgramMemorySeriesStacksTopFiveProgramsAndOthersInMib()
    {
        // Arrange
        var series = new ProgramMemorySeries();
        var empty = new ProgramMemorySeries();

        // Act
        var first = series.Add(_start, Programs(("a", 10), ("b", 20), ("c", 30), ("d", 40), ("e", 50), ("f", 60), ("g", 70)));
        var second = series.Add(_start.AddMinutes(1), Programs(("a", 5), ("b", 25), ("c", 30), ("d", 1), ("e", 1), ("f", 1), ("g", 100)));
        var chart = series.ToChart();

        // Assert
        Assert.IsNull(empty.ToChart(), "no snapshot, no chart");
        Assert.IsTrue(first, "the first snapshot is used");
        Assert.IsTrue(second, "the second snapshot is used");
        Assert.AreEqual(2, series.Count, "two snapshots");
        Assert.IsNotNull(chart, "a chart for two snapshots");
        Assert.IsTrue(chart.Stacked, "the chart is stacked");
        Assert.AreSequenceEqual(new[] { _start, _start.AddMinutes(1) }, chart.Times.ToArray(), "one point per snapshot");
        Assert.AreSequenceEqual(new[] { "g", "f", "e", "d", "c", IncidentsView.OtherPrograms }, chart.Series.Select(item => item.Label).ToArray(), "the five highest peaks, then the others");
        Assert.AreSequenceEqual(new double?[] { 70, 100 }, chart.Series[0].Values.ToArray(), "g in MiB");
        Assert.AreSequenceEqual(new double?[] { 60, 1 }, chart.Series[1].Values.ToArray(), "f in MiB");
        Assert.AreSequenceEqual(new double?[] { 50, 1 }, chart.Series[2].Values.ToArray(), "e in MiB");
        Assert.AreSequenceEqual(new double?[] { 40, 1 }, chart.Series[3].Values.ToArray(), "d in MiB");
        Assert.AreSequenceEqual(new double?[] { 30, 30 }, chart.Series[4].Values.ToArray(), "c in MiB");
        Assert.AreSequenceEqual(new double?[] { 30, 30 }, chart.Series[5].Values.ToArray(), "a and b together in MiB");
    }

    /// <summary>
    /// Programs with the same peak rank by ordinal name, and a program absent from a snapshot has 0 in it.
    /// </summary>
    [TestMethod]
    public void ProgramMemorySeriesRanksByPeakAndFillsMissingProgramsWithZero()
    {
        // Arrange
        var series = new ProgramMemorySeries();

        // Act
        series.Add(_start, Programs(("p6", 7), ("p5", 7), ("p4", 7), ("p3", 7), ("p2", 7), ("p1", 7)));
        series.Add(_start.AddMinutes(1), Programs(("p7", 7)));

        var chart = series.ToChart();

        // Assert
        Assert.IsNotNull(chart, "a chart");
        Assert.AreSequenceEqual(new[] { "p1", "p2", "p3", "p4", "p5", IncidentsView.OtherPrograms }, chart.Series.Select(item => item.Label).ToArray(), "seven equal peaks: the first five by ordinal name, whatever the order they came in");
        Assert.AreSequenceEqual(new double?[] { 7, 0 }, chart.Series[0].Values.ToArray(), "p1 is absent from the second snapshot");
        Assert.AreSequenceEqual(new double?[] { 7, 0 }, chart.Series[4].Values.ToArray(), "p5 is absent from the second snapshot");
        Assert.AreSequenceEqual(new double?[] { 7, 7 }, chart.Series[5].Values.ToArray(), "p6 in the first snapshot, p7 in the second one");
    }

    /// <summary>
    /// A label is the program name escaped and capped at 64 characters, with invisible characters made visible and markup left as plain text for the script.
    /// </summary>
    [TestMethod]
    public void ProgramMemorySeriesEscapesAndCapsProgramLabels()
    {
        // Arrange
        var bidi = char.ConvertFromUtf32(0x202e);
        var hidden = $"ev{bidi}il";
        var long200 = new string('q', 200);
        var markup = "<b>x</b>";
        var series = new ProgramMemorySeries();

        // Act
        series.Add(_start, Programs((hidden, 100), (long200, 90), (markup, 80), ("plain1", 70), ("plain2", 60), ("plain3", 5), ("plain4", 4)));

        var chart = series.ToChart();

        // Assert
        Assert.IsNotNull(chart, "a chart");

        var labels = chart.Series.Select(item => item.Label).ToArray();

        Assert.Contains(bidi, hidden, "the input holds U+202E");
        Assert.AreEqual(LogText.Escape(hidden, IncidentsView.MaxLabelChars), labels[0], "the label of a name with a direction character");
        Assert.DoesNotContain(bidi, labels[0], "the direction character itself is not in the label");
        Assert.Contains(string.Concat((char)92, "u202e"), labels[0], "the label shows the escape");
        Assert.AreEqual(LogText.Escape(long200, IncidentsView.MaxLabelChars), labels[1], "the label of a long name");
        Assert.AreEqual(64, labels[1].Count(character => character == 'q'), "the label holds 64 characters of the name");
        Assert.Contains("[cut:", labels[1], "the label says that the name was cut");
        Assert.AreEqual(markup, labels[2], "markup is plain text in a label; the script writes it as text");
    }

    /// <summary>
    /// A snapshot that is not later than the one before is skipped, and only the snapshots that are used are counted and charted.
    /// </summary>
    [TestMethod]
    public void ProgramMemorySeriesSkipsSnapshotsThatDoNotAdvance()
    {
        // Arrange
        var series = new ProgramMemorySeries();

        // Act
        var first = series.Add(_start.AddMinutes(1), Programs(("a", 1)));
        var equal = series.Add(_start.AddMinutes(1), Programs(("a", 2)));
        var earlier = series.Add(_start, Programs(("a", 3)));
        var later = series.Add(_start.AddMinutes(2), Programs(("a", 4)));
        var chart = series.ToChart();

        // Assert
        Assert.IsTrue(first, "the first snapshot is used");
        Assert.IsFalse(equal, "a snapshot at the time of the last one is skipped");
        Assert.IsFalse(earlier, "a snapshot before the last one is skipped");
        Assert.IsTrue(later, "a later snapshot is used");
        Assert.AreEqual(2, series.Count, "two snapshots are used");
        Assert.IsNotNull(chart, "a chart");
        Assert.AreSequenceEqual(new[] { _start.AddMinutes(1), _start.AddMinutes(2) }, chart.Times.ToArray(), "the times ascend strictly");
        Assert.AreSequenceEqual(new double?[] { 1, 4 }, chart.Series[0].Values.ToArray(), "the skipped snapshots add no value");
    }

    /// <summary>
    /// Programs beyond the 4,096 that are charted individually, however large they are, go to Other programs.
    /// </summary>
    [TestMethod]
    public void ProgramMemorySeriesCountsProgramsBeyondTheCapAsOther()
    {
        // Arrange
        var series = new ProgramMemorySeries();
        var programs = new Dictionary<string, ProgramUsage>(StringComparer.Ordinal);
        var total = IncidentsView.MaxChartPrograms + 4;

        for (var index = 0; index < total; index++)
        {
            var mib = index switch
                      {
                          < 5 => (ulong)(index + 1) * 100,
                          _ when index == total - 1 => 9999UL,
                          _ => 1UL
                      };

            programs[$"p{index:D4}"] = new ProgramUsage
                                       {
                                           Program = $"p{index:D4}",
                                           RssBytes = mib * Mib,
                                           Count = 1
                                       };
        }

        // Act
        series.Add(_start, programs);

        var chart = series.ToChart();

        // Assert
        Assert.IsNotNull(chart, "a chart");
        Assert.AreSequenceEqual(new[] { "p0004", "p0003", "p0002", "p0001", "p0000", IncidentsView.OtherPrograms }, chart.Series.Select(item => item.Label).ToArray(), "the largest program beyond the cap is not charted on its own");
        Assert.AreSequenceEqual(new double?[] { 500 }, chart.Series[0].Values.ToArray(), "the largest charted program");
        Assert.AreSequenceEqual(new double?[] { 14093 }, chart.Series[5].Values.ToArray(), "4,094 programs of 1 MiB and the one of 9,999 MiB beyond the cap");
    }

    /// <summary>
    /// Creates the programs of a snapshot in the given order.
    /// </summary>
    /// <param name="programs">The names and the resident memory in MiB</param>
    /// <returns>The programs by name</returns>
    private static Dictionary<string, ProgramUsage> Programs(params (string Program, ulong Mib)[] programs)
    {
        return programs.ToDictionary(entry => entry.Program,
                                     entry => new ProgramUsage
                                              {
                                                  Program = entry.Program,
                                                  RssBytes = entry.Mib * Mib,
                                                  Count = 1
                                              },
                                     StringComparer.Ordinal);
    }

    #endregion // Methods
}