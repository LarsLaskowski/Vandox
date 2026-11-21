namespace Vandox.Storage.Tests;

/// <summary>
/// Tests for <see cref="SeriesLadder"/>
/// </summary>
[TestClass]
public class SeriesLadderTests
{
    #region Constants

    private static readonly DateTimeOffset _day = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    #endregion // Constants

    #region Methods

    /// <summary>
    /// The smallest step with at most the target steps over the range is chosen.
    /// </summary>
    /// <param name="hours">The length of the range in hours, starting at midnight</param>
    /// <param name="target">The target</param>
    /// <param name="expectedMinutes">The expected step in minutes</param>
    [TestMethod]
    [TestCategory("AC3")]
    [DataRow(1, 100, 5)]
    [DataRow(24, 288, 5)]
    [DataRow(24, 287, 10)]
    [DataRow(24, 24, 60)]
    [DataRow(24, 12, 120)]
    [DataRow(24, 1, 1440)]
    [DataRow(240, 5, 2880)]
    public void SeriesLadderChooseTakesTheSmallestStepWithinTheTarget(int hours, int target, int expectedMinutes)
    {
        // Arrange
        var end = _day.AddHours(hours);

        // Act
        var step = SeriesLadder.Choose(_day, end, end, target, TimeSeriesSamples.Unlimited());

        // Assert
        Assert.AreEqual(TimeSpan.FromMinutes(expectedMinutes), step, "the smallest step of the ladder that fits the target");
    }

    /// <summary>
    /// A step under an hour is skipped when the range starts before the period of the five-minute rollups.
    /// </summary>
    /// <param name="startDays">The days before the query time the range starts</param>
    /// <param name="expectedMinutes">The expected step in minutes</param>
    [TestMethod]
    [TestCategory("AC3")]
    [DataRow(1, 5)]
    [DataRow(400, 60)]
    public void SeriesLadderChooseSkipsStepsBelowAnHourBeforeTheFiveMinutePeriod(int startDays, int expectedMinutes)
    {
        // Arrange
        var now = _day.AddDays(500);
        var from = now.AddDays(-startDays);

        // Act
        var step = SeriesLadder.Choose(from, from.AddHours(1), now, 100, RetentionSamples.Defaults());

        // Assert
        Assert.AreEqual(TimeSpan.FromMinutes(expectedMinutes), step, "the smallest step the rollups can serve");
    }

    /// <summary>
    /// The overload on plain times picks the steps of the time-series query for the same range, target and policy.
    /// </summary>
    /// <param name="startDays">The days before the query time the range starts</param>
    /// <param name="lengthHours">The length of the range in hours</param>
    /// <param name="target">The target</param>
    [TestMethod]
    [TestCategory("AC3")]
    [DataRow(1, 6, 100)]
    [DataRow(2, 48, 30)]
    [DataRow(30, 24, 700)]
    [DataRow(400, 720, 100)]
    [DataRow(500, 9000, 7)]
    [DataRow(500, 9000, 2000)]
    public void SeriesLadderChoosePicksTheStepsOfTheTimeSeriesQuery(int startDays, int lengthHours, int target)
    {
        // Arrange
        var now = _day.AddDays(600);
        var from = now.AddDays(-startDays);
        var end = from.AddHours(lengthHours) < now ? from.AddHours(lengthHours) : now;
        var policy = RetentionSamples.Defaults();
        var range = new SeriesRange
                    {
                        From = from,
                        End = end,
                        Now = now,
                        Target = target
                    };

        // Act
        var step = SeriesLadder.Choose(from, end, now, target, policy);

        // Assert
        Assert.AreEqual(SeriesLadder.Choose(range, policy), step, "the same step as the query reads");
    }

    /// <summary>
    /// A target below one is refused.
    /// </summary>
    /// <param name="target">The target</param>
    [TestMethod]
    [TestCategory("AC3")]
    [DataRow(0)]
    [DataRow(-1)]
    public void SeriesLadderChooseRefusesATargetBelowOne(int target)
    {
        // Arrange
        var end = _day.AddHours(1);

        // Act
        var exception = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SeriesLadder.Choose(_day, end, end, target, TimeSeriesSamples.Unlimited()), "the target is refused");

        // Assert
        Assert.IsNotNull(exception, "the refusal is reported");
    }

    /// <summary>
    /// A target above the maximum of the time-series query is refused, and the maximum itself is accepted.
    /// </summary>
    [TestMethod]
    [TestCategory("AC3")]
    public void SeriesLadderChooseRefusesATargetAboveTheMaximum()
    {
        // Arrange
        var end = _day.AddHours(1);
        var policy = TimeSeriesSamples.Unlimited();

        // Act
        var exception = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SeriesLadder.Choose(_day, end, end, TimeSeriesService.MaxTarget + 1, policy), "the target above the maximum is refused");
        var step = SeriesLadder.Choose(_day, end, end, TimeSeriesService.MaxTarget, policy);

        // Assert
        Assert.IsNotNull(exception, "the refusal is reported");
        Assert.AreEqual(TimeSpan.FromMinutes(5), step, "the maximum itself is a target");
    }

    #endregion // Methods
}