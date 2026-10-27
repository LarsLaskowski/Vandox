using Vandox.Core.Model;

namespace Vandox.Storage.Tests;

/// <summary>
/// Tests for <see cref="LiveRules"/>
/// </summary>
[TestClass]
public class LiveRulesTests
{
    #region Constants

    private const long NanosecondsPerSecond = 1_000_000_000L;

    #endregion // Constants

    #region Methods

    /// <summary>
    /// A run-timed record whose lateness lies within 90 seconds, late or early, is live.
    /// </summary>
    /// <param name="lateSeconds">The seconds the capture time lies before the receive time; negative when after</param>
    [TestMethod]
    [DataRow(0.0)]
    [DataRow(45.0)]
    [DataRow(90.0)]
    [DataRow(-90.0)]
    public void LiveRulesMetricWithinNinetySecondsIsLive(double lateSeconds)
    {
        // Arrange
        var record = RecordOf(RecordKind.Metric, 10, Samples.Base - TimeSpan.FromSeconds(lateSeconds));

        // Act
        var actual = LiveRules.IsLive(record, Samples.Base, 0, null);

        // Assert
        Assert.IsTrue(actual, $"lateness {lateSeconds} s is within the allowance of 90 s");
    }

    /// <summary>
    /// A run-timed record whose lateness exceeds 90 seconds, however slightly, late or early, is backfilled.
    /// </summary>
    /// <param name="lateSeconds">The seconds the capture time lies before the receive time; negative when after</param>
    [TestMethod]
    [DataRow(91.0)]
    [DataRow(90.001)]
    [DataRow(-91.0)]
    [DataRow(-90.001)]
    public void LiveRulesMetricNinetyOneSecondsLateIsBackfilled(double lateSeconds)
    {
        // Arrange
        var record = RecordOf(RecordKind.Metric, 10, Samples.Base - TimeSpan.FromSeconds(lateSeconds));

        // Act
        var actual = LiveRules.IsLive(record, Samples.Base, 0, null);

        // Assert
        Assert.IsFalse(actual, $"lateness {lateSeconds} s is beyond the allowance of 90 s");
    }

    /// <summary>
    /// A log line is live up to 10 minutes late or early.
    /// </summary>
    /// <param name="lateMinutes">The minutes the capture time lies before the receive time; negative when after</param>
    [TestMethod]
    [DataRow(9.0)]
    [DataRow(10.0)]
    [DataRow(-10.0)]
    public void LiveRulesLogLineNineMinutesLateIsLive(double lateMinutes)
    {
        // Arrange
        var record = RecordOf(RecordKind.LogLine, 10, Samples.Base - TimeSpan.FromMinutes(lateMinutes));

        // Act
        var actual = LiveRules.IsLive(record, Samples.Base, 0, null);

        // Assert
        Assert.IsTrue(actual, $"lateness {lateMinutes} min is within the allowance of 10 min");
    }

    /// <summary>
    /// A kernel event or log line more than 10 minutes late is backfilled.
    /// </summary>
    /// <param name="kind">The record kind</param>
    /// <param name="lateMinutes">The minutes the capture time lies before the receive time</param>
    [TestMethod]
    [DataRow(RecordKind.KernelEvent, 11.0)]
    [DataRow(RecordKind.KernelEvent, 10.02)]
    [DataRow(RecordKind.LogLine, 11.0)]
    public void LiveRulesKernelEventElevenMinutesLateIsBackfilled(string kind, double lateMinutes)
    {
        // Arrange
        var record = RecordOf(kind, 10, Samples.Base - TimeSpan.FromMinutes(lateMinutes));

        // Act
        var actual = LiveRules.IsLive(record, Samples.Base, 0, null);

        // Assert
        Assert.IsFalse(actual, $"{kind} {lateMinutes} min late is beyond the allowance of 10 min");
    }

