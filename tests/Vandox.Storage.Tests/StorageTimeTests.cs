using Vandox.Core.Model;

namespace Vandox.Storage.Tests;

/// <summary>
/// Tests for <see cref="StorageTime"/>
/// </summary>
[TestClass]
public class StorageTimeTests
{
    #region Methods

    /// <summary>
    /// The range checks accept the bounds of <see cref="StorableTime"/> and refuse one tick outside, and agree with <see cref="StorableTime.Contains"/>.
    /// </summary>
    /// <param name="where">Which of the four instants is checked</param>
    /// <param name="inside">Whether the instant is inside the storable range</param>
    [TestMethod]
    [DataRow("minimum", true)]
    [DataRow("maximum", true)]
    [DataRow("before the minimum", false)]
    [DataRow("after the maximum", false)]
    public void StorageTimeRangeChecksAgreeWithStorableTime(string where, bool inside)
    {
        // Arrange
        var instant = where switch
                      {
                          "minimum" => StorableTime.Min,
                          "maximum" => StorableTime.Max,
                          "before the minimum" => StorableTime.Min.AddTicks(-1),
                          _ => StorableTime.Max.AddTicks(1)
                      };

        // Act
        var contains = StorableTime.Contains(instant);
        var storable = StorageTime.InStorableRange(instant);
        var outside = StorageTime.IsOutsideStorableRange(instant);

        // Assert
        Assert.AreEqual(inside, contains, "StorableTime.Contains");
        Assert.AreEqual(inside, storable, "StorageTime.InStorableRange");
        Assert.AreNotEqual(inside, outside, "StorageTime.IsOutsideStorableRange is the opposite");
    }

    /// <summary>
    /// The nanosecond conversion of the bounds stays inside a signed 64-bit number and round-trips.
    /// </summary>
    [TestMethod]
    public void StorageTimeNanosecondsOfTheBoundsRoundTrip()
    {
        // Act
        var minimum = StorageTime.ToNanoseconds(StorableTime.Min);
        var maximum = StorageTime.ToNanoseconds(StorableTime.Max);

        // Assert
        Assert.AreEqual(StorableTime.Min, StorageTime.FromNanoseconds(minimum), "minimum round trip");
        Assert.AreEqual(StorableTime.Max, StorageTime.FromNanoseconds(maximum), "maximum round trip");
        Assert.IsLessThan(0, minimum, "minimum is before the epoch");
        Assert.IsGreaterThan(0, maximum, "maximum is after the epoch");
    }

    #endregion // Methods
}