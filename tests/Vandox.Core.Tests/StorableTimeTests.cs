using Vandox.Core.Model;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="StorableTime"/>
/// </summary>
[TestClass]
public class StorableTimeTests
{
    #region Methods

    /// <summary>
    /// The bounds are the range of storage, which counts nanoseconds in a signed 64-bit number.
    /// </summary>
    [TestMethod]
    public void StorableTimeBoundsAreTheNanosecondRangeOfStorage()
    {
        // Arrange
        var epochTicks = DateTimeOffset.UnixEpoch.UtcTicks;

        // Act
        var minimum = (StorableTime.Min.UtcTicks - epochTicks) * 100;
        var maximum = (StorableTime.Max.UtcTicks - epochTicks) * 100;

        // Assert
        Assert.AreEqual(new DateTimeOffset(1677, 9, 21, 0, 12, 43, TimeSpan.Zero).AddTicks(1452242), StorableTime.Min, "minimum is 1677-09-21T00:12:43.1452242Z");
        Assert.AreEqual(new DateTimeOffset(2262, 4, 11, 23, 47, 16, TimeSpan.Zero).AddTicks(8547758), StorableTime.Max, "maximum is 2262-04-11T23:47:16.8547758Z");
        Assert.AreEqual(TimeSpan.Zero, StorableTime.Min.Offset, "minimum is UTC");
        Assert.AreEqual(TimeSpan.Zero, StorableTime.Max.Offset, "maximum is UTC");
        Assert.IsLessThan(0, minimum, "minimum is before the epoch");
        Assert.IsGreaterThan(long.MaxValue - 100, maximum, "maximum is the largest nanosecond count, rounded to ticks");
    }

    /// <summary>
    /// The bounds belong to the range, one tick outside does not.
    /// </summary>
    [TestMethod]
    public void StorableTimeContainsIncludesTheBoundsAndExcludesOneTickOutside()
    {
        // Act
        var atMinimum = StorableTime.Contains(StorableTime.Min);
        var atMaximum = StorableTime.Contains(StorableTime.Max);
        var beforeMinimum = StorableTime.Contains(StorableTime.Min.AddTicks(-1));
        var afterMaximum = StorableTime.Contains(StorableTime.Max.AddTicks(1));

        // Assert
        Assert.IsTrue(atMinimum, "the minimum is inside");
        Assert.IsTrue(atMaximum, "the maximum is inside");
        Assert.IsFalse(beforeMinimum, "one tick before the minimum is outside");
        Assert.IsFalse(afterMaximum, "one tick after the maximum is outside");
    }

    /// <summary>
    /// Ordinary times, the epoch and times in other offsets are compared by their UTC instant.
    /// </summary>
    [TestMethod]
    public void StorableTimeContainsComparesTheUtcInstant()
    {
        // Arrange
        var berlin = TimeSpan.FromHours(1);
        var justInside = new DateTimeOffset(StorableTime.Min.UtcTicks, TimeSpan.Zero).ToOffset(berlin);
        var justOutside = new DateTimeOffset(StorableTime.Min.UtcTicks - 1, TimeSpan.Zero).ToOffset(berlin);

        // Act and Assert
        Assert.IsTrue(StorableTime.Contains(DateTimeOffset.UnixEpoch), "the epoch is inside");
        Assert.IsTrue(StorableTime.Contains(new DateTimeOffset(2026, 3, 1, 12, 0, 0, berlin)), "an ordinary time is inside");
        Assert.IsTrue(StorableTime.Contains(justInside), "the minimum written in another offset is inside");
        Assert.IsFalse(StorableTime.Contains(justOutside), "one tick before the minimum in another offset is outside");
        Assert.IsFalse(StorableTime.Contains(DateTimeOffset.MinValue), "year 1 is outside");
        Assert.IsFalse(StorableTime.Contains(DateTimeOffset.MaxValue), "year 9999 is outside");
    }

    #endregion // Methods
}