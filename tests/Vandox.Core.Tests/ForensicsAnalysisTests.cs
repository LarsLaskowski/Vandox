using Vandox.Core.Incidents;
using Vandox.Core.Model;
using Vandox.Core.Reports;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="ForensicsAnalysis"/>
/// </summary>
[TestClass]
public class ForensicsAnalysisTests
{
    #region Constants

    private const ulong Mib = 1048576;

    private static readonly DateTimeOffset _base = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset _now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan _hour = TimeSpan.FromHours(1);

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Only lines of the sources journal and syslog make an hour observed; every line is counted.
    /// </summary>
    [TestMethod]
    public void ForensicsAnalysisObservesOnlySystemLogSources()
    {
        // Arrange
        var analysis = new ForensicsAnalysis(_hour);

        // Act
        analysis.AddLogLine(_base.AddMinutes(10), "journal");
        analysis.AddLogLine(_base.AddHours(1).AddMinutes(10), "syslog");
        analysis.AddLogLine(_base.AddHours(2).AddMinutes(10), "nginx");
        analysis.AddLogLine(_base.AddHours(3).AddMinutes(10), "mariadb");
        analysis.Seal();

        var summary = analysis.Summarize(_now);

        // Assert
        Assert.AreSequenceEqual(["journal", "syslog"], ForensicsAnalysis.SystemLogSources, "the sources of the system log");
        Assert.AreEqual(2, summary.ObservedHours, "only the hours of the journal and the syslog line");
        Assert.AreEqual(4, summary.LogLines, "every line is counted");
        Assert.AreEqual(_base.AddMinutes(10), summary.FirstLogLineAt, "first line");
        Assert.AreEqual(_base.AddHours(3).AddMinutes(10), summary.LastLogLineAt, "last line");
        Assert.AreEqual(_now, summary.GeneratedAt, "generation time");
        Assert.AreEqual(_hour, summary.Lookback, "lookback");
    }

    /// <summary>
    /// Three outages in the same hour of the day over 72 observed hours are detected for that hour only.
    /// </summary>
    [TestMethod]
    public void ForensicsAnalysisDetectsTimeOfDayCluster()
    {
        // Arrange
        var analysis = Prepared(new ForensicsAnalysis(_hour), _ => null, []);
        var times = new[] { _base.AddHours(2).AddMinutes(10), _base.AddDays(1).AddHours(2).AddMinutes(20), _base.AddDays(2).AddHours(2).AddMinutes(40) };

        // Act
        var summary = Summarized(analysis, times, 2, 1, 4);

        // Assert
        Assert.HasCount(24, summary.TimeOfDay, "one result per hour of the day");
        Assert.AreEqual(PatternOutcome.Detected, summary.TimeOfDay[2].Outcome, "hour 2");
        Assert.AreEqual(3, summary.TimeOfDay[2].Matching, "outages in hour 2");
        Assert.AreEqual(3, summary.TimeOfDay[2].Known, "all outages are known");
        Assert.AreEqual(3, summary.TimeOfDay[2].BaseMatching, "observed hours that are hour 2");
        Assert.AreEqual(72, summary.TimeOfDay[2].BaseKnown, "observed hours");

        for (var hour = 0; hour < 24; hour++)
        {
            if (hour != 2)
            {
                Assert.AreEqual(PatternOutcome.NotDetected, summary.TimeOfDay[hour].Outcome, $"hour {hour}");
            }
        }

        Assert.AreEqual(3, summary.Incidents, "outages");
        Assert.AreEqual(7, summary.Kills, "kills");
        Assert.AreEqual(times[0], summary.FirstOutageAt, "first outage");
        Assert.AreEqual(times[2], summary.LastOutageAt, "last outage");
        Assert.AreEqual(72, summary.ObservedHours, "observed hours");
        Assert.AreEqual(72, summary.LogLines, "log lines");
    }

