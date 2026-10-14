using Vandox.Backend.Logs;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="TemplateHourSeries"/>
/// </summary>
[TestClass]
public class TemplateHourSeriesTests
{
    #region Fields

    private static readonly DateTimeOffset _first = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);

    #endregion // Fields

    #region Methods

    /// <summary>
    /// The points are the counts of the template hours.
    /// </summary>
    [TestMethod]
    public void BuildCountsTemplateHours()
    {
        // Arrange
        var hours = new[] { Hour(0, 5), Hour(1, 7), Hour(2, 1) };
        var covered = new[] { At(0), At(1), At(2) };

        // Act
        var data = TemplateHourSeries.Build(hours, covered, At(0), At(3));

        // Assert
        Assert.AreSequenceEqual([At(0), At(1), At(2)], data.Times.ToArray(), "one point per hour");
        Assert.HasCount(1, data.Series, "one series");
        Assert.AreEqual(LogsView.ChartSeries, data.Series[0].Label, "the fixed series label");
        Assert.AreSequenceEqual([5.0, 7.0, 1.0], data.Series[0].Values.ToArray(), "the counts of the template");
    }

    /// <summary>
    /// A covered hour without the template is 0.
    /// </summary>
    [TestMethod]
    public void BuildFillsCoveredHoursWithZero()
    {
        // Arrange
        var hours = new[] { Hour(1, 4) };
        var covered = new[] { At(0), At(1), At(2) };

        // Act
        var data = TemplateHourSeries.Build(hours, covered, At(0), At(3));

        // Assert
        Assert.AreSequenceEqual([At(0), At(1), At(2)], data.Times.ToArray(), "one point per covered hour");
        Assert.AreSequenceEqual([0.0, 4.0, 0.0], data.Series[0].Values.ToArray(), "zero where the source logged without the template");
    }

    /// <summary>
    /// An hour the source did not log is a gap.
    /// </summary>
    [TestMethod]
    public void BuildLeavesUncoveredHoursAsGaps()
    {
        // Arrange
        var hours = new[] { Hour(0, 3) };
        var covered = new[] { At(0), At(2) };

        // Act
        var data = TemplateHourSeries.Build(hours, covered, At(0), At(3));

        // Assert
        Assert.AreSequenceEqual([At(0), At(1), At(2)], data.Times.ToArray(), "every hour from the first to the last");
        Assert.AreSequenceEqual([3.0, null, 0.0], data.Series[0].Values.ToArray(), "a gap for the hour without lines of the source");
    }

    /// <summary>
    /// A range of more than 2,160 hours keeps its last 2,160 hours.
    /// </summary>
    [TestMethod]
    public void BuildKeepsTheLastHoursOfALongRange()
    {
        // Arrange
        const int total = 3000;

        var covered = Enumerable.Range(0, total).Select(At).ToArray();
        var hours = new[] { Hour(0, 9), Hour(total - 1, 2) };

        // Act
        var data = TemplateHourSeries.Build(hours, covered, At(0), At(total));

        // Assert
        Assert.HasCount(TemplateHourSeries.MaxHours, data.Times, "at most the last 2160 hours");
        Assert.AreEqual(At(total - TemplateHourSeries.MaxHours), data.Times[0], "the first kept hour");
        Assert.AreEqual(At(total - 1), data.Times[^1], "the last hour");
        Assert.AreEqual(2.0, data.Series[0].Values[^1], "the count of the last hour");
        Assert.AreEqual(0.0, data.Series[0].Values[0], "the early count is cut away");
    }

    /// <summary>
    /// Gets the start of an hour.
    /// </summary>
    /// <param name="offset">The hours after the first hour of the tests</param>
    /// <returns>The start of the hour</returns>
    private static DateTimeOffset At(int offset)
    {
        return _first.AddHours(offset);
    }

    /// <summary>
    /// Creates the count of a template in an hour.
    /// </summary>
    /// <param name="offset">The hours after the first hour of the tests</param>
    /// <param name="count">The number of lines</param>
    /// <returns>The template hour</returns>
    private static TemplateHour Hour(int offset, long count)
    {
        return new TemplateHour
               {
                   Hour = At(offset),
                   Count = count
               };
    }

    #endregion // Methods
}