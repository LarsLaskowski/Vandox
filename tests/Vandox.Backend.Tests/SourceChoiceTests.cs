using Vandox.Backend.History;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="SourceChoice"/>
/// </summary>
[TestClass]
public class SourceChoiceTests
{
    #region Methods

    /// <summary>
    /// The source with the latest data is chosen, the first listed on a tie, and an empty list gives an empty name.
    /// </summary>
    [TestMethod]
    [TestCategory("AC7")]
    public void SourceChoiceLatestTakesTheLatestDataAndTheFirstOnATie()
    {
        // Arrange
        var now = HistorySamples.Base;
        var latest = ProcessesSamples.SourceList(false, ("agent", now.AddHours(-3)), ("import", now.AddHours(-1)), ("zeta", now.AddHours(-2)));
        var tie = ProcessesSamples.SourceList(false, ("beta", now.AddHours(-5)), ("agent", now.AddHours(-1)), ("import", now.AddHours(-1)));
        var empty = ProcessesSamples.SourceList(false);

        // Act and assert
        Assert.AreEqual("import", SourceChoice.Latest(latest), "the source with the latest data");
        Assert.AreEqual("agent", SourceChoice.Latest(tie), "the first listed of the latest data");
        Assert.AreEqual(string.Empty, SourceChoice.Latest(empty), "no source");
    }

    #endregion // Methods
}