    /// <summary>
    /// A record captured after its receipt by more than the allowance of its kind is backfilled.
    /// </summary>
    /// <param name="kind">The record kind</param>
    /// <param name="earlySeconds">The seconds the capture time lies after the receive time</param>
    [TestMethod]
    [DataRow(RecordKind.Metric, 91.0)]
    [DataRow(RecordKind.Metric, 7200.0)]
    [DataRow(RecordKind.LogLine, 601.0)]
    [DataRow(RecordKind.KernelEvent, 601.0)]
    public void LiveRulesEarlyBeyondAllowanceIsBackfilled(string kind, double earlySeconds)
    {
        // Arrange
        var record = RecordOf(kind, 10, Samples.Base + TimeSpan.FromSeconds(earlySeconds));

        // Act
        var actual = LiveRules.IsLive(record, Samples.Base, 0, null);

        // Assert
        Assert.IsFalse(actual, $"{kind} captured {earlySeconds} s after its receipt");
    }

    /// <summary>
    /// An open sequence gap that starts below the record's number makes a fresh record backfilled, one above it does not.
    /// </summary>
    [TestMethod]
    public void LiveRulesOpenGapBelowIsBackfilled()
    {
        // Arrange
        var record = RecordOf(RecordKind.Metric, 100, Samples.Base);

        // Act
        var gapFarBelow = LiveRules.IsLive(record, Samples.Base, 0, 50);
        var gapJustBelow = LiveRules.IsLive(record, Samples.Base, 0, 99);
        var gapAbove = LiveRules.IsLive(record, Samples.Base, 0, 101);
        var noGap = LiveRules.IsLive(record, Samples.Base, 0, null);

        // Assert
        Assert.IsFalse(gapFarBelow, "an open gap starting at 50 lies below number 100");
        Assert.IsFalse(gapJustBelow, "an open gap starting at 99 lies below number 100");
        Assert.IsTrue(gapAbove, "an open gap starting at 101 lies above number 100");
        Assert.IsTrue(noGap, "no open gap");
    }

    /// <summary>
    /// A gap number at the top of the number range makes no difference to a record whose number lies below it.
    /// </summary>
    [TestMethod]
    public void LiveRulesGapAtMaximumNumberLeavesLowerRecordLive()
    {
        // Arrange
        var record = RecordOf(RecordKind.Metric, 100, Samples.Base);

        // Act
        var below = LiveRules.IsLive(record, Samples.Base, 0, ulong.MaxValue);
        var above = LiveRules.IsLive(RecordOf(RecordKind.Metric, ulong.MaxValue, Samples.Base), Samples.Base, 0, 100);

        // Assert
        Assert.IsTrue(below, "the gap starts above the record");
        Assert.IsFalse(above, "the gap starts below the record's number");
    }

    /// <summary>
    /// The measured offset corrects the capture time before the lateness is taken.
    /// </summary>
    /// <param name="offsetSeconds">The agent's clock minus the backend's clock in seconds</param>
    [TestMethod]
    [DataRow(7200L)]
    [DataRow(-7200L)]
    public void LiveRulesOffsetCorrectsCaptureTime(long offsetSeconds)
    {
        // Arrange
        var record = RecordOf(RecordKind.Metric, 10, Samples.Base.AddSeconds(offsetSeconds - 3));
        var oldRecord = RecordOf(RecordKind.Metric, 11, Samples.Base.AddSeconds(offsetSeconds - 600));

        // Act
        var corrected = LiveRules.IsLive(record, Samples.Base, offsetSeconds * NanosecondsPerSecond, null);
        var uncorrected = LiveRules.IsLive(record, Samples.Base, 0, null);
        var old = LiveRules.IsLive(oldRecord, Samples.Base, offsetSeconds * NanosecondsPerSecond, null);

        // Assert
        Assert.IsTrue(corrected, $"3 s late after the correction of {offsetSeconds} s");
        Assert.IsFalse(uncorrected, $"without the correction the record is {offsetSeconds} s off");
        Assert.IsFalse(old, "10 minutes late after the correction stays beyond the allowance");
    }