    /// <summary>
    /// Three outages in three different hours of the day are decided as not detected.
    /// </summary>
    [TestMethod]
    public void ForensicsAnalysisDoesNotDetectSpreadOutages()
    {
        // Arrange
        var analysis = Prepared(new ForensicsAnalysis(_hour), _ => null, []);
        var times = new[] { _base.AddHours(2).AddMinutes(10), _base.AddDays(1).AddHours(9).AddMinutes(20), _base.AddDays(2).AddHours(17).AddMinutes(40) };

        // Act
        var summary = Summarized(analysis, times, 1, 1, 1);

        // Assert
        Assert.HasCount(24, summary.TimeOfDay, "one result per hour of the day");
        Assert.IsTrue(summary.TimeOfDay.All(result => result.Outcome == PatternOutcome.NotDetected), "every hour is decided and not detected");
    }

    /// <summary>
    /// Two outages are too few to decide the time of day.
    /// </summary>
    [TestMethod]
    public void ForensicsAnalysisTimeOfDayIsUnknownWithTwoOutages()
    {
        // Arrange
        var analysis = Prepared(new ForensicsAnalysis(_hour), _ => null, []);
        var times = new[] { _base.AddHours(2).AddMinutes(10), _base.AddDays(1).AddHours(2).AddMinutes(20) };

        // Act
        var summary = Summarized(analysis, times, 1, 1);

        // Assert
        Assert.HasCount(24, summary.TimeOfDay, "one result per hour of the day");
        Assert.IsTrue(summary.TimeOfDay.All(result => result.Outcome == PatternOutcome.Unknown), "every hour is unknown");
        Assert.IsTrue(summary.TimeOfDay.All(result => result.Reason == UnknownReason.TooFewIncidents), "because of too few incidents");
    }

    /// <summary>
    /// The hour of the day is the UTC hour of the first kill, whatever offset its time carries.
    /// </summary>
    [TestMethod]
    public void ForensicsAnalysisBinsTimeOfDayInUtc()
    {
        // Arrange
        var analysis = Prepared(new ForensicsAnalysis(_hour), _ => null, []);
        var offset = TimeSpan.FromHours(2);
        var times = new[] { new DateTimeOffset(2026, 9, 1, 4, 10, 0, offset), new DateTimeOffset(2026, 9, 2, 4, 20, 0, offset), new DateTimeOffset(2026, 9, 3, 4, 40, 0, offset) };

        // Act
        var summary = Summarized(analysis, times, 1, 1, 1);

        // Assert
        Assert.AreEqual(PatternOutcome.Detected, summary.TimeOfDay[2].Outcome, "04:xx at +02:00 is hour 2 UTC");
        Assert.AreEqual(PatternOutcome.NotDetected, summary.TimeOfDay[4].Outcome, "not hour 4");
    }

    /// <summary>
    /// The backup indicator of an outage is unknown without a snapshot in its lookback, yes with a backup program and no without.
    /// </summary>
    [TestMethod]
    public void ForensicsAnalysisJudgesBackupInTheLookback()
    {
        // Arrange
        var analysis = new ForensicsAnalysis(TimeSpan.FromMinutes(5));
        var yes = _base.AddHours(1).AddMinutes(10);
        var no = _base.AddHours(2).AddMinutes(10);
        var unknown = _base.AddHours(3).AddMinutes(10);

        analysis.AddProcessSnapshot(yes.AddMinutes(-3), Snapshot(("web", Mib), ("pzstd", Mib)));
        analysis.AddProcessSnapshot(no.AddMinutes(-3), Snapshot(("web", Mib)));
        analysis.Seal();

        // Act
        var withBackup = analysis.Activities(Outage(yes));
        var withoutBackup = analysis.Activities(Outage(no));
        var withoutSnapshot = analysis.Activities(Outage(unknown));

        // Assert
        Assert.AreEqual(Indicator.Yes, withBackup.Backup, "a snapshot with pzstd in the lookback");
        Assert.AreEqual(Indicator.No, withoutBackup.Backup, "a snapshot without pzstd");
        Assert.AreEqual(Indicator.Unknown, withoutSnapshot.Backup, "no snapshot in the lookback");
    }

