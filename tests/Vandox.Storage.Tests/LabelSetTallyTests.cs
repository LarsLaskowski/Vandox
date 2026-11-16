using System.Globalization;

namespace Vandox.Storage.Tests;

/// <summary>
/// Tests for <see cref="LabelSetTally"/>
/// </summary>
[TestClass]
public class LabelSetTallyTests
{
    #region Constants

    private const int Limit = 16;

    private const int Distinct = 10000;

    private const int Step = 7919;

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Of 10,000 distinct label texts added in scattered order, some of them twice, the tally keeps the smallest up to the limit in ordinal order, each with its first row, never holds more than the limit, and counts every distinct text once.
    /// </summary>
    [TestMethod]
    [TestCategory("AC5")]
    public void TallyKeepsTheSmallestUpToTheLimitAndCountsEverySet()
    {
        // Arrange
        var tally = new LabelSetTally(Limit);
        var firstAt = new Dictionary<int, DateTimeOffset>();

        // Act
        var mostKept = AddScattered(tally, firstAt);

        mostKept = Math.Max(mostKept, AddRepeats(tally));

        // Assert
        Assert.AreEqual(Distinct, tally.Count, "every distinct label text is counted once");
        Assert.IsLessThanOrEqualTo(Limit, mostKept, "the tally never holds more than the limit");

        var expected = Enumerable.Range(0, Limit).Select(index => string.Create(CultureInfo.InvariantCulture, $"{Labels(index)} {index} {firstAt[index]:O}"));
        var kept = tally.Kept.Select(entry => string.Create(CultureInfo.InvariantCulture, $"{entry.Labels} {entry.Value} {entry.CapturedAt:O}"));

        Assert.AreEqual(string.Join(Environment.NewLine, expected), string.Join(Environment.NewLine, kept), "the smallest label texts in ordinal order, each with its first row");
    }

    /// <summary>
    /// Adds every distinct label text once, in an order that scatters the smallest ones, each one second older than the one before.
    /// </summary>
    /// <param name="tally">The tally</param>
    /// <param name="firstAt">Receives the capture time of each index</param>
    /// <returns>The highest number of kept texts seen after an add</returns>
    private static int AddScattered(LabelSetTally tally, Dictionary<int, DateTimeOffset> firstAt)
    {
        var mostKept = 0;

        for (var position = 0; position < Distinct; position++)
        {
            var index = (int)((long)position * Step % Distinct);
            var at = Samples.Base.AddSeconds(-position);

            firstAt[index] = at;
            tally.Add(Labels(index), at, index);
            mostKept = Math.Max(mostKept, tally.Kept.Count);
        }

        return mostKept;
    }

    /// <summary>
    /// Adds the smallest label texts again, with older rows and other values, as later rows of the same label set.
    /// </summary>
    /// <param name="tally">The tally</param>
    /// <returns>The highest number of kept texts seen after an add</returns>
    private static int AddRepeats(LabelSetTally tally)
    {
        var mostKept = 0;

        for (var index = 0; index < 100; index++)
        {
            tally.Add(Labels(index), Samples.Base.AddDays(-1), -1);
            mostKept = Math.Max(mostKept, tally.Kept.Count);
        }

        return mostKept;
    }

    /// <summary>
    /// Creates the label text of an index, which sorts in ordinal order by the index.
    /// </summary>
    /// <param name="index">The index</param>
    /// <returns>The label text</returns>
    private static string Labels(int index)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{{\"mount\":\"/m{index:D5}\"}}");
    }

    #endregion // Methods
}