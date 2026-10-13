using Vandox.Core.Incidents;
using Vandox.Core.Reports;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="SnapshotSeries"/>
/// </summary>
[TestClass]
public class SnapshotSeriesTests
{
    #region Constants

    private const ulong Mib = 1048576;

    private static readonly DateTimeOffset _start = new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);

    #endregion // Constants

    #region Methods

    /// <summary>
    /// The panel threshold is the nearest-rank 90th percentile of the panel memory of the snapshots.
    /// </summary>
    [TestMethod]
    public void SnapshotSeriesPanelThresholdIsNearestRankPercentile()
    {
        // Arrange
        var ten = new SnapshotSeries();
        var one = new SnapshotSeries();

        for (var index = 10; index >= 1; index--)
        {
            ten.Add(_start.AddMinutes(10 - index), Usage(("sw-engine", (ulong)index * 10 * Mib), ("mariadbd", 5 * Mib)));
        }

        one.Add(_start, Usage(("sw-engi", 7 * Mib)));

        // Act
        var tenThreshold = ten.PanelThresholdBytes();
        var oneThreshold = one.PanelThresholdBytes();

        // Assert
        Assert.AreEqual(90 * Mib, tenThreshold, "ten snapshots of 10 to 100 MiB: the ninth value");
        Assert.AreEqual(7 * Mib, oneThreshold, "one snapshot: its value");
    }

    /// <summary>
    /// A snapshot earlier than the previous one is refused, an equal time is accepted.
    /// </summary>
    [TestMethod]
    public void SnapshotSeriesRefusesATimeEarlierThanThePreviousOne()
    {
        // Arrange
        var series = new SnapshotSeries();

        series.Add(_start.AddMinutes(5), Usage(("sw-engine", Mib)));
        series.Add(_start.AddMinutes(5), Usage(("sw-engine", Mib)));

        // Act
        var exception = Assert.ThrowsExactly<ArgumentException>(() => series.Add(_start.AddMinutes(4), Usage(("sw-engine", Mib))), "an earlier time");

        // Assert
        Assert.StartsWith("snapshot series: a time is earlier than the previous one", exception.Message, "message");
        Assert.AreEqual(2, series.Count, "the refused snapshot is not kept");
    }

    /// <summary>
    /// Without any panel memory the threshold is zero, and with a zero threshold the panel is unknown.
    /// </summary>
    [TestMethod]
    public void SnapshotSeriesPanelThresholdIsZeroWithoutPanel()
    {
        // Arrange
        var empty = new SnapshotSeries();
        var withoutPanel = new SnapshotSeries();

        withoutPanel.Add(_start, Usage(("mariadbd", 100 * Mib)));
        withoutPanel.Add(_start.AddMinutes(1), Usage(("pzstd", 100 * Mib)));

        // Act
        var emptyThreshold = empty.PanelThresholdBytes();
        var threshold = withoutPanel.PanelThresholdBytes();
        var high = withoutPanel.PanelHigh(_start, _start.AddHours(1), threshold);

        // Assert
        Assert.AreEqual(0UL, emptyThreshold, "no snapshot");
        Assert.AreEqual(0UL, threshold, "snapshots without a panel program");
        Assert.AreEqual(Indicator.Unknown, high, "a zero threshold makes the panel unknown");
    }

    /// <summary>
    /// The series keeps at most its capacity and says so when more points arrive.
    /// </summary>
    [TestMethod]
    public void SnapshotSeriesTruncatesAtCapacity()
    {
        // Arrange
        var series = new SnapshotSeries(3);

        // Act
        for (var index = 0; index < 3; index++)
        {
            series.Add(_start.AddMinutes(index), Usage(("sw-engine", Mib)));
        }

        var atCapacity = series.Truncated;

        series.Add(_start.AddMinutes(3), Usage(("sw-engine", Mib)));
        series.Add(_start.AddMinutes(4), Usage(("sw-engine", Mib)));

        // Assert
        Assert.IsFalse(atCapacity, "a series at its capacity is not truncated");
        Assert.AreEqual(3, series.Count, "no more than the capacity is kept");
        Assert.IsTrue(series.Truncated, "points were dropped");
    }

    /// <summary>
    /// The backup indicator is unknown without a snapshot in the period, yes with a backup program in one and no otherwise; the end is exclusive.
    /// </summary>
    [TestMethod]
    public void SnapshotSeriesJudgesBackupInThePeriod()
    {
        // Arrange
        var series = new SnapshotSeries();

        series.Add(_start.AddMinutes(10), Usage(("mariadbd", Mib)));
        series.Add(_start.AddMinutes(20), Usage(("mariadbd", Mib), ("pzstd", Mib)));
        series.Add(_start.AddMinutes(40), Usage(("mariadbd", Mib)));

        // Act and Assert
        Assert.AreEqual(Indicator.Unknown, series.Backup(_start.AddMinutes(41), _start.AddMinutes(60)), "no snapshot in the period");
        Assert.AreEqual(Indicator.Yes, series.Backup(_start.AddMinutes(15), _start.AddMinutes(25)), "a snapshot with pzstd");
        Assert.AreEqual(Indicator.No, series.Backup(_start.AddMinutes(30), _start.AddMinutes(45)), "snapshots without pzstd");
        Assert.AreEqual(Indicator.Yes, series.Backup(_start.AddMinutes(20), _start.AddMinutes(21)), "the start is inclusive");
        Assert.AreEqual(Indicator.Unknown, series.Backup(_start.AddMinutes(21), _start.AddMinutes(40)), "the end is exclusive");
    }

    /// <summary>
    /// The panel indicator is yes at or above the threshold, no below it and unknown without a snapshot or threshold.
    /// </summary>
    [TestMethod]
    public void SnapshotSeriesJudgesPanelMemoryInThePeriod()
    {
        // Arrange
        var series = new SnapshotSeries();

        series.Add(_start.AddMinutes(10), Usage(("sw-engine", 50 * Mib)));
        series.Add(_start.AddMinutes(20), Usage(("sw-engine", 60 * Mib), ("sw-engine-fpm", 40 * Mib)));
        series.Add(_start.AddMinutes(40), Usage(("mariadbd", Mib)));

        // Act and Assert
        Assert.AreEqual(Indicator.Yes, series.PanelHigh(_start.AddMinutes(15), _start.AddMinutes(25), 100 * Mib), "the sum of 100 MiB reaches the threshold");
        Assert.AreEqual(Indicator.No, series.PanelHigh(_start.AddMinutes(5), _start.AddMinutes(15), 100 * Mib), "50 MiB is below the threshold");
        Assert.AreEqual(Indicator.No, series.PanelHigh(_start.AddMinutes(35), _start.AddMinutes(45), 100 * Mib), "a snapshot without panel programs");
        Assert.AreEqual(Indicator.Unknown, series.PanelHigh(_start.AddMinutes(45), _start.AddMinutes(55), 100 * Mib), "no snapshot in the period");
        Assert.AreEqual(Indicator.Unknown, series.PanelHigh(_start.AddMinutes(15), _start.AddMinutes(25), 0), "no threshold");
    }

    /// <summary>
    /// Builds the programs of a snapshot by name.
    /// </summary>
    /// <param name="programs">The programs with their resident memory in bytes</param>
    /// <returns>The programs</returns>
    private static Dictionary<string, ProgramUsage> Usage(params (string Program, ulong Rss)[] programs)
    {
        return programs.ToDictionary(entry => entry.Program,
                                     entry => new ProgramUsage
                                              {
                                                  Program = entry.Program,
                                                  RssBytes = entry.Rss,
                                                  Count = 1
                                              },
                                     StringComparer.Ordinal);
    }

    #endregion // Methods
}