    /// <summary>
    /// The boot indicator is unknown when an hour of the lookback is not observed, yes with the time of the last boot, and no otherwise.
    /// </summary>
    [TestMethod]
    public void ForensicsAnalysisJudgesBootInTheLookback()
    {
        // Arrange
        var analysis = new ForensicsAnalysis(TimeSpan.FromMinutes(5));
        var yes = _base.AddHours(1).AddMinutes(10);
        var no = _base.AddHours(2).AddMinutes(10);
        var unobserved = _base.AddHours(7).AddMinutes(10);
        var straddling = _base.AddHours(6).AddMinutes(2);

        for (var hour = 0; hour <= 5; hour++)
        {
            analysis.AddLogLine(_base.AddHours(hour).AddMinutes(30), "syslog");
        }

        analysis.AddBoot(yes.AddMinutes(-4));
        analysis.AddBoot(yes.AddMinutes(-2));
        analysis.Seal();

        // Act
        var withBoot = analysis.Activities(Outage(yes));
        var withoutBoot = analysis.Activities(Outage(no));
        var inUnobservedHour = analysis.Activities(Outage(unobserved));
        var overUnobservedHour = analysis.Activities(Outage(straddling));

        // Assert
        Assert.AreEqual(Indicator.Yes, withBoot.Boot, "boots in the lookback");
        Assert.AreEqual(yes.AddMinutes(-2), withBoot.LastBootAt, "the time of the last boot");
        Assert.AreEqual(Indicator.No, withoutBoot.Boot, "an observed lookback without a boot");
        Assert.IsNull(withoutBoot.LastBootAt, "no boot time");
        Assert.AreEqual(Indicator.Unknown, inUnobservedHour.Boot, "the hour is not observed");
        Assert.AreEqual(Indicator.Unknown, overUnobservedHour.Boot, "the lookback reaches into an hour that is not observed");
    }

    /// <summary>
    /// The panel indicator is yes at or above the threshold, no below it and unknown without a snapshot or threshold.
    /// </summary>
    [TestMethod]
    public void ForensicsAnalysisJudgesPanelMemoryInTheLookback()
    {
        // Arrange
        var analysis = new ForensicsAnalysis(TimeSpan.FromMinutes(5));
        var withoutPanel = new ForensicsAnalysis(TimeSpan.FromMinutes(5));

        for (var rank = 1; rank <= 10; rank++)
        {
            analysis.AddProcessSnapshot(_base.AddMinutes(10 * rank), Snapshot(("sw-engine", (ulong)rank * 10 * Mib), ("mariadbd", Mib)));
        }

        withoutPanel.AddProcessSnapshot(_base.AddMinutes(10), Snapshot(("mariadbd", Mib)));
        analysis.Seal();
        withoutPanel.Seal();

        // Act
        var high = analysis.Activities(Outage(_base.AddMinutes(92)));
        var low = analysis.Activities(Outage(_base.AddMinutes(82)));
        var none = analysis.Activities(Outage(_base.AddMinutes(107)));
        var noThreshold = withoutPanel.Activities(Outage(_base.AddMinutes(12)));

        // Assert
        Assert.AreEqual(Indicator.Yes, high.PanelHigh, "90 MiB reaches the threshold of 90 MiB");
        Assert.AreEqual(Indicator.No, low.PanelHigh, "80 MiB is below it");
        Assert.AreEqual(Indicator.Unknown, none.PanelHigh, "no snapshot in the lookback");
        Assert.AreEqual(Indicator.Unknown, noThreshold.PanelHigh, "no panel memory, no threshold");
    }

    /// <summary>
    /// Outages that are each preceded by a backup that runs in few observed hours are detected.
    /// </summary>
    [TestMethod]
    public void ForensicsAnalysisDetectsBackupCorrelation()
    {
        // Arrange
        var analysis = Prepared(new ForensicsAnalysis(_hour), hour => hour % 24 == 2 ? Snapshot(("web", Mib), ("pzstd", Mib)) : Snapshot(("web", Mib)), []);

        // Act
        var summary = Summarized(analysis, DailyAt(2, 50), 1, 1, 1);

        // Assert
        Assert.AreEqual(PatternOutcome.Detected, summary.Backup.Outcome, "backup");
        Assert.AreEqual(3, summary.Backup.Matching, "outages after a backup");
        Assert.AreEqual(3, summary.Backup.Known, "known outages");
        Assert.AreEqual(3, summary.Backup.BaseMatching, "observed hours with a backup");
        Assert.AreEqual(72, summary.Backup.BaseKnown, "observed hours with a snapshot");
    }

