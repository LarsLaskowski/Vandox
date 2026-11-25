namespace Vandox.Storage.Tests;

/// <summary>
/// Tests for <see cref="UnknownSpans"/>
/// </summary>
[TestClass]
public class UnknownSpansTests
{
    #region Methods

    /// <summary>
    /// The known parts of a window are the window without the gap spans and without the time from the completeness time to the end of the range.
    /// </summary>
    /// <param name="from">The start of the window in minutes</param>
    /// <param name="to">The end of the window in minutes</param>
    /// <param name="knownUntil">The time up to which the gaps are complete in minutes, or <c>null</c> when they are complete</param>
    /// <param name="expected">The known parts as <c>start-end</c> in minutes, separated by commas</param>
    [TestMethod]
    [TestCategory("AC3")]
    [DataRow(0, 60, 50, "0-10,20-30,40-50")]
    [DataRow(15, 35, 50, "20-30")]
    [DataRow(0, 5, 50, "0-5")]
    [DataRow(10, 20, 50, "")]
    [DataRow(45, 100, 50, "45-50")]
    [DataRow(60, 90, 50, "")]
    [DataRow(50, 100, 50, "")]
    [DataRow(0, 100, null, "0-10,20-30,40-100")]
    [DataRow(0, 100, 5, "0-5")]
    [DataRow(0, 100, 100, "0-10,20-30,40-100")]
    public void UnknownSpansKnownPartsSkipSpansAndCompleteUntil(int from, int to, int? knownUntil, string expected)
    {
        // Arrange
        var unknown = new UnknownSpans([(At(10), At(20)), (At(30), At(40))], knownUntil is null ? null : At(knownUntil.Value), At(100));

        // Act
        var parts = unknown.KnownParts(At(from), At(to));

        // Assert
        Assert.AreEqual(expected, string.Join(",", parts.Select(part => $"{Minutes(part.From)}-{Minutes(part.To)}")), $"the known parts of {from}-{to} minutes");
    }

    /// <summary>
    /// Without any gap span and with complete gaps the whole window is known.
    /// </summary>
    [TestMethod]
    [TestCategory("AC3")]
    public void UnknownSpansKnownPartsWithoutSpansIsWholeWindow()
    {
        // Arrange
        var unknown = new UnknownSpans([], null, At(100));

        // Act
        var parts = unknown.KnownParts(At(5), At(95));

        // Assert
        Assert.HasCount(1, parts, "one known part");
        Assert.AreEqual(At(5), parts[0].From, "the part starts with the window");
        Assert.AreEqual(At(95), parts[0].To, "the part ends with the window");
    }

    /// <summary>
    /// Gets an instant a number of minutes after <see cref="Samples.Base"/>.
    /// </summary>
    /// <param name="minutes">The minutes</param>
    /// <returns>The instant</returns>
    private static DateTimeOffset At(int minutes)
    {
        return Samples.Base.AddMinutes(minutes);
    }

    /// <summary>
    /// Gets the minutes after <see cref="Samples.Base"/> of an instant.
    /// </summary>
    /// <param name="instant">The instant</param>
    /// <returns>The minutes</returns>
    private static int Minutes(DateTimeOffset instant)
    {
        return (int)(instant - Samples.Base).TotalMinutes;
    }

    #endregion // Methods
}