    /// <summary>
    /// Extreme capture times, receive times and offsets never throw, and only records near their receipt are live.
    /// </summary>
    [TestMethod]
    public void LiveRulesExtremeTimesDoNotOverflow()
    {
        // Arrange
        var min = StorableTime.Min;
        var max = StorableTime.Max;
        var cases = new (DateTimeOffset Captured, DateTimeOffset Received, long Offset, bool Expected)[]
                    {
                        (max, min, 0, false),
                        (min, max, 0, false),
                        (max, min, long.MaxValue, false),
                        (min, max, long.MinValue, false),
                        (max, max, long.MinValue, false),
                        (min, min, long.MaxValue, false),
                        (max, max, long.MaxValue, false),
                        (min, min, long.MinValue, false),
                        (max, max, 0, true),
                        (min, min, 0, true)
                    };

        foreach (var (captured, received, offset, expected) in cases)
        {
            // Act
            var actual = LiveRules.IsLive(RecordOf(RecordKind.Metric, ulong.MaxValue, captured), received, offset, null);

            // Assert
            Assert.AreEqual(expected, actual, $"captured {captured.UtcTicks} received {received.UtcTicks} offset {offset}");
        }
    }

    /// <summary>
    /// The allowance is 10 minutes for log lines and kernel events and 90 seconds for every other kind.
    /// </summary>
    /// <param name="kind">The record kind</param>
    /// <param name="seconds">The expected allowance in seconds</param>
    [TestMethod]
    [DataRow(RecordKind.LogLine, 600)]
    [DataRow(RecordKind.KernelEvent, 600)]
    [DataRow(RecordKind.Metric, 90)]
    [DataRow(RecordKind.ProcessSnapshot, 90)]
    [DataRow(RecordKind.ConnectionSnapshot, 90)]
    [DataRow(RecordKind.ServiceState, 90)]
    [DataRow(RecordKind.MariaDbStatus, 90)]
    [DataRow(RecordKind.Gap, 90)]
    [DataRow(RecordKind.BackupRun, 90)]
    public void LiveRulesAllowanceOfKindIsFixed(string kind, int seconds)
    {
        // Act
        var actual = LiveRules.AllowanceOf(kind);

        // Assert
        Assert.AreEqual(TimeSpan.FromSeconds(seconds), actual, kind);
    }

    /// <summary>
    /// Every kind except log lines and kernel events is timed by the agent's run.
    /// </summary>
    /// <param name="kind">The record kind</param>
    /// <param name="expected">Whether the kind is run-timed</param>
    [TestMethod]
    [DataRow(RecordKind.LogLine, false)]
    [DataRow(RecordKind.KernelEvent, false)]
    [DataRow(RecordKind.Metric, true)]
    [DataRow(RecordKind.ProcessSnapshot, true)]
    [DataRow(RecordKind.ConnectionSnapshot, true)]
    [DataRow(RecordKind.ServiceState, true)]
    [DataRow(RecordKind.MariaDbStatus, true)]
    [DataRow(RecordKind.Gap, true)]
    [DataRow(RecordKind.BackupRun, true)]
    public void LiveRulesIsRunTimedExcludesLogBasedKinds(string kind, bool expected)
    {
        // Act
        var actual = LiveRules.IsRunTimed(kind);

        // Assert
        Assert.AreEqual(expected, actual, kind);
    }

    /// <summary>
    /// Creates an agent record of a kind.
    /// </summary>
    /// <param name="kind">The record kind; metric, log line or kernel event</param>
    /// <param name="seq">The sequence number</param>
    /// <param name="capturedAt">The capture time</param>
    /// <returns>The record</returns>
    private static DataRecord RecordOf(string kind, ulong seq, DateTimeOffset capturedAt)
    {
        var record = kind switch
                     {
                         RecordKind.LogLine => Samples.Log(RecordOrigin.Agent, seq, 0, "line"),
                         RecordKind.KernelEvent => new DataRecord
                                                   {
                                                       Origin = RecordOrigin.Agent,
                                                       Source = "kernel",
                                                       Seq = seq,
                                                       Data = new KernelEvent
                                                              {
                                                                  Type = KernelEvent.TypeBoot
                                                              }
                                                   },
                         _ => Samples.Metric(RecordOrigin.Agent, seq, 0, "cpu")
                     };

        record.CapturedAt = capturedAt;

        return record;
    }

    #endregion // Methods
}