    /// <summary>
    /// Outages without a snapshot count as unknown and never as no.
    /// </summary>
    [TestMethod]
    public void ForensicsAnalysisCountsMissingSnapshotsAsUnknown()
    {
        // Arrange
        var none = Prepared(new ForensicsAnalysis(_hour), _ => null, []);
        var some = Prepared(new ForensicsAnalysis(_hour), hour => hour is 2 or 26 ? Snapshot(("web", Mib)) : null, []);

        // Act
        var withoutSnapshots = Summarized(none, DailyAt(2, 50), 1, 1, 1);
        var twoOfThree = Summarized(some, DailyAt(2, 50), 1, 1, 1);

        // Assert
        Assert.AreEqual(PatternOutcome.Unknown, withoutSnapshots.Backup.Outcome, "no snapshot at all");
        Assert.AreEqual(UnknownReason.TooFewIncidents, withoutSnapshots.Backup.Reason, "no outage is known");
        Assert.AreEqual(0, withoutSnapshots.Backup.Known, "none known");
        Assert.AreEqual(PatternOutcome.Unknown, twoOfThree.Backup.Outcome, "two known outages are too few");
        Assert.AreEqual(UnknownReason.TooFewIncidents, twoOfThree.Backup.Reason, "reason");
        Assert.AreEqual(2, twoOfThree.Backup.Known, "the outage without a snapshot is unknown, not no");
        Assert.AreEqual(0, twoOfThree.Backup.Matching, "no backup");
    }

    /// <summary>
    /// Outages that are each preceded by a boot that happens in few observed hours are detected.
    /// </summary>
    [TestMethod]
    public void ForensicsAnalysisDetectsBootCorrelation()
    {
        // Arrange
        var analysis = Prepared(new ForensicsAnalysis(_hour), _ => null, DailyAt(2, 10));

        // Act
        var summary = Summarized(analysis, DailyAt(2, 50), 1, 1, 1);

        // Assert
        Assert.AreEqual(PatternOutcome.Detected, summary.Boot.Outcome, "boot");
        Assert.AreEqual(3, summary.Boot.Matching, "outages after a boot");
        Assert.AreEqual(3, summary.Boot.Known, "known outages");
        Assert.AreEqual(3, summary.Boot.BaseMatching, "observed hours with a boot");
        Assert.AreEqual(72, summary.Boot.BaseKnown, "observed hours");
    }

    /// <summary>
    /// Outages that are each preceded by high panel memory that occurs in few observed hours are detected.
    /// </summary>
    [TestMethod]
    public void ForensicsAnalysisDetectsPanelCorrelation()
    {
        // Arrange
        var analysis = Prepared(new ForensicsAnalysis(_hour), hour => Snapshot(("sw-engine", hour % 24 == 2 ? 1000 * Mib : (ulong)(hour + 1) * Mib)), []);

        // Act
        var summary = Summarized(analysis, DailyAt(2, 50), 1, 1, 1);

        // Assert
        Assert.AreEqual(68 * Mib, summary.PanelThresholdBytes, "the 65th of 72 values");
        Assert.AreEqual(PatternOutcome.Detected, summary.PanelHigh.Outcome, "panel memory");
        Assert.AreEqual(3, summary.PanelHigh.Matching, "outages after high panel memory");
        Assert.AreEqual(3, summary.PanelHigh.Known, "known outages");
        Assert.AreEqual(72, summary.PanelHigh.BaseKnown, "observed hours with a snapshot");
    }

