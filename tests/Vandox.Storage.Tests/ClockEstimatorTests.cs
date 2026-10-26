using Vandox.Core.Model;

namespace Vandox.Storage.Tests;

/// <summary>
/// Tests for <see cref="ClockEstimator"/>
/// </summary>
[TestClass]
public class ClockEstimatorTests
{
    #region Constants

    private const long NanosecondsPerSecond = 1_000_000_000L;

    #endregion // Constants

    #region Methods

    /// <summary>
    /// A bound is the capture time minus the receive time in nanoseconds.
    /// </summary>
    [TestMethod]
    public void ClockEstimatorBoundOfIsCaptureMinusReceiveInNanoseconds()
    {
        // Arrange
        var received = Samples.Base;

        // Act
        var ahead = ClockEstimator.BoundOf(received.AddSeconds(7197), received);
        var behind = ClockEstimator.BoundOf(received.AddSeconds(-7203), received);
        var oneTick = ClockEstimator.BoundOf(received.AddTicks(1), received);

        // Assert
        Assert.AreEqual(7197 * NanosecondsPerSecond, ahead, "capture 7197 s after receipt");
        Assert.AreEqual(-7203 * NanosecondsPerSecond, behind, "capture 7203 s before receipt");
        Assert.AreEqual(100L, oneTick, "one tick is 100 ns");
    }

    /// <summary>
    /// A receive time is floored to a bucket of 10 seconds.
    /// </summary>
    [TestMethod]
    public void ClockEstimatorBucketOfFloorsToTenSeconds()
    {
        // Arrange
        var inside = Samples.Base.AddSeconds(7).AddTicks(5_000_000);
        var onEdge = Samples.Base.AddSeconds(10);
        var justBefore = Samples.Base.AddSeconds(10).AddTicks(-1);

        // Act
        var insideBucket = ClockEstimator.BucketOf(inside);
        var edgeBucket = ClockEstimator.BucketOf(onEdge);
        var beforeBucket = ClockEstimator.BucketOf(justBefore);

        // Assert
        Assert.AreEqual(Samples.Base, insideBucket, "10:00:07.5 belongs to the bucket of 10:00:00");
        Assert.AreEqual(onEdge, edgeBucket, "a bucket starts at its own first instant");
        Assert.AreEqual(Samples.Base, beforeBucket, "the instant before the edge belongs to the bucket before");
    }

    /// <summary>
    /// A clock 2 hours behind is measured as the largest bound of the counted samples, whatever the run-timed records say.
    /// </summary>
    [TestMethod]
    public void ClockEstimatorBehindTwoHoursMeasuredFromCountedSample()
    {
        // Arrange
        var counted = new[]
                      {
                          Sample(0, -7203),
                          Sample(60, -7201),
                          Sample(120, -7206)
                      };
        var run = new[]
                  {
                      Sample(0, 30),
                      Sample(130, 7000)
                  };

        // Act
        var actual = ClockEstimator.Estimate(counted, run);

        // Assert
        Assert.AreEqual(-7201 * NanosecondsPerSecond, actual, "the tightest counted bound, no floor at 0");
    }

    /// <summary>
    /// A clock 2 hours ahead is measured, before the first counted sample, from the largest bound of run-timed records.
    /// </summary>
    [TestMethod]
    public void ClockEstimatorAheadTwoHoursFromRunTimedRecordsBeforeCountedSample()
    {
        // Arrange
        var run = new[]
                  {
                      Sample(0, 7195),
                      Sample(10, 7199),
                      Sample(20, 7190)
                  };

        // Act
        var actual = ClockEstimator.Estimate(Array.Empty<ClockSample>(), run);

        // Assert
        Assert.AreEqual(7199 * NanosecondsPerSecond, actual, "the largest run-timed bound");
    }

    /// <summary>
    /// Before the first counted sample, a clock that shows no proof of being ahead gives an offset of 0.
    /// </summary>
    [TestMethod]
    public void ClockEstimatorBeforeCountedSampleWithoutProofOfAheadClockIsZero()
    {
        // Arrange
        var behind = new[]
                     {
                         Sample(0, -7203),
                         Sample(10, -7201)
                     };

        // Act
        var fromBehind = ClockEstimator.Estimate(Array.Empty<ClockSample>(), behind);
        var fromNothing = ClockEstimator.Estimate(Array.Empty<ClockSample>(), Array.Empty<ClockSample>());

        // Assert
        Assert.AreEqual(0L, fromBehind, "only negative bounds");
        Assert.AreEqual(0L, fromNothing, "no samples at all");
    }

    /// <summary>
    /// Log lines and kernel events give no bound, so a future-stamped one cannot raise the estimate.
    /// </summary>
    [TestMethod]
    public void ClockEstimatorLogLineNeverRaisesEstimate()
    {
        // Arrange
        var received = Samples.Base;
        var records = new[]
                      {
                          (Kind: RecordKind.Metric, Captured: received.AddSeconds(-2)),
                          (Kind: RecordKind.LogLine, Captured: received.AddHours(2)),
                          (Kind: RecordKind.KernelEvent, Captured: received.AddHours(2))
                      };
        var run = records.Where(r => LiveRules.IsRunTimed(r.Kind))
                         .Select(r => new ClockSample(ClockEstimator.BucketOf(received), ClockEstimator.BoundOf(r.Captured, received)))
                         .ToArray();

        // Act
        var actual = ClockEstimator.Estimate(Array.Empty<ClockSample>(), run);

        // Assert
        Assert.HasCount(1, run, "only the metric gives a bound");
        Assert.AreEqual(0L, actual, "the metric proves no clock ahead");
    }

