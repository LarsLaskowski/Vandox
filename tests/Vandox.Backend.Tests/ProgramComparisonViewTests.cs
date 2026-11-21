using Vandox.Backend.History;
using Vandox.Backend.Processes;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="ProgramComparisonView"/>. The table has the columns Program, the memory at the earlier point, the memory at the later point, Change, the CPU % at the earlier point, the CPU % at the later point and State;
/// the heading of the two memory columns names the memory key in parentheses, <c>(PSS)</c> or <c>(RSS)</c>; a side that does not name the program shows <c>--</c>.
/// </summary>
[TestClass]
public class ProgramComparisonViewTests
{
    #region Constants

    private const string IncompleteNote = "One of the two snapshots does not list every program, so no program is marked new or gone.";
    private const double Mebibyte = ProcessesSamples.Mebibyte;
    private const char RightToLeftOverride = (char)0x202E;
    private const char Bell = (char)7;

    #endregion // Constants

    #region Fields

    private static readonly DateTimeOffset _earlier = HistorySamples.Base.AddMinutes(10);

    private static readonly DateTimeOffset _later = HistorySamples.Base.AddMinutes(40);

    #endregion // Fields

    #region Methods

    /// <summary>
    /// The rows are ordered by the memory change from the largest increase down, then by program name, with the figures of both points, the change signed in binary units and the CPU percent with one decimal; the heading names both points and the source.
    /// </summary>
    [TestMethod]
    [TestCategory("AC6")]
    public void ProgramComparisonViewOrdersByMemoryChangeThenName()
    {
        // Arrange
        var earlier = Snapshot(_earlier, true, ("alpha", 10, 1.26), ("beta", 10, 2.0), ("gamma", 10, 3.0), ("delta", 10, 4.0));
        var later = Snapshot(_later, true, ("alpha", 12, 12.34), ("beta", 12, 2.0), ("gamma", 10, 3.0), ("delta", 5, 0.0), ("zeta", 1, 0.5));

        // Act
        var view = ProgramComparisonView.Compare(earlier, later);

        // Assert
        Assert.AreEqual(string.Empty, view.Text, "a comparison has no text of a missing snapshot");
        Assert.AreEqual("2026-10-01 10:10:00 UTC to 2026-10-01 10:40:00 UTC (agent)", view.Heading, "the heading names both points and the source");
        Assert.HasCount(7, view.Table.Columns, "the columns");
        Assert.AreEqual("Program", view.Table.Columns[0], "the first column");
        Assert.AreEqual("Change", view.Table.Columns[3], "the change column");
        Assert.AreEqual("State", view.Table.Columns[6], "the state column");
        Assert.AreEqual("alpha|beta|zeta|gamma|delta", string.Join('|', view.Table.Rows.Select(row => Cell(row, 0))), "largest increase first, equal changes by name");
        Assert.AreSequenceEqual(["alpha", "10.0 MiB", "12.0 MiB", "+2.0 MiB", "1.3", "12.3", string.Empty], Cells(view.Table.Rows[0]), "the figures of the first row");
        Assert.AreEqual("+2.0 MiB", Cell(view.Table.Rows[1], 3), "the second row has the same change");
        Assert.AreEqual("-5.0 MiB", Cell(view.Table.Rows[4], 3), "a decrease is signed");
        Assert.AreEqual("10.0 MiB", Cell(view.Table.Rows[4], 1), "the memory at the earlier point");
        Assert.AreEqual("5.0 MiB", Cell(view.Table.Rows[4], 2), "the memory at the later point");
        Assert.AreEqual(string.Empty, view.Closing, "no row is left out");
        Assert.AreEqual(string.Empty, view.Note, "both snapshots list every program");
    }