    /// <summary>
    /// A cap that is hit makes the statements that need the data unknown, with the reason, and the time of day is still decided.
    /// </summary>
    [TestMethod]
    public void ForensicsAnalysisMarksActivitiesUnknownOverCaps()
    {
        // Arrange
        var boots = new ForensicsAnalysis(_hour, ReportLimits.MaxSnapshots, 2, ReportLimits.MaxObservedRuns, ReportLimits.MaxPrograms);
        var snapshots = new ForensicsAnalysis(_hour, 2, ReportLimits.MaxBoots, ReportLimits.MaxObservedRuns, ReportLimits.MaxPrograms);
        var runs = new ForensicsAnalysis(_hour, ReportLimits.MaxSnapshots, ReportLimits.MaxBoots, 2, ReportLimits.MaxPrograms);

        Prepared(boots, _ => null, DailyAt(2, 10));
        Prepared(snapshots, hour => Snapshot(("sw-engine", (ulong)(hour + 1) * Mib)), []);

        foreach (var hour in new[] { 0, 2, 4 })
        {
            runs.AddLogLine(_base.AddHours(hour).AddMinutes(30), "syslog");
            runs.AddProcessSnapshot(_base.AddHours(hour).AddMinutes(20), Snapshot(("sw-engine", (ulong)(hour + 1) * Mib), ("pzstd", Mib)));
        }

        runs.AddBoot(_base.AddHours(2).AddMinutes(10));
        runs.Seal();

        // Act
        var bootSummary = Summarized(boots, DailyAt(2, 50), 1, 1, 1);
        var snapshotSummary = Summarized(snapshots, DailyAt(2, 50), 1, 1, 1);
        var runSummary = Summarized(runs, DailyAt(2, 50), 1, 1, 1);

        // Assert
        Assert.IsTrue(bootSummary.BootsTruncated, "more boots than the cap");
        Assert.AreEqual(PatternOutcome.Unknown, bootSummary.Boot.Outcome, "boot rule over the boot cap");
        Assert.AreEqual(UnknownReason.TooManyBoots, bootSummary.Boot.Reason, "reason for the boots");
        Assert.IsTrue(snapshotSummary.SnapshotsTruncated, "more snapshots than the cap");
        Assert.AreEqual(UnknownReason.TooManySnapshots, snapshotSummary.Backup.Reason, "backup over the snapshot cap");
        Assert.AreEqual(UnknownReason.TooManySnapshots, snapshotSummary.PanelHigh.Reason, "panel over the snapshot cap");
        Assert.AreEqual(PatternOutcome.Unknown, snapshotSummary.Backup.Outcome, "backup is unknown");
        Assert.IsTrue(runSummary.ObservedHoursTruncated, "more runs than the cap");
        Assert.AreEqual(UnknownReason.TooManyObservedRuns, runSummary.Backup.Reason, "backup over the run cap");
        Assert.AreEqual(UnknownReason.TooManyObservedRuns, runSummary.Boot.Reason, "boot over the run cap");
        Assert.AreEqual(UnknownReason.TooManyObservedRuns, runSummary.PanelHigh.Reason, "panel over the run cap");
        Assert.IsTrue(runSummary.TimeOfDay.All(result => result.Outcome != PatternOutcome.Unknown), "the time of day is still decided");
    }

