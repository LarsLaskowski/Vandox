using Vandox.Backend.History;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="StackedLine"/>
/// </summary>
[TestClass]
public class StackedLineTests
{
    #region Methods

    /// <summary>
    /// A line of points has the averages divided by the factor as its values, the minimums and maximums for the legend, a color by its index, no bridge and a dash from the seventh.
    /// </summary>
    [TestMethod]
    [TestCategory("AC3")]
    public void StackedLineBuildDividesPointsByTheFactor()
    {
        // Arrange
        ProgramRollupPoint?[] points = [new ProgramRollupPoint(8, 4, 16), null, new ProgramRollupPoint(2, 1, 4)];

        // Act
        var line = StackedLine.Build("nginx", 7, points, [false, true, false], 2, null);

        // Assert
        Assert.AreEqual("nginx", line.Label, "the label");
        Assert.AreEqual(1, line.Color, "the color of index 7 is index 1 of six");
        Assert.IsTrue(line.Dashed, "dashed from the seventh");
        Assert.AreEqual(0, line.Scale, "one scale");
        Assert.AreSequenceEqual([4.0, null, 1.0], line.Values.ToArray(), "the averages divided by the factor");
        Assert.AreSequenceEqual([2.0, null, 0.5], line.Minimums.ToArray(), "the minimums");
        Assert.AreSequenceEqual([8.0, null, 2.0], line.Maximums.ToArray(), "the maximums");
        Assert.AreSequenceEqual([false, false, false], line.Bridged.ToArray(), "nothing is bridged, as long as the gaps");
    }

    /// <summary>
    /// A line without points takes its values from the other-values list, null at a gap and where the list has none, and has no minimum or maximum.
    /// </summary>
    [TestMethod]
    [TestCategory("AC2")]
    public void StackedLineBuildTakesTheOthersAndLeavesGapsNull()
    {
        // Act
        var line = StackedLine.Build("Other programs", 0, null, [false, true, false], 1, [3, 5, null]);

        // Assert
        Assert.AreSequenceEqual([3.0, null, null], line.Values.ToArray(), "the others, null at a gap and where the others are null");
        Assert.IsEmpty(line.Minimums, "no minimum");
        Assert.IsEmpty(line.Maximums, "no maximum");
        Assert.IsFalse(line.Dashed, "not dashed");
    }

    /// <summary>
    /// A hostile label is escaped and cut at 64 characters.
    /// </summary>
    [TestMethod]
    [TestCategory("AC9")]
    public void StackedLineBuildEscapesAndCapsTheLabel()
    {
        // Act
        var escaped = StackedLine.Build($"evil{(char)0x202E}name", 0, [], [], 1, null);
        var capped = StackedLine.Build(new string('a', 100), 0, [], [], 1, null);

        // Assert
        Assert.AreEqual("evil\\u202ename", escaped.Label, "the control character is written as an escape");
        Assert.AreEqual(new string('a', 64) + "[cut: 36 more characters]", capped.Label, "the label is capped at 64 characters");
    }

    #endregion // Methods
}