    /// <summary>
    /// A program only at the later point is new and one only at the earlier point is gone, each with the absent side shown as two dashes and its whole memory as the change; the other rows have no state.
    /// </summary>
    [TestMethod]
    [TestCategory("AC6")]
    public void ProgramComparisonViewMarksNewAndGone()
    {
        // Arrange
        var earlier = Snapshot(_earlier, true, ("stays", 10, 1.0), ("gone-one", 3, 2.0));
        var later = Snapshot(_later, true, ("stays", 10, 1.0), ("new-one", 1, 4.0));

        // Act
        var view = ProgramComparisonView.Compare(earlier, later);

        // Assert
        var rows = view.Table.Rows.ToDictionary(row => Cell(row, 0), row => Cells(row), StringComparer.Ordinal);

        Assert.HasCount(3, rows, "three programs");
        Assert.AreSequenceEqual(["new-one", "--", "1.0 MiB", "+1.0 MiB", "--", "4.0", "new"], rows["new-one"], "the new program");
        Assert.AreSequenceEqual(["gone-one", "3.0 MiB", "--", "-3.0 MiB", "2.0", "--", "gone"], rows["gone-one"], "the gone program");
        Assert.AreEqual(string.Empty, rows["stays"][6], "a program at both points has no state");
        Assert.AreEqual("new-one|stays|gone-one", string.Join('|', view.Table.Rows.Select(row => Cell(row, 0))), "the order by change");
    }

    /// <summary>
    /// At most twenty rows are shown and the closing line says how many programs are left out, and how many of them are new and gone.
    /// </summary>
    [TestMethod]
    [TestCategory("AC6")]
    public void ProgramComparisonViewCutsAtTwentyRowsWithNewAndGoneCounts()
    {
        // Arrange
        var (earlier, later) = TwentyFivePrograms(true, true);

        // Act
        var view = ProgramComparisonView.Compare(earlier, later);

        // Assert
        Assert.HasCount(20, view.Table.Rows, "twenty rows");
        Assert.AreEqual("grow19", Cell(view.Table.Rows[0], 0), "the largest increase is the first row");
        Assert.AreEqual("grow0", Cell(view.Table.Rows[19], 0), "the twentieth row is the smallest of the growing programs");
        Assert.AreEqual("and 5 more programs (2 new, 1 gone).", view.Closing, "the closing line counts the rows left out, the new and the gone ones among them");
        Assert.AreEqual(string.Empty, view.Note, "no note");
    }

    /// <summary>
    /// The memory is PSS when every program of both snapshots has one, and RSS for both otherwise, and the headings of the memory columns say which.
    /// </summary>
    [TestMethod]
    [TestCategory("AC6")]
    public void ProgramComparisonViewUsesPssOnlyWhenBothSnapshotsHaveIt()
    {
        // Arrange
        var earlierPss = Figures(_earlier, true, ("a", Figure(100, 40, 1)), ("b", Figure(50, 20, 1)));
        var laterPss = Figures(_later, true, ("a", Figure(100, 60, 1)), ("b", Figure(50, 20, 1)));
        var laterMissing = Figures(_later, true, ("a", Figure(100, 60, 1)), ("b", Figure(50, null, 1)));
        var earlierMissing = Figures(_earlier, true, ("a", Figure(100, null, 1)), ("b", Figure(50, 20, 1)));

        // Act
        var both = ProgramComparisonView.Compare(earlierPss, laterPss);
        var laterWithout = ProgramComparisonView.Compare(earlierPss, laterMissing);
        var earlierWithout = ProgramComparisonView.Compare(earlierMissing, laterPss);

        // Assert
        Assert.Contains("(PSS)", both.Table.Columns[1], "the earlier column says PSS");
        Assert.Contains("(PSS)", both.Table.Columns[2], "the later column says PSS");
        Assert.AreSequenceEqual(["a", "40.0 MiB", "60.0 MiB", "+20.0 MiB"], Cells(both.Table.Rows[0]).Take(4).ToArray(), "the PSS figures");
        Assert.Contains("(RSS)", laterWithout.Table.Columns[1], "the earlier column says RSS when the later snapshot lacks a PSS");
        Assert.Contains("(RSS)", laterWithout.Table.Columns[2], "the later column says RSS");
        Assert.AreSequenceEqual(["a", "100.0 MiB", "100.0 MiB"], Cells(laterWithout.Table.Rows.Single(row => Cell(row, 0) == "a")).Take(3).ToArray(), "the RSS figures of both points");
        Assert.Contains("(RSS)", earlierWithout.Table.Columns[1], "the earlier snapshot lacking a PSS gives RSS too");
        Assert.Contains("(RSS)", earlierWithout.Table.Columns[2], "for both columns");
    }