    /// <summary>
    /// Lines, boots, snapshots and a second seal after the seal, and incidents, activities and the summary before it, are refused.
    /// </summary>
    [TestMethod]
    public void ForensicsAnalysisRefusesCallsOutOfOrder()
    {
        // Arrange
        var open = new ForensicsAnalysis(_hour);
        var sealedAnalysis = new ForensicsAnalysis(_hour);

        sealedAnalysis.Seal();

        // Act
        var addIncident = Assert.ThrowsExactly<InvalidOperationException>(() => open.AddIncident(Outage(_base, 1)), "an incident before the seal");
        var activities = Assert.ThrowsExactly<InvalidOperationException>(() => open.Activities(Outage(_base, 1)), "activities before the seal");
        var summarize = Assert.ThrowsExactly<InvalidOperationException>(() => open.Summarize(_now), "the summary before the seal");
        var line = Assert.ThrowsExactly<InvalidOperationException>(() => sealedAnalysis.AddLogLine(_base, "syslog"), "a line after the seal");
        var boot = Assert.ThrowsExactly<InvalidOperationException>(() => sealedAnalysis.AddBoot(_base), "a boot after the seal");
        var snapshot = Assert.ThrowsExactly<InvalidOperationException>(() => sealedAnalysis.AddProcessSnapshot(_base, Snapshot(("mariadbd", Mib))), "a snapshot after the seal");
        var seal = Assert.ThrowsExactly<InvalidOperationException>(() => sealedAnalysis.Seal(), "a second seal");

        // Assert
        Assert.AreEqual("forensics analysis: not sealed yet", addIncident.Message, "message of an incident before the seal");
        Assert.AreEqual("forensics analysis: not sealed yet", activities.Message, "message of activities before the seal");
        Assert.AreEqual("forensics analysis: not sealed yet", summarize.Message, "message of the summary before the seal");
        Assert.AreEqual("forensics analysis: already sealed", line.Message, "message of a line after the seal");
        Assert.AreEqual("forensics analysis: already sealed", boot.Message, "message of a boot after the seal");
        Assert.AreEqual("forensics analysis: already sealed", snapshot.Message, "message of a snapshot after the seal");
        Assert.AreEqual("forensics analysis: already sealed", seal.Message, "message of a second seal");
    }

    /// <summary>
    /// Over the snapshot and boot caps a no becomes unknown, since the dropped data may hold the activity, and a yes stays a yes.
    /// </summary>
    [TestMethod]
    public void ForensicsAnalysisTurnsANoIntoUnknownOverTheCaps()
    {
        // Arrange
        var capped = CappedByData(new ForensicsAnalysis(_hour, 2, 1, ReportLimits.MaxObservedRuns, ReportLimits.MaxPrograms));
        var uncapped = CappedByData(new ForensicsAnalysis(_hour));
        var quiet = Outage(_base.AddHours(10).AddMinutes(40));
        var busy = Outage(_base.AddDays(1).AddHours(10).AddMinutes(40));

        // Act
        var cappedQuiet = capped.Activities(quiet);
        var cappedBusy = capped.Activities(busy);
        var uncappedQuiet = uncapped.Activities(quiet);

        // Assert
        Assert.AreEqual(Indicator.No, uncappedQuiet.Backup, "without caps: no backup in the lookback");
        Assert.AreEqual(Indicator.No, uncappedQuiet.PanelHigh, "without caps: no high panel memory in the lookback");
        Assert.AreEqual(Indicator.No, uncappedQuiet.Boot, "without caps: no boot in the lookback");
        Assert.AreEqual(Indicator.Unknown, cappedQuiet.Backup, "over the snapshot cap: backup unknown");
        Assert.AreEqual(Indicator.Unknown, cappedQuiet.PanelHigh, "over the snapshot cap: panel unknown");
        Assert.AreEqual(Indicator.Unknown, cappedQuiet.Boot, "over the boot cap: boot unknown");
        Assert.AreEqual(Indicator.Yes, cappedBusy.Backup, "a kept backup stays yes");
        Assert.AreEqual(Indicator.Yes, cappedBusy.PanelHigh, "a kept high panel memory stays yes");
        Assert.AreEqual(Indicator.Yes, cappedBusy.Boot, "a kept boot stays yes");
    }

    /// <summary>
    /// Adds 72 observed hours (a log line at minute 30 of each), the snapshots at minute 20 of the hours that have one, the boots, and seals the analysis.
    /// </summary>
    /// <param name="analysis">The analysis</param>
    /// <param name="snapshotOf">Returns the snapshot of an hour, or <c>null</c> for none</param>
    /// <param name="boots">The boot times in ascending order</param>
    /// <returns>The analysis</returns>
    private static ForensicsAnalysis Prepared(ForensicsAnalysis analysis, Func<int, ProcessSnapshot?> snapshotOf, IReadOnlyList<DateTimeOffset> boots)
    {
        for (var hour = 0; hour < 72; hour++)
        {
            analysis.AddLogLine(_base.AddHours(hour).AddMinutes(30), "syslog");

            var snapshot = snapshotOf(hour);

            if (snapshot is not null)
            {
                analysis.AddProcessSnapshot(_base.AddHours(hour).AddMinutes(20), snapshot);
            }
        }

        foreach (var boot in boots)
        {
            analysis.AddBoot(boot);
        }

        analysis.Seal();

        return analysis;
    }

