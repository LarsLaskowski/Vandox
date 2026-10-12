using System.Globalization;

using Vandox.Core.Incidents;
using Vandox.Core.Model;
using Vandox.Core.Signatures;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="IncidentBuilder"/>
/// </summary>
[TestClass]
public class IncidentBuilderTests
{
    #region Constants

    private const string Backup = "pzstd";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// The timeline holds the events of the window and the first and last backup snapshot, ordered by time, type and first record ID.
    /// </summary>
    [TestMethod]
    public void IncidentBuilderOrdersTimeline()
    {
        // Arrange
        var builder = IncidentSamples.Builder(IncidentSamples.Base);

        builder.AddEvent(IncidentSamples.Event(IncidentSignatures.ServiceStop, At(-10), 9));
        builder.AddEvent(IncidentSamples.Kill(At(0), recordId: 100));
        builder.AddEvent(IncidentSamples.ServiceStart(At(-5), 30));
        builder.AddEvent(IncidentSamples.Event(IncidentSignatures.ServiceStart, At(-10), 2));
        builder.AddEvent(IncidentSamples.ServiceStart(At(-61), 90));
        builder.AddEvent(IncidentSamples.ServiceStart(At(30), 91));
        builder.AddEvent(IncidentSamples.Event(IncidentSignatures.Boot, At(-10), 4));
        builder.AddEvent(IncidentSamples.ServiceStart(At(-5), 20));
        builder.AddEvent(IncidentSamples.ServiceStart(At(-60), 1));
        builder.AddProcessSnapshot(502, At(-20), IncidentSamples.Programs(("/usr/bin/pzstd", 300)));
        builder.AddProcessSnapshot(501, At(-40), IncidentSamples.Programs((Backup, 200), ("mariadbd", 100)));

        // Act
        var incident = builder.Build();

        // Assert
        Assert.AreEqual(At(-60), incident.Start, "start of the window");
        Assert.AreEqual(At(30), incident.End, "end of the window");
        Assert.AreEqual(3600L, incident.WindowBeforeSeconds, "window before in seconds");
        Assert.AreEqual(1800L, incident.WindowAfterSeconds, "window after in seconds");
        Assert.AreEqual(At(0), incident.FirstKillAt, "first kill");
        Assert.AreEqual(1L, incident.KillCount, "kill count");
        Assert.HasCount(1, incident.Kills, "kills of the cluster");
        Assert.AreEqual(0L, incident.TimelineOmitted, "nothing omitted");
        Assert.AreEqual("-60:service_start:1,-40:backup_first_seen:501,-20:backup_last_seen:502,-10:boot:4,-10:service_start:2,-10:service_stop:9,-5:service_start:20,-5:service_start:30,0:oom_kill:100", Describe(incident), "timeline in order, events outside the window left out");

        var first = incident.Timeline[1];
        var last = incident.Timeline[2];

        Assert.AreEqual(Backup, first.Texts["program"], "program of the first backup entry");
        Assert.AreEqual(200L, first.Numbers["rss_bytes"], "memory of the first backup entry");
        Assert.AreSequenceEqual([501L], first.RecordIds, "snapshot of the first backup entry");
        Assert.AreEqual(Backup, last.Texts["program"], "program of the last backup entry");
        Assert.AreEqual(300L, last.Numbers["rss_bytes"], "memory of the last backup entry");
        Assert.AreSequenceEqual([502L], last.RecordIds, "snapshot of the last backup entry");
    }

    /// <summary>
    /// The timeline keeps 1,000 entries and counts the rest, and each template list keeps the first 20.
    /// </summary>
    [TestMethod]
    public void IncidentBuilderCapsTimelineAndTemplates()
    {
        // Arrange
        var builder = IncidentSamples.Builder(IncidentSamples.Base);

        for (var index = 0; index < 1005; index++)
        {
            builder.AddEvent(IncidentSamples.ServiceStart(At(-55).AddSeconds(index), index + 1));
        }

        for (var index = 0; index < 25; index++)
        {
            builder.AddNewTemplate(new IncidentNewTemplate
                                   {
                                       Source = "syslog",
                                       Program = "cron",
                                       Template = $"new template {index}",
                                       FirstSeen = At(-30),
                                       Count = index + 1
                                   });
            builder.AddTemplateSpike(new IncidentTemplateSpike
                                     {
                                         Source = "syslog",
                                         Program = "cron",
                                         Template = $"spike template {index}",
                                         Hour = At(-30),
                                         Count = index + 100,
                                         UsualLevel = 1.5
                                     });
        }

        // Act
        var incident = builder.Build();

        // Assert
        Assert.HasCount(1000, incident.Timeline, "kept entries");
        Assert.AreEqual(5L, incident.TimelineOmitted, "omitted events");
        Assert.AreEqual(1000L, incident.Timeline[^1].RecordIds[0], "the kept entries are the first 1,000");
        Assert.HasCount(20, incident.NewTemplates, "kept new templates");
        Assert.HasCount(20, incident.TemplateSpikes, "kept spikes");
        Assert.AreSequenceEqual(Enumerable.Range(0, 20).Select(index => $"new template {index}"), incident.NewTemplates.Select(template => template.Template), "the first 20 new templates");
        Assert.AreSequenceEqual(Enumerable.Range(0, 20).Select(index => $"spike template {index}"), incident.TemplateSpikes.Select(spike => spike.Template), "the first 20 spikes");
    }

