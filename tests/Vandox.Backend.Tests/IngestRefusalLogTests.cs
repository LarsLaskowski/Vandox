using Microsoft.Extensions.Time.Testing;

using Vandox.Backend.Ingest;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="IngestRefusalLog"/>
/// </summary>
[TestClass]
public class IngestRefusalLogTests
{
    #region Methods

    /// <summary>
    /// One line a minute gets through, and the next one after a quiet minute carries the number of refusals in between.
    /// </summary>
    [TestMethod]
    public void IngestRefusalLogAllowsOneLinePerMinuteWithSuppressedCount()
    {
        // Arrange
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));
        var log = new IngestRefusalLog(clock);

        // Act
        var first = log.TryTake(out var firstSuppressed);
        var second = log.TryTake(out _);
        var third = log.TryTake(out _);

        clock.Advance(TimeSpan.FromSeconds(59));

        var beforeMinute = log.TryTake(out _);

        clock.Advance(TimeSpan.FromSeconds(1));

        var afterMinute = log.TryTake(out var afterSuppressed);
        var immediately = log.TryTake(out _);

        clock.Advance(TimeSpan.FromSeconds(60));

        var nextMinute = log.TryTake(out var nextSuppressed);

        // Assert
        Assert.IsTrue(first, "the first refusal is logged");
        Assert.AreEqual(0, firstSuppressed, "nothing was suppressed before the first line");
        Assert.IsFalse(second, "a second refusal in the same minute is not logged");
        Assert.IsFalse(third, "a third refusal in the same minute is not logged");
        Assert.IsFalse(beforeMinute, "a refusal 59 seconds after the line is not logged");
        Assert.IsTrue(afterMinute, "a refusal a minute after the line is logged");
        Assert.AreEqual(3, afterSuppressed, "the refusals in between are counted");
        Assert.IsFalse(immediately, "the next refusal is suppressed again");
        Assert.IsTrue(nextMinute, "the line after another quiet minute is logged");
        Assert.AreEqual(1, nextSuppressed, "only the refusals since the last line are counted");
    }

    #endregion // Methods
}