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

    /// <summary>
    /// The hour start floors a nanosecond time to the start of its UTC hour, also before 1970.
    /// </summary>
    /// <param name="nanoseconds">The time in Unix nanoseconds</param>
    /// <param name="expected">The start of its hour in Unix nanoseconds</param>
    [TestMethod]
    [DataRow(0L, 0L)]
    [DataRow(7_200_000_000_000L, 7_200_000_000_000L)]
    [DataRow(9_000_000_000_000L, 7_200_000_000_000L)]
    [DataRow(10_799_999_999_999L, 7_200_000_000_000L)]
    [DataRow(1L, 0L)]
    [DataRow(-1L, -3_600_000_000_000L)]
    [DataRow(-3_600_000_000_000L, -3_600_000_000_000L)]
    [DataRow(-5_400_000_000_000L, -7_200_000_000_000L)]
    [DataRow(9_223_372_036_854_775_807L, 9_223_369_200_000_000_000L)]
    [DataRow(-9_223_369_200_000_000_000L, -9_223_369_200_000_000_000L)]
    public void StorageTimeHourStartFloorsToTheHour(long nanoseconds, long expected)
    {
        // Act
        var start = StorageTime.HourStart(nanoseconds);

        // Assert
        Assert.AreEqual(expected, start, $"hour start of {nanoseconds}");
    }

    /// <summary>
    /// A time of the first, partial hour of the storable range belongs to the hour that starts at the beginning of the range.
    /// </summary>
    /// <param name="nanoseconds">The time in Unix nanoseconds</param>
    [TestMethod]
    [DataRow(long.MinValue)]
    [DataRow(long.MinValue + 1)]
    [DataRow(long.MinValue + 1_000_000_000_000L)]
    [DataRow(-9_223_369_200_000_000_001L)]
    public void StorageTimeHourStartClampsTheFirstPartialHour(long nanoseconds)
    {
        // Act
        var start = StorageTime.HourStart(nanoseconds);

        // Assert
        Assert.AreEqual(long.MinValue, start, $"hour start of {nanoseconds} is clamped to the first instant");
    }

    #endregion // Methods
}