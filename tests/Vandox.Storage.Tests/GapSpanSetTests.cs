namespace Vandox.Storage.Tests;

/// <summary>
/// Tests for <see cref="GapSpanSet"/>
/// </summary>
[TestClass]
public class GapSpanSetTests
{
    #region Methods

    /// <summary>
    /// Overlapping and touching spans become one span, separate ones stay apart.
    /// </summary>
    [TestMethod]
    [TestCategory("AC3")]
    [TestCategory("AC5")]
    public void GapSpanSetMergesOverlappingAndTouchingSpans()
    {
        // Arrange
        var set = new GapSpanSet();

        // Act
        set.Add(At(0), At(10));
        set.Add(At(10), At(20));
        set.Add(At(15), At(30));
        set.Add(At(40), At(50));
        set.Add(At(45), At(46));

        // Assert
        Assert.AreEqual("0-30,40-50", Describe(set), "touching and overlapping spans are one, the contained span adds nothing");
        Assert.IsNull(set.CompleteUntil, "nothing was dropped");
    }

    /// <summary>
    /// Spans added in any order give the same merged spans, sorted by start.
    /// </summary>
    [TestMethod]
    [TestCategory("AC3")]
    [TestCategory("AC5")]
    public void GapSpanSetMergesSpansAddedOutOfOrder()
    {
        // Arrange
        var set = new GapSpanSet();

        // Act
        set.Add(At(30), At(40));
        set.Add(At(50), At(60));
        set.Add(At(0), At(10));
        set.Add(At(10), At(30));
        set.Add(At(70), At(80));
        set.Add(At(55), At(75));

        // Assert
        Assert.AreEqual("0-40,50-80", Describe(set), "the spans are merged by start whatever the order of adding");
    }

    /// <summary>
    /// Past 1,000 spans the latest-starting one is dropped, and the set is complete up to its start.
    /// </summary>
    [TestMethod]
    [TestCategory("AC3")]
    [TestCategory("AC5")]
    public void GapSpanSetKeepsFirstThousandAndBoundsAtNext()
    {
        // Arrange
        var exactly = new GapSpanSet();
        var inOrder = new GapSpanSet();
        var descending = new GapSpanSet();

        // Act
        foreach (var index in Enumerable.Range(0, GapSpanSet.MaxSpans))
        {
            exactly.Add(At(index * 10), At((index * 10) + 5));
        }

        foreach (var index in Enumerable.Range(0, 1500))
        {
            inOrder.Add(At(index * 10), At((index * 10) + 5));
        }

        foreach (var index in Enumerable.Range(0, 1500).Reverse())
        {
            descending.Add(At(index * 10), At((index * 10) + 5));
        }

        // Assert
        Assert.AreEqual(1000, GapSpanSet.MaxSpans, "the cap");
        Assert.HasCount(1000, exactly.Spans, "1,000 spans are all kept");
        Assert.IsNull(exactly.CompleteUntil, "1,000 spans are complete");

        foreach (var set in new[] { inOrder, descending })
        {
            Assert.HasCount(1000, set.Spans, "the first 1,000 spans are kept");
            Assert.AreEqual(At(0), set.Spans[0].From, "the first span starts first");
            Assert.AreEqual(At(9995), set.Spans[999].To, "the last kept span is the 1,000th");
            Assert.AreEqual(At(10000), set.CompleteUntil, "complete up to the start of the first span that was dropped");
        }
    }

    /// <summary>
    /// A span that starts before the bound pushes out the latest span and lowers the bound to its start.
    /// </summary>
    [TestMethod]
    [TestCategory("AC3")]
    [TestCategory("AC5")]
    public void GapSpanSetLowersBoundWhenEarlierSpanPushesOutLatest()
    {
        // Arrange
        var set = new GapSpanSet();

        foreach (var index in Enumerable.Range(0, 1001))
        {
            set.Add(At(index * 10), At((index * 10) + 5));
        }

        // Act
        set.Add(At(6), At(8));

        // Assert
        Assert.HasCount(1000, set.Spans, "the cap holds");
        Assert.AreEqual(At(6), set.Spans[1].From, "the new span is kept");
        Assert.AreEqual(At(9980), set.Spans[999].From, "the second latest span is now the last one");
        Assert.AreEqual(At(9990), set.CompleteUntil, "complete up to the start of the span that was pushed out");
    }

    /// <summary>
    /// A span from the bound on is ignored, one that touches the last kept span still merges into it.
    /// </summary>
    [TestMethod]
    [TestCategory("AC3")]
    [TestCategory("AC5")]
    public void GapSpanSetIgnoresSpansAfterBound()
    {
        // Arrange
        var set = new GapSpanSet();

        foreach (var index in Enumerable.Range(0, 1001))
        {
            set.Add(At(index * 10), At((index * 10) + 5));
        }

        // Act
        set.Add(At(10000), At(10003));
        set.Add(At(20000), At(20005));
        set.Add(At(9995), At(9998));

        // Assert
        Assert.HasCount(1000, set.Spans, "no span was added");
        Assert.AreEqual(At(10000), set.CompleteUntil, "the bound did not move");
        Assert.AreEqual(At(9990), set.Spans[999].From, "the last kept span starts as before");
        Assert.AreEqual(At(9998), set.Spans[999].To, "the span touching the last kept span merged into it");
    }

    /// <summary>
    /// Gets an instant a number of seconds after <see cref="Samples.Base"/>.
    /// </summary>
    /// <param name="seconds">The seconds</param>
    /// <returns>The instant</returns>
    private static DateTimeOffset At(int seconds)
    {
        return Samples.Base.AddSeconds(seconds);
    }

    /// <summary>
    /// Describes the spans as <c>start-end</c> in seconds, separated by commas.
    /// </summary>
    /// <param name="set">The set</param>
    /// <returns>The text</returns>
    private static string Describe(GapSpanSet set)
    {
        return string.Join(",", set.Spans.Select(span => $"{(int)(span.From - Samples.Base).TotalSeconds}-{(int)(span.To - Samples.Base).TotalSeconds}"));
    }

    #endregion // Methods
}