    /// <summary>
    /// A counted sample that arrives with a longer delay never lowers the estimate while a tighter one is in the window.
    /// </summary>
    [TestMethod]
    public void ClockEstimatorLateSampleNeverLowersEstimate()
    {
        // Arrange
        var counted = new[]
                      {
                          Sample(0, -7201),
                          Sample(60, -7260),
                          Sample(70, -7500)
                      };

        // Act
        var actual = ClockEstimator.Estimate(counted, Array.Empty<ClockSample>());

        // Assert
        Assert.AreEqual(-7201 * NanosecondsPerSecond, actual, "the late samples bound the clock more loosely");
    }

    /// <summary>
    /// After the clock is stepped back by 5 minutes the estimate follows within 5 minutes of samples.
    /// </summary>
    [TestMethod]
    public void ClockEstimatorBackwardStepRecoversWithinFiveMinutes()
    {
        // Arrange
        var duringStep = new[]
                         {
                             Sample(0, -2),
                             Sample(290, -302)
                         };
        var afterStep = new[]
                        {
                            Sample(0, -2),
                            Sample(290, -302),
                            Sample(310, -301)
                        };

        // Act
        var during = ClockEstimator.Estimate(duringStep, Array.Empty<ClockSample>());
        var after = ClockEstimator.Estimate(afterStep, Array.Empty<ClockSample>());

        // Assert
        Assert.AreEqual(-2 * NanosecondsPerSecond, during, "samples from before the step are still in the window");
        Assert.AreEqual(-301 * NanosecondsPerSecond, after, "more than 5 minutes after the last sample from before the step");
    }

    /// <summary>
    /// A run-timed bound from before the first counted sample no longer counts once the newest bucket lies more than 5 minutes after it.
    /// </summary>
    [TestMethod]
    public void ClockEstimatorPreSampleBoundExpiresAfterFiveMinutes()
    {
        // Arrange
        var inWindow = new[]
                       {
                           Sample(0, 7200),
                           Sample(290, 0)
                       };
        var expired = new[]
                      {
                          Sample(0, 7200),
                          Sample(310, 0)
                      };

        // Act
        var within = ClockEstimator.Estimate(Array.Empty<ClockSample>(), inWindow);
        var after = ClockEstimator.Estimate(Array.Empty<ClockSample>(), expired);

        // Assert
        Assert.AreEqual(7200 * NanosecondsPerSecond, within, "the bound is 290 s old");
        Assert.AreEqual(0L, after, "the bound is 310 s old");
    }

    /// <summary>
    /// When the largest bound expires, the estimate falls back to the largest bound still in the window, not to the newest bucket, which a backfill batch may hold.
    /// </summary>
    [TestMethod]
    public void ClockEstimatorExpiryFallsBackToWindowMaximumNotBackfillBatch()
    {
        // Arrange
        var run = new[]
                  {
                      Sample(0, 7199),
                      Sample(120, 7197),
                      Sample(200, 7190),
                      Sample(310, -3600)
                  };

        // Act
        var actual = ClockEstimator.Estimate(Array.Empty<ClockSample>(), run);

        // Assert
        Assert.AreEqual(7197 * NanosecondsPerSecond, actual, "the maximum of the buckets from 10:00:10 on, backfill bucket included");
    }

    /// <summary>
    /// Extreme capture and receive times give bounds saturated to the range of a long, and extreme bounds are estimated without an exception.
    /// </summary>
    [TestMethod]
    public void ClockEstimatorSaturatesExtremeBounds()
    {
        // Arrange
        var high = new[] { new ClockSample(Samples.Base, long.MaxValue) };
        var low = new[] { new ClockSample(Samples.Base, long.MinValue) };

        // Act
        var maximum = ClockEstimator.BoundOf(StorableTime.Max, StorableTime.Min);
        var minimum = ClockEstimator.BoundOf(StorableTime.Min, StorableTime.Max);
        var countedHigh = ClockEstimator.Estimate(high, Array.Empty<ClockSample>());
        var countedLow = ClockEstimator.Estimate(low, Array.Empty<ClockSample>());
        var runHigh = ClockEstimator.Estimate(Array.Empty<ClockSample>(), high);
        var runLow = ClockEstimator.Estimate(Array.Empty<ClockSample>(), low);

        // Assert
        Assert.AreEqual(long.MaxValue, maximum, "the largest possible difference");
        Assert.AreEqual(long.MinValue, minimum, "the smallest possible difference");
        Assert.AreEqual(long.MaxValue, countedHigh, "a counted sample at the top");
        Assert.AreEqual(long.MinValue, countedLow, "a counted sample at the bottom");
        Assert.AreEqual(long.MaxValue, runHigh, "a run-timed bound at the top");
        Assert.AreEqual(0L, runLow, "a run-timed bound at the bottom is floored at 0");
    }

    /// <summary>
    /// Creates a sample whose bucket lies a number of seconds after <see cref="Samples.Base"/>.
    /// </summary>
    /// <param name="seconds">The seconds after <see cref="Samples.Base"/>, a multiple of 10</param>
    /// <param name="boundSeconds">The bound in seconds</param>
    /// <returns>The sample</returns>
    private static ClockSample Sample(int seconds, long boundSeconds)
    {
        return new ClockSample(Samples.Base.AddSeconds(seconds), boundSeconds * NanosecondsPerSecond);
    }

    #endregion // Methods
}