    /// <summary>
    /// The timeline stops at the first event whose entry does not fit the byte budget, and omits every later event.
    /// </summary>
    [TestMethod]
    public void IncidentBuilderCapsTimelineByteBudget()
    {
        // Arrange
        var builder = IncidentSamples.Builder(IncidentSamples.Base);
        var events = new List<DetectedEvent>();

        for (var index = 0; index < 260; index++)
        {
            events.Add(HeavyEvent(At(-50).AddSeconds(index), index + 1));
        }

        events.Add(IncidentSamples.ServiceStart(At(-50).AddSeconds(1000), 1000));

        foreach (var detected in events)
        {
            builder.AddEvent(detected);
        }

        // Act
        var incident = builder.Build();

        // Assert
        var kept = incident.Timeline.Count;
        var bytes = incident.Timeline.Sum(entry => IncidentJson.Utf8Length(entry));
        var next = IncidentJson.Utf8Length(TimelineEntry.From(events[kept]));

        Assert.IsGreaterThan(100, kept, "a large part of the heavy events fits");
        Assert.IsLessThan(260, kept, "not every heavy event fits");
        Assert.AreSequenceEqual(Enumerable.Range(1, kept).Select(id => (long)id), incident.Timeline.Select(entry => entry.RecordIds[0]), "the kept entries are a prefix in arrival order");
        Assert.IsLessThanOrEqualTo(IncidentLimits.MaxTimelineBytes, bytes, "serialized size of the kept entries");
        Assert.IsGreaterThan(IncidentLimits.MaxTimelineBytes, bytes + next, "the next entry would exceed the budget");
        Assert.AreEqual(events.Count - kept, incident.TimelineOmitted, "every omitted event is counted, the small one after the first misfit too");
        Assert.DoesNotContain(entry => entry.RecordIds[0] == 1000, incident.Timeline, "the small event after the misfit is omitted");
    }

    /// <summary>
    /// The growth candidates are the three programs that grew most from the first to the last snapshot, the consumers the three largest of the last.
    /// </summary>
    [TestMethod]
    public void IncidentBuilderRanksTopThreeGrowthCandidates()
    {
        // Arrange
        var builder = IncidentSamples.Builder(IncidentSamples.Base);

        builder.AddProcessSnapshot(3, At(-10), IncidentSamples.Programs(("alpha", 400), ("bravo", 250), ("charlie", 300), ("delta", 100), ("echo", 160), ("foxtrot", 200)));
        builder.AddProcessSnapshot(1, At(-30), IncidentSamples.Programs(("alpha", 100), ("charlie", 500), ("delta", 100), ("echo", 10), ("foxtrot", 50)));
        builder.AddProcessSnapshot(2, At(-20), IncidentSamples.Programs(("alpha", 5000), ("bravo", 5000), ("charlie", 900), ("delta", 100), ("echo", 10), ("foxtrot", 50)));

        // Act
        var hypothesis = builder.Build().Hypothesis;

        // Assert
        Assert.AreEqual(3L, hypothesis.SnapshotCount, "snapshots of the period");
        Assert.AreEqual(At(-30), hypothesis.FirstSnapshotAt, "first snapshot");
        Assert.AreEqual(At(-10), hypothesis.LastSnapshotAt, "last snapshot");
        Assert.IsTrue(hypothesis.GrowthKnown, "growth is known");
        Assert.AreSequenceEqual(["alpha", "bravo", "echo"], hypothesis.Growth.Select(entry => entry.Program), "growth candidates: last minus first, the middle snapshot does not count, ties by name");
        Assert.AreSequenceEqual([300UL, 250UL, 150UL], hypothesis.Growth.Select(entry => entry.GrowthBytes), "growth in bytes");
        Assert.AreSequenceEqual([100UL, 0UL, 10UL], hypothesis.Growth.Select(entry => entry.StartRssBytes), "start memory, 0 for a program absent from the first snapshot");
        Assert.AreSequenceEqual(["alpha", "charlie", "bravo"], hypothesis.Consumers.Select(entry => entry.Program), "consumers of the last snapshot");
        Assert.AreSequenceEqual([400UL, 300UL, 250UL], hypothesis.Consumers.Select(entry => entry.RssBytes), "consumer memory");
    }

