using Vandox.Backend.History;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="SnapshotCells"/>
/// </summary>
[TestClass]
public class SnapshotCellsTests
{
    #region Methods

    /// <summary>
    /// External text is escaped visibly with a run marked escaped, and capped at 256 characters.
    /// </summary>
    [TestMethod]
    [TestCategory("AC9")]
    public void SnapshotCellsRunsEscapesAndCapsExternalText()
    {
        // Act
        var hostile = SnapshotCells.Runs($"web{(char)0x202E}1");
        var plain = SnapshotCells.Runs("nginx");
        var capped = SnapshotCells.Runs(new string('a', 300));

        // Assert
        Assert.AreEqual("web\\u202e1", HistorySamples.Text(hostile), "the control character is written as an escape");
        Assert.Contains(run => run.Escaped, hostile, "a run is marked escaped");
        Assert.AreEqual("nginx", HistorySamples.Text(plain), "plain text stays");
        Assert.IsTrue(plain.All(run => run.Escaped == false), "no run of plain text is escaped");
        Assert.AreEqual(new string('a', 256) + "[cut: 44 more characters]", HistorySamples.Text(capped), "the cap of 256 characters");
    }

    /// <summary>
    /// Runs are limited to 16: more escaped pieces than that end in a last run that holds the rest.
    /// </summary>
    [TestMethod]
    [TestCategory("AC9")]
    public void SnapshotCellsRunsLimitsTheRuns()
    {
        // Arrange
        var text = string.Concat(Enumerable.Repeat($"a{(char)7}", 40));

        // Act
        var runs = SnapshotCells.Runs(text);

        // Assert
        Assert.IsLessThanOrEqualTo(16, runs.Count, "at most 16 runs");
    }

    /// <summary>
    /// Numbers are written with the invariant culture and the page's own texts as they are, each as one run that is not escaped, and a row holds its cells in order.
    /// </summary>
    [TestMethod]
    [TestCategory("AC5")]
    public void SnapshotCellsPlainAndRowKeepTheirValues()
    {
        // Act
        var number = SnapshotCells.Plain(1234567);
        var decimalNumber = SnapshotCells.Plain(1.5);
        var text = SnapshotCells.Plain("no owner");
        var row = SnapshotCells.Row(number, text);

        // Assert
        Assert.AreEqual("1234567", HistorySamples.Text(number), "a number without grouping");
        Assert.AreEqual("1.5", HistorySamples.Text(decimalNumber), "a decimal number with a point");
        Assert.HasCount(1, number, "one run");
        Assert.IsFalse(number[0].Escaped, "not escaped");
        Assert.AreEqual("no owner", HistorySamples.Text(text), "the text as it is");
        Assert.IsFalse(text[0].Escaped, "not escaped");
        Assert.AreEqual("1234567|no owner", NetworkSamples.Cells(row), "the cells in order");
    }

    #endregion // Methods
}