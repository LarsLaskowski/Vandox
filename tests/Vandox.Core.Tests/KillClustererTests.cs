using Vandox.Core.Incidents;
using Vandox.Core.Model;
using Vandox.Core.Signatures;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="KillClusterer"/> and <see cref="IncidentKill"/>
/// </summary>
[TestClass]
public class KillClustererTests
{
    #region Methods

    /// <summary>
    /// Kills whose windows overlap form one cluster that ends with the last kill's window; the next kill closes it.
    /// </summary>
    [TestMethod]
    public void KillClustererMergesKillsWhoseWindowsOverlap()
    {
        // Arrange
        var start = IncidentSamples.Base;
        var clusterer = new KillClusterer(TimeSpan.FromHours(1), TimeSpan.FromMinutes(30));

        // Act
        var first = clusterer.Add(IncidentSamples.Kill(start, recordId: 1));
        var second = clusterer.Add(IncidentSamples.Kill(start.AddMinutes(80), recordId: 2));
        var closed = clusterer.Add(IncidentSamples.Kill(start.AddMinutes(211), recordId: 3));
        var last = clusterer.Complete();

        // Assert
        Assert.IsNull(first, "the first kill opens a cluster");
        Assert.IsNull(second, "the second kill extends it");
        Assert.IsNotNull(closed, "the third kill closes the first cluster");
        Assert.AreEqual(start.AddHours(-1), closed.Start, "start of the first cluster");
        Assert.AreEqual(start.AddMinutes(110), closed.End, "end of the first cluster is the window end of its last kill");
        Assert.AreEqual(2, closed.KillCount, "kills of the first cluster");
        Assert.HasCount(2, closed.Kills, "kept kills of the first cluster");
        Assert.AreEqual(start, closed.FirstKillAt, "first kill of the first cluster");
        Assert.AreEqual(start.AddMinutes(80), closed.LastKillAt, "last kill of the first cluster");
        Assert.IsNotNull(last, "Complete returns the open cluster");
        Assert.AreEqual(start.AddMinutes(151), last.Start, "start of the second cluster");
        Assert.AreEqual(start.AddMinutes(241), last.End, "end of the second cluster");
        Assert.AreEqual(1, last.KillCount, "kills of the second cluster");
        Assert.AreEqual(start.AddMinutes(211), last.FirstKillAt, "first kill of the second cluster");
        Assert.AreEqual(start.AddMinutes(211), last.LastKillAt, "last kill of the second cluster");
    }

    /// <summary>
    /// Windows that only touch are two clusters.
    /// </summary>
    [TestMethod]
    public void KillClustererSeparatesWindowsThatOnlyTouch()
    {
        // Arrange
        var start = IncidentSamples.Base;
        var clusterer = new KillClusterer(TimeSpan.FromHours(1), TimeSpan.FromMinutes(30));

        // Act
        clusterer.Add(IncidentSamples.Kill(start));

        var closed = clusterer.Add(IncidentSamples.Kill(start.AddMinutes(90)));
        var last = clusterer.Complete();

        // Assert
        Assert.IsNotNull(closed, "the second kill starts a new cluster");
        Assert.AreEqual(start.AddMinutes(30), closed.End, "the first window ends where the second begins");
        Assert.AreEqual(1, closed.KillCount, "kills of the first cluster");
        Assert.IsNotNull(last, "the second cluster is open");
        Assert.AreEqual(start.AddMinutes(30), last.Start, "the second window begins where the first ends");
        Assert.AreEqual(1, last.KillCount, "kills of the second cluster");
    }

    /// <summary>
    /// Windows are clipped to the storable range, and a kill at the last storable instant is ignored.
    /// </summary>
    [TestMethod]
    public void KillClustererClipsWindowsToStorableRange()
    {
        // Arrange
        var clusterer = new KillClusterer(TimeSpan.FromHours(1), TimeSpan.FromMinutes(30));
        var lastTick = StorableTime.Max.AddTicks(-1);

        // Act
        clusterer.Add(IncidentSamples.Kill(StorableTime.Min));

        var earliest = clusterer.Add(IncidentSamples.Kill(lastTick));
        var ignored = clusterer.Add(IncidentSamples.Kill(StorableTime.Max));
        var latest = clusterer.Complete();
        var empty = new KillClusterer(TimeSpan.FromHours(1), TimeSpan.FromMinutes(30));
        var ignoredAlone = empty.Add(IncidentSamples.Kill(StorableTime.Max));
        var nothing = empty.Complete();

        // Assert
        Assert.IsNotNull(earliest, "the second kill closes the first cluster");
        Assert.AreEqual(StorableTime.Min, earliest.Start, "a window before the storable range starts at its first instant");
        Assert.AreEqual(StorableTime.Min.AddMinutes(30), earliest.End, "end of the first window");
        Assert.IsNull(ignored, "a kill at the last storable instant is ignored");
        Assert.IsNotNull(latest, "the cluster of the second kill is still open");
        Assert.AreEqual(StorableTime.Max, latest.End, "a window beyond the storable range ends at its last instant");
        Assert.AreEqual(lastTick, latest.FirstKillAt, "first kill");
        Assert.AreEqual(1, latest.KillCount, "the ignored kill is not counted");
        Assert.IsTrue((latest.FirstKillAt >= latest.Start) && (latest.FirstKillAt < latest.End), "the kill lies in its window");
        Assert.IsNull(ignoredAlone, "an ignored kill closes nothing");
        Assert.IsNull(nothing, "an ignored kill opens no cluster");
    }