    /// <summary>
    /// When either snapshot does not list every program no row is marked new or gone, the left-out count has no new and gone counts and the note says why.
    /// </summary>
    /// <param name="earlierLists">Whether the earlier snapshot lists every program</param>
    /// <param name="laterLists">Whether the later snapshot lists every program</param>
    [TestMethod]
    [TestCategory("AC6")]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(false, false)]
    public void ProgramComparisonViewMarksNothingWhenASnapshotDoesNotListEveryProgram(bool earlierLists, bool laterLists)
    {
        // Arrange
        var (earlier, later) = TwentyFivePrograms(earlierLists, laterLists);

        var small = ProgramComparisonView.Compare(Snapshot(_earlier, earlierLists, ("old", 3, 1.0)), Snapshot(_later, laterLists, ("fresh", 2, 1.0)));

        // Act
        var view = ProgramComparisonView.Compare(earlier, later);

        // Assert
        Assert.AreEqual(IncompleteNote, view.Note, "the note says no program is marked");
        Assert.AreEqual("and 5 more programs.", view.Closing, "the closing line has no counts");
        Assert.IsTrue(view.Table.Rows.All(row => Cell(row, 6).Length == 0), "no state in the shown rows");
        Assert.AreEqual(IncompleteNote, small.Note, "the note of a small comparison");
        Assert.AreSequenceEqual(["fresh", "--", "2.0 MiB", "+2.0 MiB", "--", "1.0", string.Empty], Cells(small.Table.Rows[0]), "a program of one point only shows the dashes and no state");
        Assert.AreSequenceEqual(["old", "3.0 MiB", "--", "-3.0 MiB", "1.0", "--", string.Empty], Cells(small.Table.Rows[1]), "a program of the other point only too");
    }

    /// <summary>
    /// A program name is displayed as external text: control and format characters are escaped visibly in separate runs, and a long name is cut.
    /// </summary>
    [TestMethod]
    [TestCategory("AC9")]
    public void ProgramComparisonViewShowsProgramNamesAsExternalText()
    {
        // Arrange
        var hostile = "evil" + RightToLeftOverride + "name" + Bell;
        var longName = new string('a', 300);
        var earlier = Snapshot(_earlier, true, (hostile, 1, 1.0), (longName, 1, 1.0));
        var later = Snapshot(_later, true, (hostile, 2, 1.0), (longName, 2, 1.0));

        // Act
        var view = ProgramComparisonView.Compare(earlier, later);

        // Assert
        var hostileRuns = view.Table.Rows.Select(row => row.Cells[0]).Single(runs => HistorySamples.Text(runs).StartsWith("evil", StringComparison.Ordinal));
        var longRuns = view.Table.Rows.Select(row => row.Cells[0]).Single(runs => HistorySamples.Text(runs).StartsWith("aaaa", StringComparison.Ordinal));

        var plainText = HistorySamples.Text(hostileRuns.Where(run => run.Escaped == false).ToArray());

        Assert.AreEqual("evil\\u202ename\\u0007", HistorySamples.Text(hostileRuns), "the characters are written as escapes");
        Assert.Contains(run => run.Escaped, hostileRuns, "the escapes are marked as escaped runs");
        Assert.DoesNotContain(RightToLeftOverride.ToString(), plainText, "no raw override character");
        Assert.DoesNotContain(Bell.ToString(), plainText, "no raw control character");
        Assert.AreEqual("evil", hostileRuns[0].Text, "the plain text before the escape");
        Assert.IsFalse(hostileRuns[0].Escaped, "plain");
        Assert.AreEqual("[cut: 44 more characters]", longRuns[^1].Text, "a long name is cut");
        Assert.IsTrue(longRuns[^1].Escaped, "the cut marker is escaped text");
    }

    /// <summary>
    /// A comparison without one of the two snapshots is refused.
    /// </summary>
    [TestMethod]
    [TestCategory("AC6")]
    public void ProgramComparisonViewCompareRefusesAMissingSnapshot()
    {
        // Arrange
        var figures = ProcessesSamples.Figures(HistorySamples.Base, true);

        // Act
        var first = Assert.Throws<ArgumentNullException>(() => ProgramComparisonView.Compare(null!, figures), "no earlier snapshot");
        var second = Assert.Throws<ArgumentNullException>(() => ProgramComparisonView.Compare(figures, null!), "no later snapshot");

        // Assert
        Assert.AreEqual("earlier", first.ParamName, "the earlier parameter");
        Assert.AreEqual("later", second.ParamName, "the later parameter");
    }

    /// <summary>
    /// Creates the figures of a snapshot of the source <c>agent</c> from programs with their RSS in MiB and their CPU percent.
    /// </summary>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="listsEveryProgram">Whether the snapshot lists every program</param>
    /// <param name="programs">The programs</param>
    /// <returns>The figures</returns>
    private static ProgramFigures Snapshot(DateTimeOffset capturedAt, bool listsEveryProgram, params (string Program, double RssMebibytes, double Cpu)[] programs)
    {
        return Figures(capturedAt, listsEveryProgram, [.. programs.Select(program => (program.Program, Figure(program.RssMebibytes, null, program.Cpu)))]);
    }

    /// <summary>
    /// Creates the figures of a snapshot with programs given as figures; the numbers are MiB for the memory.
    /// </summary>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="listsEveryProgram">Whether the snapshot lists every program</param>
    /// <param name="programs">The programs</param>
    /// <returns>The figures</returns>
    private static ProgramFigures Figures(DateTimeOffset capturedAt, bool listsEveryProgram, params (string Program, ProgramFigure Figure)[] programs)
    {
        return ProcessesSamples.Figures(capturedAt, listsEveryProgram, programs);
    }

    /// <summary>
    /// Creates the figures of a program with the memory in MiB.
    /// </summary>
    /// <param name="rss">The resident memory in MiB</param>
    /// <param name="pss">The proportional memory in MiB, <c>null</c> for none</param>
    /// <param name="cpu">The CPU percent</param>
    /// <returns>The figures</returns>
    private static ProgramFigure Figure(double rss, double? pss, double cpu)
    {
        return ProcessesSamples.Figure(rss * Mebibyte, pss * Mebibyte, cpu);
    }

    /// <summary>
    /// Creates two snapshots of twenty-five programs between them: twenty that grow by 1 to 20 MiB, two new ones that grow by a little, two that shrink by 1 MiB and one gone that shrinks by 3 MiB; the last five rank below the twenty.
    /// </summary>
    /// <param name="earlierLists">Whether the earlier snapshot lists every program</param>
    /// <param name="laterLists">Whether the later snapshot lists every program</param>
    /// <returns>The earlier and the later snapshot</returns>
    private static (ProgramFigures Earlier, ProgramFigures Later) TwentyFivePrograms(bool earlierLists, bool laterLists)
    {
        var earlier = new List<(string Program, double RssMebibytes, double Cpu)>();
        var later = new List<(string Program, double RssMebibytes, double Cpu)>();

        for (var index = 0; index < 20; index++)
        {
            earlier.Add(($"grow{index}", 1, 1.0));
            later.Add(($"grow{index}", 2 + index, 1.0));
        }

        earlier.AddRange([("shrinkA", 2, 1.0), ("shrinkB", 2, 1.0), ("goneC", 3, 1.0)]);
        later.AddRange([("shrinkA", 1, 1.0), ("shrinkB", 1, 1.0), ("newA", 1.0 / 1024, 1.0), ("newB", 1.0 / 1024, 1.0)]);

        return (Snapshot(_earlier, earlierLists, [.. earlier]), Snapshot(_later, laterLists, [.. later]));
    }

    /// <summary>
    /// Gives the text of a cell.
    /// </summary>
    /// <param name="row">The row</param>
    /// <param name="index">The column</param>
    /// <returns>The text</returns>
    private static string Cell(SnapshotRowView row, int index)
    {
        return HistorySamples.Text(row.Cells[index]);
    }

    /// <summary>
    /// Gives the texts of the cells of a row.
    /// </summary>
    /// <param name="row">The row</param>
    /// <returns>The texts</returns>
    private static string[] Cells(SnapshotRowView row)
    {
        return [.. row.Cells.Select(HistorySamples.Text)];
    }

    #endregion // Methods
}