    /// <summary>
    /// Growth starts after the last boot of the lookback: snapshots at or before the boot do not count.
    /// </summary>
    [TestMethod]
    public void IncidentBuilderStartsGrowthAfterLastBoot()
    {
        // Act
        var growth = BuildAroundBoots(true, true).Hypothesis;
        var oneSnapshot = BuildAroundBoots(false, true).Hypothesis;
        var oldOnly = BuildAroundBoots(false, false).Hypothesis;

        // Assert
        Assert.IsTrue(growth.GrowthKnown, "two snapshots after the boot");
        Assert.AreEqual(2L, growth.SnapshotCount, "snapshots after the boot");
        Assert.AreEqual(At(-10), growth.FirstSnapshotAt, "the snapshot exactly at the boot is not the baseline");
        Assert.AreEqual(At(-2), growth.LastSnapshotAt, "last snapshot");
        Assert.AreEqual(At(-15), growth.RecentBootAt, "the last boot before the first kill");
        Assert.AreSequenceEqual(["web", "db", "base"], growth.Growth.Select(entry => entry.Program), "growth from the first to the last snapshot after the boot");
        Assert.AreSequenceEqual([400UL, 300UL, 50UL], growth.Growth.Select(entry => entry.GrowthBytes), "growth in bytes");
        Assert.AreSequenceEqual(["web", "db", "base"], growth.Consumers.Select(entry => entry.Program), "consumers of the last snapshot");
        Assert.IsFalse(oneSnapshot.GrowthKnown, "one snapshot after the boot: growth unknown");
        Assert.IsEmpty(oneSnapshot.Growth, "no growth candidates");
        Assert.AreEqual(1L, oneSnapshot.SnapshotCount, "one snapshot after the boot");
        Assert.AreSequenceEqual(["web", "base"], oneSnapshot.Consumers.Select(entry => entry.Program), "consumers of the only snapshot");
        Assert.AreEqual(0L, oldOnly.SnapshotCount, "snapshots before the boot do not count");
        Assert.IsFalse(oldOnly.GrowthKnown, "growth unknown without snapshots after the boot");
        Assert.IsEmpty(oldOnly.Consumers, "no consumers");
        Assert.IsNull(oldOnly.BackupRunning, "backup unknown without snapshots after the boot");
        Assert.AreEqual(At(-15), oldOnly.RecentBootAt, "the boot is still known");
    }

    /// <summary>
    /// An event after a process snapshot is refused, because the last boot decides which snapshots count.
    /// </summary>
    [TestMethod]
    public void IncidentBuilderRefusesEventsAfterProcessSnapshots()
    {
        // Arrange
        var builder = IncidentSamples.Builder(IncidentSamples.Base);

        builder.AddProcessSnapshot(1, At(-10), IncidentSamples.Programs(("web", 100)));

        // Act
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => builder.AddEvent(IncidentSamples.ServiceStart(At(-5))), "event after a snapshot");