    /// <summary>
    /// A cluster keeps its first 16 kills and counts all.
    /// </summary>
    [TestMethod]
    public void KillClustererKeepsSixteenKillsAndCountsAll()
    {
        // Arrange
        var clusterer = new KillClusterer(TimeSpan.FromHours(1), TimeSpan.FromMinutes(30));

        // Act
        for (var index = 0; index < 20; index++)
        {
            Assert.IsNull(clusterer.Add(IncidentSamples.Kill(IncidentSamples.Base.AddMinutes(index), 1000 + index, recordId: index + 1)), $"kill {index} extends the cluster");
        }

        var cluster = clusterer.Complete();

        // Assert
        Assert.IsNotNull(cluster, "one cluster");
        Assert.HasCount(16, cluster.Kills, "kept kills");
        Assert.AreEqual(20, cluster.KillCount, "counted kills");
        Assert.AreSequenceEqual(Enumerable.Range(1000, 16).Select(pid => (long?)pid), cluster.Kills.Select(kill => kill.VictimPid), "the first 16 kills are kept");
        Assert.AreEqual(IncidentSamples.Base, cluster.FirstKillAt, "first kill");
        Assert.AreEqual(IncidentSamples.Base.AddMinutes(19), cluster.LastKillAt, "last kill is the twentieth");
        Assert.AreEqual(IncidentSamples.Base.AddMinutes(49), cluster.End, "the window ends after the last kill");
    }

    /// <summary>
    /// Wrong input is refused with fixed texts.
    /// </summary>
    [TestMethod]
    public void KillClustererRefusesWrongInput()
    {
        // Arrange
        var wrongSignature = new KillClusterer(TimeSpan.FromHours(1), TimeSpan.FromMinutes(30));
        var outOfOrder = new KillClusterer(TimeSpan.FromHours(1), TimeSpan.FromMinutes(30));
        var complete = new KillClusterer(TimeSpan.FromHours(1), TimeSpan.FromMinutes(30));

        outOfOrder.Add(IncidentSamples.Kill(IncidentSamples.Base));
        complete.Complete();

        // Act
        Assert.ThrowsExactly<ArgumentException>(() => wrongSignature.Add(IncidentSamples.Event(IncidentSignatures.Boot, IncidentSamples.Base)), "an event that is no kill");

        var early = Assert.ThrowsExactly<InvalidOperationException>(() => outOfOrder.Add(IncidentSamples.Kill(IncidentSamples.Base.AddTicks(-1))), "a kill earlier than the previous");
        var addAfter = Assert.ThrowsExactly<InvalidOperationException>(() => complete.Add(IncidentSamples.Kill(IncidentSamples.Base)), "Add after Complete");
        var completeAfter = Assert.ThrowsExactly<InvalidOperationException>(() => complete.Complete(), "Complete after Complete");

        // Assert
        Assert.AreEqual("incidents: kills must be added in time order", early.Message, "message of a kill out of order");
        Assert.AreEqual("incidents: already complete", addAfter.Message, "message of Add after Complete");
        Assert.AreEqual("incidents: already complete", completeAfter.Message, "message of Complete after Complete");
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new KillClusterer(TimeSpan.Zero, TimeSpan.FromMinutes(30)), "a window before of zero");
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new KillClusterer(TimeSpan.FromHours(1), TimeSpan.FromMinutes(-1)), "a negative window after");
    }

    /// <summary>
    /// A kill takes the victim, the memory fields, the host, the first 16 record IDs and the line count of its event; absent fields stay null or empty.
    /// </summary>
    [TestMethod]
    public void KillClustererMapsKillFields()
    {
        // Arrange
        var full = IncidentSamples.Kill(IncidentSamples.Base, 77, "php-fpm");

        full.Numbers = new Dictionary<string, long>
                       {
                           ["victim_pid"] = 77,
                           ["anon_rss_bytes"] = 4096,
                           ["oom_score_adj"] = -1000
                       };
        full.Host = "web-9";
        full.RecordIds = Enumerable.Range(1, 20).Select(id => (long)id).ToList();
        full.LineCount = 20;

        var bare = IncidentSamples.Event(IncidentSignatures.OomKill, IncidentSamples.Base.AddMinutes(1));

        // Act
        var mapped = IncidentKill.From(full);
        var absent = IncidentKill.From(bare);

        // Assert
        Assert.AreEqual(IncidentSamples.Base, mapped.At, "time");
        Assert.AreEqual("web-9", mapped.Host, "host");
        Assert.AreEqual(77L, mapped.VictimPid, "victim pid");
        Assert.AreEqual("php-fpm", mapped.VictimCommand, "victim command");
        Assert.AreEqual(4096L, mapped.AnonRssBytes, "anon-RSS");
        Assert.AreEqual(-1000L, mapped.OomScoreAdj, "oom_score_adj");
        Assert.AreSequenceEqual(Enumerable.Range(1, 16).Select(id => (long)id), mapped.RecordIds, "the first 16 record IDs");
        Assert.AreEqual(20L, mapped.LineCount, "line count");
        Assert.IsNull(absent.VictimPid, "absent victim pid");
        Assert.AreEqual(string.Empty, absent.VictimCommand, "absent victim command");
        Assert.IsNull(absent.AnonRssBytes, "absent anon-RSS");
        Assert.IsNull(absent.OomScoreAdj, "absent oom_score_adj");
        Assert.AreSequenceEqual([1L], absent.RecordIds, "the only record ID");
        Assert.AreEqual(1L, absent.LineCount, "line count of the bare event");
    }

    #endregion // Methods
}