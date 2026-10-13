using Vandox.Core.Reports;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="ObservedHours"/>
/// </summary>
[TestClass]
public class ObservedHoursTests
{
    #region Constants

    private static readonly DateTimeOffset _day = new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);

    #endregion // Constants

    #region Methods

    /// <summary>
    /// A UTC hour counts once however many times it is added, and the hours of the day are counted per distinct hour.
    /// </summary>
    [TestMethod]
    public void ObservedHoursCountsDistinctHoursAndHoursOfDay()
    {
        // Arrange
        var hours = new ObservedHours();

        // Act
        hours.Add(_day.AddHours(2).AddMinutes(5));
        hours.Add(_day.AddHours(2).AddMinutes(50));
        hours.Add(_day.AddHours(3));
        hours.Add(_day.AddHours(26).AddMinutes(1));
        hours.Add(new DateTimeOffset(2026, 10, 3, 6, 30, 0, TimeSpan.FromHours(2)));

        // Assert
        Assert.AreEqual(4, hours.Count, "distinct UTC hours: 2, 3, 26 and 28 (06:30 at offset +02:00 is 04:30 UTC)");
        Assert.HasCount(24, hours.HoursOfDay, "one count per hour of the day");
        Assert.AreEqual(0, hours.HoursOfDay[0], "no hour 0");
        Assert.AreEqual(2, hours.HoursOfDay[2], "hour 2 was observed on two days");
        Assert.AreEqual(1, hours.HoursOfDay[3], "hour 3");
        Assert.AreEqual(1, hours.HoursOfDay[4], "hour 4 by the UTC instant, not by the offset");
        Assert.AreEqual(4, hours.HoursOfDay.Sum(), "the counts add up to the number of hours");
        Assert.IsFalse(hours.Truncated, "not truncated");
    }

    /// <summary>
    /// Times before 1970 fall into the hour that contains them: the division rounds down.
    /// </summary>
    [TestMethod]
    public void ObservedHoursPlacesTimesBeforeTheEpochInTheirHour()
    {
        // Arrange
        var before = new DateTimeOffset(1969, 12, 31, 23, 59, 59, TimeSpan.Zero).AddTicks(9999999);
        var hours = new ObservedHours();

        // Act
        hours.Add(new DateTimeOffset(1969, 12, 31, 22, 30, 0, TimeSpan.Zero));
        hours.Add(before);

        // Assert
        Assert.AreEqual(-1, ObservedHours.HourOf(before), "the last tick before the epoch is hour -1");
        Assert.AreEqual(0, ObservedHours.HourOf(DateTimeOffset.UnixEpoch), "the epoch starts hour 0");
        Assert.AreEqual(-2, ObservedHours.HourOf(new DateTimeOffset(1969, 12, 31, 22, 30, 0, TimeSpan.Zero)), "22:30 is hour -2");
        Assert.AreEqual(1, ObservedHours.HourOf(new DateTimeOffset(1970, 1, 1, 3, 30, 0, TimeSpan.FromHours(2))), "the UTC instant counts, not the offset");
        Assert.AreEqual(2, hours.Count, "two hours");
        Assert.AreEqual(1, hours.HoursOfDay[22], "hour of day 22");
        Assert.AreEqual(1, hours.HoursOfDay[23], "hour of day 23");
    }

    /// <summary>
    /// A period is covered only when every hour that overlaps it is observed; the end of the period is exclusive.
    /// </summary>
    [TestMethod]
    public void ObservedHoursCoversOnlyWhenEveryHourIsObserved()
    {
        // Arrange
        var hours = new ObservedHours();

        hours.Add(_day.AddHours(10).AddMinutes(30));
        hours.Add(_day.AddHours(11).AddMinutes(10));
        hours.Add(_day.AddHours(13).AddMinutes(10));

        // Act and Assert
        Assert.IsTrue(hours.Covers(_day.AddHours(10).AddMinutes(15), _day.AddHours(10).AddMinutes(45)), "inside one observed hour");
        Assert.IsTrue(hours.Covers(_day.AddHours(10).AddMinutes(15), _day.AddHours(12)), "two observed hours, the end is exclusive");
        Assert.IsFalse(hours.Covers(_day.AddHours(10).AddMinutes(15), _day.AddHours(12).AddTicks(1)), "the hour 12 overlaps and is not observed");
        Assert.IsFalse(hours.Covers(_day.AddHours(9).AddMinutes(59), _day.AddHours(10).AddMinutes(30)), "the hour 9 is not observed");
        Assert.IsFalse(hours.Covers(_day.AddHours(11), _day.AddHours(14)), "a gap in the middle");
        Assert.AreSequenceEqual([_day.AddHours(11), _day.AddHours(12), _day.AddHours(14)], hours.Ends().ToList(), "the end of every observed hour, ascending");
        Assert.ThrowsExactly<ArgumentException>(() => hours.Covers(_day.AddHours(10), _day.AddHours(10)), "an empty period is refused");
    }

    /// <summary>
    /// Runs of consecutive hours are kept up to the cap; beyond it the hours are dropped and the series says so.
    /// </summary>
    [TestMethod]
    public void ObservedHoursTruncatesAfterMaxRuns()
    {
        // Arrange
        var hours = new ObservedHours(2);

        // Act
        hours.Add(_day.AddHours(0).AddMinutes(10));
        hours.Add(_day.AddHours(1).AddMinutes(10));
        hours.Add(_day.AddHours(3).AddMinutes(10));

        var beforeThird = hours.Truncated;

        hours.Add(_day.AddHours(5).AddMinutes(10));
        hours.Add(_day.AddHours(7).AddMinutes(10));

        // Assert
        Assert.IsFalse(beforeThird, "two runs fit");
        Assert.IsTrue(hours.Truncated, "a third run does not");
        Assert.AreSequenceEqual([_day.AddHours(1), _day.AddHours(2), _day.AddHours(4)], hours.Ends().ToList(), "only the hours of the two kept runs");
    }

    /// <summary>
    /// A time that is earlier than the previous one is refused.
    /// </summary>
    [TestMethod]
    public void ObservedHoursRefusesEarlierTime()
    {
        // Arrange
        var hours = new ObservedHours();

        hours.Add(_day.AddHours(5));

        // Act and Assert
        Assert.ThrowsExactly<ArgumentException>(() => hours.Add(_day.AddHours(4)), "an earlier hour");
        Assert.ThrowsExactly<ArgumentException>(() => hours.Add(_day.AddHours(5).AddTicks(-1)), "an earlier time in the same hour");
    }

    #endregion // Methods
}