        // Assert
        Assert.AreEqual("incidents: events must be added before process snapshots", exception.Message, "message");
    }

    /// <summary>
    /// Only snapshots before the first kill count for the hypothesis.
    /// </summary>
    [TestMethod]
    public void IncidentBuilderUsesOnlyLookbackSnapshotsForGrowth()
    {
        // Arrange
        var builder = IncidentSamples.Builder(IncidentSamples.Base);

        builder.AddProcessSnapshot(1, At(-20), IncidentSamples.Programs(("web", 100)));
        builder.AddProcessSnapshot(2, At(-5), IncidentSamples.Programs(("web", 300)));
        builder.AddProcessSnapshot(3, At(0), IncidentSamples.Programs(("web", 9000)));
        builder.AddProcessSnapshot(4, At(10), IncidentSamples.Programs(("web", 99000)));

        // Act
        var hypothesis = builder.Build().Hypothesis;

        // Assert
        Assert.AreEqual(At(-20), hypothesis.FirstSnapshotAt, "first snapshot");
        Assert.AreEqual(At(-5), hypothesis.LastSnapshotAt, "the snapshot at the time of the kill is not counted");
        Assert.AreEqual(2L, hypothesis.SnapshotCount, "snapshots of the lookback");
        Assert.AreEqual(200UL, hypothesis.Growth[0].GrowthBytes, "growth between the two lookback snapshots");
        Assert.AreEqual(300UL, hypothesis.Consumers[0].RssBytes, "consumers of the last lookback snapshot");
    }

    /// <summary>
    /// With fewer than two snapshots growth is unknown, and without any the consumers and the backup are unknown too.
    /// </summary>
    [TestMethod]
    public void IncidentBuilderMarksGrowthUnknownWithFewerThanTwoSnapshots()
    {
        // Arrange
        var single = IncidentSamples.Builder(IncidentSamples.Base);
        var none = IncidentSamples.Builder(IncidentSamples.Base);

        single.AddProcessSnapshot(1, At(-10), IncidentSamples.Programs(("web", 100), ("db", 300)));

        // Act
        var one = single.Build().Hypothesis;
        var empty = none.Build().Hypothesis;

        // Assert
        Assert.IsFalse(one.GrowthKnown, "growth is unknown with one snapshot");
        Assert.IsEmpty(one.Growth, "no growth candidates with one snapshot");
        Assert.AreEqual(1L, one.SnapshotCount, "one snapshot");
        Assert.AreEqual(At(-10), one.FirstSnapshotAt, "first snapshot");
        Assert.AreEqual(At(-10), one.LastSnapshotAt, "last snapshot");
        Assert.AreSequenceEqual(["db", "web"], one.Consumers.Select(entry => entry.Program), "consumers come from the only snapshot");
        Assert.IsFalse(empty.GrowthKnown, "growth is unknown without snapshots");
        Assert.IsEmpty(empty.Growth, "no growth candidates without snapshots");
        Assert.AreEqual(0L, empty.SnapshotCount, "no snapshots");
        Assert.IsNull(empty.FirstSnapshotAt, "no first snapshot");
        Assert.IsNull(empty.LastSnapshotAt, "no last snapshot");
        Assert.IsEmpty(empty.Consumers, "no consumers without snapshots");
        Assert.IsNull(empty.BackupRunning, "backup unknown without snapshots");
        Assert.IsNull(empty.RecentBootAt, "no boot");
        Assert.AreEqual(0L, empty.ServiceEvents, "no service events");
    }

    /// <summary>
    /// A backup program in a snapshot after the last boot, the last boot and the service activity before the first kill are recorded.
    /// </summary>
    [TestMethod]
    public void IncidentBuilderRecordsBackupBootAndServiceActivity()
    {
        // Arrange
        var running = IncidentSamples.Builder(IncidentSamples.Base);
        var single = IncidentSamples.Builder(IncidentSamples.Base);
        var beforeBoot = IncidentSamples.Builder(IncidentSamples.Base);
        var activity = IncidentSamples.Builder(IncidentSamples.Base);

        running.AddProcessSnapshot(1, At(-30), IncidentSamples.Programs((Backup, 200), ("web", 100)));
        running.AddProcessSnapshot(2, At(-10), IncidentSamples.Programs(("/usr/bin/pzstd", 300), ("web", 100)));
        single.AddProcessSnapshot(3, At(-10), IncidentSamples.Programs((Backup, 200)));
        beforeBoot.AddEvent(IncidentSamples.Event(IncidentSignatures.Boot, At(-15), 7));
        beforeBoot.AddProcessSnapshot(4, At(-30), IncidentSamples.Programs((Backup, 200)));
        beforeBoot.AddProcessSnapshot(5, At(-10), IncidentSamples.Programs(("web", 100)));
        activity.AddEvent(IncidentSamples.Event(IncidentSignatures.Boot, At(-50), 1));
        activity.AddEvent(IncidentSamples.Event(IncidentSignatures.Boot, At(-30), 2));
        activity.AddEvent(IncidentSamples.Event(IncidentSignatures.Boot, At(10), 3));
        activity.AddEvent(IncidentSamples.ServiceStart(At(-29), 4));
        activity.AddEvent(IncidentSamples.ServiceStart(At(-20), 5));
        activity.AddEvent(IncidentSamples.Event(IncidentSignatures.ServiceStop, At(-19), 6));
        activity.AddEvent(IncidentSamples.Event(IncidentSignatures.ServiceFailed, At(-18), 7));
        activity.AddEvent(IncidentSamples.Event(IncidentSignatures.ServiceFailed, At(5), 8));
        activity.AddEvent(IncidentSamples.Kill(At(0), recordId: 9));

        // Act
        var runningIncident = running.Build();
        var singleIncident = single.Build();
        var beforeBootIncident = beforeBoot.Build();
        var activityIncident = activity.Build();

        // Assert
        Assert.IsTrue(runningIncident.Hypothesis.BackupRunning, "a backup program ran");
        Assert.AreSequenceEqual([Backup], runningIncident.Hypothesis.BackupPrograms, "the backup programs seen");
        Assert.AreEqual("-30:backup_first_seen:1,-10:backup_last_seen:2", Describe(runningIncident), "first and last backup entry");
        Assert.IsTrue(singleIncident.Hypothesis.BackupRunning, "a backup program ran in the only snapshot");
        Assert.AreEqual("-10:backup_first_seen:3", Describe(singleIncident), "only the first backup entry for one snapshot");
        Assert.IsFalse(beforeBootIncident.Hypothesis.BackupRunning, "the backup ended with the reboot: snapshots exist after it and none holds a backup");
        Assert.IsEmpty(beforeBootIncident.Hypothesis.BackupPrograms, "no backup program after the boot");
        Assert.AreEqual("-30:backup_first_seen:4,-15:boot:7", Describe(beforeBootIncident), "the timeline still shows the backup before the boot");
        Assert.AreEqual(At(-15), beforeBootIncident.Hypothesis.RecentBootAt, "the boot");
        Assert.AreEqual(At(-30), activityIncident.Hypothesis.RecentBootAt, "the last boot before the first kill");
        Assert.AreEqual(4L, activityIncident.Hypothesis.ServiceEvents, "starts, stops and failures before the first kill");
    }

    /// <summary>
    /// The five metric definitions keep their extreme with time, source and sample count; labels match exactly, ties keep the earliest, non-finite values are skipped.
    /// </summary>
    [TestMethod]
    public void IncidentBuilderKeepsMetricExtremes()
    {
        // Arrange
        var builder = IncidentSamples.Builder(IncidentSamples.Base);
        var some = ("kind", "some");
        var full = ("kind", "full");
        var memory = ("resource", "memory");
        var legacy = "legacy-monitor";

        builder.AddMetric(legacy, At(-30), IncidentSamples.Metric("mem_available_bytes", 500));
        builder.AddMetric(legacy, At(-20), IncidentSamples.Metric("mem_available_bytes", 300));
        builder.AddMetric("other", At(-10), IncidentSamples.Metric("mem_available_bytes", 300));
        builder.AddMetric(legacy, At(-15), IncidentSamples.Metric("mem_available_bytes", double.NaN));
        builder.AddMetric(legacy, At(-14), IncidentSamples.Metric("mem_available_bytes", double.NegativeInfinity));
        builder.AddMetric(legacy, At(-12), IncidentSamples.Metric("mem_available_bytes", 1, ("cpu", "0")));
        builder.AddMetric(legacy, At(-30), IncidentSamples.Metric("pressure_avg10", 0.5, memory, some));
        builder.AddMetric(legacy, At(-20), IncidentSamples.Metric("pressure_avg10", 3.0, memory, some));
        builder.AddMetric(legacy, At(-10), IncidentSamples.Metric("pressure_avg10", 2.0, memory, some));
        builder.AddMetric(legacy, At(-9), IncidentSamples.Metric("pressure_avg10", 99, memory, some, ("extra", "x")));
        builder.AddMetric(legacy, At(-8), IncidentSamples.Metric("pressure_avg10", 99, ("resource", "cpu"), some));
        builder.AddMetric(legacy, At(-7), IncidentSamples.Metric("pressure_avg10", 99));
        builder.AddMetric(legacy, At(-5), IncidentSamples.Metric("pressure_avg10", 1.0, memory, full));
        builder.AddMetric("a", At(-30), IncidentSamples.Metric("load1", 2.0));
        builder.AddMetric("b", At(-20), IncidentSamples.Metric("load1", 2.0));
        builder.AddMetric(legacy, At(-6), IncidentSamples.Metric("cpu_percent", 99));

        // Act
        var metrics = builder.Build().Metrics;

        // Assert
        Assert.AreSequenceEqual(["mem_available_bytes", "pressure_avg10", "pressure_avg10", "load1"], metrics.Select(metric => metric.Name), "definitions in order, swap_used_bytes has no samples and is absent");

        var mem = metrics[0];
        var pressureSome = metrics[1];
        var pressureFull = metrics[2];
        var load = metrics[3];

        Assert.AreEqual("min", mem.Extreme, "available memory is a minimum");
        Assert.AreEqual(300.0, mem.Value, "lowest available memory");
        Assert.AreEqual(At(-20), mem.At, "a tie keeps the earliest");
        Assert.AreEqual(legacy, mem.Source, "source of the earliest");
        Assert.AreEqual(3L, mem.Samples, "finite samples with matching labels");
        Assert.IsNull(mem.Labels, "no labels");
        Assert.AreEqual("max", pressureSome.Extreme, "pressure is a maximum");
        Assert.AreEqual(3.0, pressureSome.Value, "highest some pressure");
        Assert.AreEqual(At(-20), pressureSome.At, "time of the highest some pressure");
        Assert.AreEqual(3L, pressureSome.Samples, "samples whose labels match exactly");
        Assert.AreEqual("memory", pressureSome.Labels!["resource"], "resource label");
        Assert.AreEqual("some", pressureSome.Labels["kind"], "kind label");
        Assert.HasCount(2, pressureSome.Labels, "two labels");
        Assert.AreEqual("full", pressureFull.Labels!["kind"], "kind label of the full pressure");
        Assert.AreEqual(1.0, pressureFull.Value, "highest full pressure");
        Assert.AreEqual(1L, pressureFull.Samples, "samples of the full pressure");
        Assert.AreEqual("max", load.Extreme, "load is a maximum");
        Assert.AreEqual(At(-30), load.At, "a tie keeps the earliest");
        Assert.AreEqual("a", load.Source, "source of the earliest");
        Assert.AreEqual(2L, load.Samples, "load samples");
    }

    /// <summary>
    /// The connection peak is the snapshot with the most sockets; the earliest wins a tie, and its three busiest commands are kept.
    /// </summary>
    [TestMethod]
    public void IncidentBuilderKeepsConnectionPeak()
    {
        // Arrange
        var builder = IncidentSamples.Builder(IncidentSamples.Base);
        var none = IncidentSamples.Builder(IncidentSamples.Base);
        var first = IncidentSamples.Connections(10, ("apache2", 1));
        var withoutStates = new ConnectionSnapshot
                            {
                                Complete = true,
                                Processes = [
                                                new ProcessConnections
                                                {
                                                    Pid = 1,
                                                    Command = "huge",
                                                    Count = 500
                                                }
                                            ]
                            };

        first.States!.Add(new StateCount
                          {
                              Proto = Proto.Tcp,
                              State = "TIME_WAIT",
                              Count = 5
                          });
        builder.AddConnectionSnapshot(13, At(-5), IncidentSamples.Connections(20, ("other", 1)));
        builder.AddConnectionSnapshot(11, At(-20), first);
        builder.AddConnectionSnapshot(12, At(-10), IncidentSamples.Connections(20, ("apache2", 7), ("apache2", 6), ("php-fpm", 3), ("mariadbd", 2), ("nginx", 13)));
        builder.AddConnectionSnapshot(14, At(-3), withoutStates);

        // Act
        var peak = builder.Build().Connections;
        var absent = none.Build().Connections;

        // Assert
        Assert.IsNotNull(peak, "a peak exists");
        Assert.AreEqual(4L, peak.Snapshots, "snapshots of the window");
        Assert.AreEqual(20L, peak.Sockets, "sockets are the sum of the state counts");
        Assert.AreEqual(12L, peak.RecordId, "a tie keeps the earliest snapshot");
        Assert.AreEqual(At(-10), peak.At, "time of the peak");
        Assert.AreSequenceEqual(["apache2", "nginx", "php-fpm"], peak.Processes.Select(process => process.Command), "top three commands by summed count, then command");
        Assert.AreSequenceEqual([13L, 13L, 3L], peak.Processes.Select(process => process.Count), "summed counts");
        Assert.IsNull(absent, "no snapshot: unknown");
    }

    /// <summary>
    /// An incident is built once.
    /// </summary>
    [TestMethod]
    public void IncidentBuilderBuildsOnce()
    {
        // Arrange
        var builder = IncidentSamples.Builder(IncidentSamples.Base);

        Assert.IsNotNull(builder.Build(), "the first build");

        // Act
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => builder.Build(), "second build");

        // Assert
        Assert.AreEqual("incidents: already built", exception.Message, "message");
    }

    /// <summary>
    /// Process snapshots, connection snapshots and metrics outside the window, at its start minus a minute and at its exclusive end, change nothing.
    /// </summary>
    [TestMethod]
    public void IncidentBuilderIgnoresSnapshotsAndMetricsOutsideWindow()
    {
        // Arrange
        var builder = IncidentSamples.Builder(IncidentSamples.Base);
        var legacy = "legacy-monitor";

        builder.AddProcessSnapshot(1, At(-61), IncidentSamples.Programs(("huge", 9000), (Backup, 50)));
        builder.AddProcessSnapshot(2, At(-30), IncidentSamples.Programs(("base", 100)));
        builder.AddProcessSnapshot(3, At(-10), IncidentSamples.Programs(("base", 200)));
        builder.AddProcessSnapshot(4, At(30), IncidentSamples.Programs(("huge", 9000), (Backup, 50)));
        builder.AddConnectionSnapshot(5, At(-61), IncidentSamples.Connections(1000, ("evil", 1000)));
        builder.AddConnectionSnapshot(6, At(-20), IncidentSamples.Connections(5, ("web", 5)));
        builder.AddConnectionSnapshot(7, At(30), IncidentSamples.Connections(1000, ("evil", 1000)));
        builder.AddMetric(legacy, At(-61), IncidentSamples.Metric("load1", 99));
        builder.AddMetric(legacy, At(-20), IncidentSamples.Metric("load1", 2));
        builder.AddMetric(legacy, At(30), IncidentSamples.Metric("load1", 99));

        // Act
        var incident = builder.Build();

        // Assert
        Assert.AreEqual(2L, incident.Hypothesis.SnapshotCount, "only the snapshots of the window count");
        Assert.IsFalse(incident.Hypothesis.BackupRunning, "the backup program outside the window is not seen");
        Assert.IsEmpty(incident.Timeline, "no backup entries");
        Assert.IsNotNull(incident.Connections, "connection peak of the window");
        Assert.AreEqual(5L, incident.Connections.Sockets, "sockets of the only snapshot in the window");
        Assert.AreEqual(1L, incident.Connections.Snapshots, "connection snapshots in the window");
        Assert.AreEqual(6L, incident.Connections.RecordId, "record of the peak");
        Assert.HasCount(1, incident.Metrics, "one metric");
        Assert.AreEqual(2.0, incident.Metrics[0].Value, "the metric sample outside the window is ignored");
        Assert.AreEqual(1L, incident.Metrics[0].Samples, "metric samples in the window");
    }

    /// <summary>
    /// 100,000 events with large texts and 1,000 snapshots of 4,096 programs leave only what the caps keep: the first 1,000 timeline entries and two program lists.
    /// </summary>
    [TestMethod]
    public void IncidentBuilderRetainsBoundedMemory()
    {
        // Arrange
        const int eventCount = 100000;
        const int snapshotCount = 1000;
        const long bound = 24L * 1024 * 1024;
        var builder = IncidentSamples.Builder(IncidentSamples.Base);
        var snapshot = IncidentSamples.Programs(Enumerable.Range(0, 4096).Select(index => (index == 0 ? Backup : $"program-{index}", (ulong)(index + 1))).ToArray());
        var before = RetainedBytes();

        // Act
        for (var index = 0; index < eventCount; index++)
        {
            builder.AddEvent(LargeEvent(At(-55).AddSeconds(index % 3000), index + 1));
        }

        for (var index = 0; index < snapshotCount; index++)
        {
            builder.AddProcessSnapshot(index + 1, At(-50).AddSeconds(index), snapshot);
        }

        var retained = RetainedBytes() - before;

        GC.KeepAlive(builder);

        var incident = builder.Build();

        // Assert
        Assert.IsLessThan(bound, retained, "retained bytes, keeping every event would be about 600 MB and every snapshot about 600 MB");
        Assert.HasCount(IncidentLimits.MaxTimelineEvents, incident.Timeline.Where(entry => entry.Type == IncidentSignatures.ServiceStart).ToList(), "timeline entries kept");
        Assert.AreEqual(eventCount - IncidentLimits.MaxTimelineEvents, incident.TimelineOmitted, "omitted events are counted");
        Assert.AreEqual((long)snapshotCount, incident.Hypothesis.SnapshotCount, "snapshots counted");
        Assert.HasCount(IncidentLimits.MaxCandidates, incident.Hypothesis.Consumers, "consumers");
    }

    /// <summary>
    /// Returns a time relative to the first kill.
    /// </summary>
    /// <param name="minutes">Minutes after the first kill, negative before it</param>
    /// <returns>The time</returns>
    private static DateTimeOffset At(int minutes)
    {
        return IncidentSamples.Base.AddMinutes(minutes);
    }

    /// <summary>
    /// Describes the timeline as minutes from the first kill, type and first record ID.
    /// </summary>
    /// <param name="incident">The incident</param>
    /// <returns>The entries joined by commas</returns>
    private static string Describe(Incident incident)
    {
        return string.Join(',', incident.Timeline.Select(entry => string.Create(CultureInfo.InvariantCulture, $"{(entry.At - IncidentSamples.Base).TotalMinutes}:{entry.Type}:{entry.RecordIds[0]}")));
    }

    /// <summary>
    /// Returns the bytes the process retains after a full collection, which is how the heap-bound claims are measured.
    /// </summary>
    /// <returns>The number of bytes</returns>
    private static long RetainedBytes()
    {
#pragma warning disable S1215
        return GC.GetTotalMemory(true);
#pragma warning restore S1215
    }

    /// <summary>
    /// Creates a service start whose three texts are separate strings of 1,024 characters and that names 256 records.
    /// </summary>
    /// <param name="at">The time</param>
    /// <param name="recordId">The ID of the first record</param>
    /// <returns>The event</returns>
    private static DetectedEvent LargeEvent(DateTimeOffset at, long recordId)
    {
        var detected = IncidentSamples.ServiceStart(at, recordId);

        detected.Host = new string('h', 1024);
        detected.Texts = new Dictionary<string, string>
                         {
                             ["unit"] = new string('u', 1024),
                             ["description"] = new string('d', 1024)
                         };
        detected.RecordIds = Enumerable.Range(0, 256).Select(offset => recordId + offset).ToList();
        detected.LineCount = 256;

        return detected;
    }

    /// <summary>
    /// Creates a service start whose entry serializes to about 18 KB.
    /// </summary>
    /// <param name="at">The time</param>
    /// <param name="recordId">The ID of the only record</param>
    /// <returns>The event</returns>
    private static DetectedEvent HeavyEvent(DateTimeOffset at, long recordId)
    {
        var detected = IncidentSamples.ServiceStart(at, recordId);
        var text = new string('<', 1024);

        detected.Host = text;
        detected.Texts = new Dictionary<string, string>
                         {
                             ["unit"] = text,
                             ["description"] = text
                         };

        return detected;
    }

    /// <summary>
    /// Builds an incident whose lookback holds boots at 40 and 15 minutes and snapshots before, at and after the last boot.
    /// </summary>
    /// <param name="withLast">Whether the snapshot two minutes before the kill exists</param>
    /// <param name="withRecent">Whether the snapshots after the last boot exist at all</param>
    /// <returns>The incident</returns>
    private static Incident BuildAroundBoots(bool withLast, bool withRecent)
    {
        var builder = IncidentSamples.Builder(IncidentSamples.Base);

        builder.AddEvent(IncidentSamples.Event(IncidentSignatures.Boot, At(-40), 1));
        builder.AddEvent(IncidentSamples.Event(IncidentSignatures.Boot, At(-15), 2));
        builder.AddProcessSnapshot(10, At(-50), IncidentSamples.Programs(("huge", 9000), ("base", 100)));
        builder.AddProcessSnapshot(11, At(-20), IncidentSamples.Programs(("huge2", 8000), ("base", 100)));
        builder.AddProcessSnapshot(12, At(-15), IncidentSamples.Programs(("atboot", 7000)));

        if (withRecent)
        {
            builder.AddProcessSnapshot(13, At(-10), IncidentSamples.Programs(("base", 100), ("web", 200)));
        }

        if (withLast)
        {
            builder.AddProcessSnapshot(14, At(-2), IncidentSamples.Programs(("base", 150), ("web", 600), ("db", 300)));
        }

        return builder.Build();
    }

    #endregion // Methods
}