    /// <summary>
    /// Adds 72 observed hours, three snapshots at 10:20 of the days (low panel memory, then a backup with high panel memory twice),
    /// two boots at 10:10 of the second and third day, and seals the analysis. A cap of two snapshots or one boot drops the third
    /// snapshot or the second boot.
    /// </summary>
    /// <param name="analysis">The analysis</param>
    /// <returns>The analysis</returns>
    private static ForensicsAnalysis CappedByData(ForensicsAnalysis analysis)
    {
        for (var hour = 0; hour < 72; hour++)
        {
            analysis.AddLogLine(_base.AddHours(hour).AddMinutes(30), "syslog");
        }

        analysis.AddProcessSnapshot(_base.AddHours(10).AddMinutes(20), Snapshot(("sw-engine", 10 * Mib)));
        analysis.AddProcessSnapshot(_base.AddDays(1).AddHours(10).AddMinutes(20), Snapshot(("sw-engine", 100 * Mib), ("pzstd", Mib)));
        analysis.AddProcessSnapshot(_base.AddDays(2).AddHours(10).AddMinutes(20), Snapshot(("sw-engine", 100 * Mib), ("pzstd", Mib)));
        analysis.AddBoot(_base.AddDays(1).AddHours(10).AddMinutes(10));
        analysis.AddBoot(_base.AddDays(2).AddHours(10).AddMinutes(10));
        analysis.Seal();

        return analysis;
    }

    /// <summary>
    /// Adds the outages of the given kill times and summarizes.
    /// </summary>
    /// <param name="analysis">The sealed analysis</param>
    /// <param name="times">The times of the first kills</param>
    /// <param name="kills">The kill counts of the outages, one for each time</param>
    /// <returns>The summary</returns>
    private static ForensicsSummary Summarized(ForensicsAnalysis analysis, DateTimeOffset[] times, params long[] kills)
    {
        for (var index = 0; index < times.Length; index++)
        {
            analysis.AddIncident(Outage(times[index], kills[index]));
        }

        return analysis.Summarize(_now);
    }

    /// <summary>
    /// Returns the same time of day on each of the three days.
    /// </summary>
    /// <param name="hour">The hour</param>
    /// <param name="minute">The minute</param>
    /// <returns>The three times</returns>
    private static DateTimeOffset[] DailyAt(int hour, int minute)
    {
        return [_base.AddHours(hour).AddMinutes(minute), _base.AddDays(1).AddHours(hour).AddMinutes(minute), _base.AddDays(2).AddHours(hour).AddMinutes(minute)];
    }

    /// <summary>
    /// Creates an incident of the given first kill.
    /// </summary>
    /// <param name="firstKillAt">The time of the first kill</param>
    /// <param name="killCount">The number of kills</param>
    /// <returns>The incident</returns>
    private static Incident Outage(DateTimeOffset firstKillAt, long killCount = 1)
    {
        return new Incident
               {
                   Start = firstKillAt - _hour,
                   End = firstKillAt + TimeSpan.FromMinutes(30),
                   FirstKillAt = firstKillAt,
                   LastKillAt = firstKillAt,
                   KillCount = killCount
               };
    }

    /// <summary>
    /// Creates a process snapshot of programs.
    /// </summary>
    /// <param name="programs">The programs with their resident memory in bytes</param>
    /// <returns>The snapshot</returns>
    private static ProcessSnapshot Snapshot(params (string Program, ulong Rss)[] programs)
    {
        return IncidentSamples.Programs(programs);
    }

    #endregion // Methods
}