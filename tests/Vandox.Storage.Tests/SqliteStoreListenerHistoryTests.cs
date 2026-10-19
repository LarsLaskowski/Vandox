using System.Net;

using Microsoft.Data.Sqlite;

using Vandox.Core.Model;
using Vandox.Core.Reports;

namespace Vandox.Storage.Tests;

/// <summary>
/// Tests for the listener comparisons that <see cref="SqliteStore"/> writes with a batch and reads for the forensics report
/// </summary>
[TestClass]
public class SqliteStoreListenerHistoryTests
{
    #region Constants

    private const string Agent = "agent-1";

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// The first snapshot after a start is compared with the base snapshot of the same agent, and the finding names the new listener with the owner of the later snapshot.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreListenerFindingsComparesStartWithPreviousBySeq()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        // Act
        await store.WriteBatchAsync(ListenerSamples.Batch(Agent, BaseRecord(1, 0), StartRecord(2, 60)), TestContext.CancellationToken);

        var tallies = await store.ListenerTalliesAsync(TestContext.CancellationToken);
        var starts = await ReadAsync(store, ListenerFindingKind.AgentStart);

        // Assert
        Assert.HasCount(1, tallies.Agents, "one agent");
        Assert.AreEqual(0L, tallies.Omitted, "no agent left out");
        Assert.AreEqual(Agent, tallies.Agents[0].AgentId, "agent ID");
        Assert.AreEqual(2L, tallies.Agents[0].Snapshots, "snapshots");
        Assert.AreEqual(Samples.Base, tallies.Agents[0].FirstAt, "first capture time");
        Assert.AreEqual(Samples.Base.AddSeconds(60), tallies.Agents[0].LastAt, "last capture time");
        Assert.AreEqual(1L, tallies.Agents[0].Starts, "starts");
        Assert.AreEqual(1L, tallies.Agents[0].ComparedAll, "compared on all ports");
        Assert.AreEqual(0L, tallies.Agents[0].ComparedPrivileged, "compared on privileged ports");
        Assert.AreEqual(0L, tallies.Agents[0].NotCompared, "not compared");
        Assert.AreEqual(1L, starts.Total, "start findings");
        Assert.HasCount(1, starts.Findings, "listed start findings");

        var finding = starts.Findings[0];

        Assert.AreEqual(ListenerFindingKind.AgentStart, finding.Kind, "kind");
        Assert.AreEqual(Agent, finding.AgentId, "finding agent ID");
        Assert.AreEqual(2UL, finding.Seq, "finding sequence number");
        Assert.AreEqual(Samples.Base.AddSeconds(60), finding.CapturedAt, "capture time of the start");
        Assert.AreEqual(Samples.Base, finding.BaseCapturedAt, "capture time of the base");
        Assert.AreEqual(ListenerOutcome.AllPorts, finding.Outcome, "outcome");
        Assert.AreEqual("tcp 0.0.0.0:23", ListenerSamples.Joined(finding), "new listeners");
        Assert.AreEqual(200, finding.Listeners[0].Pid, "owner PID from the snapshot after the start");
        Assert.AreEqual("telnetd", finding.Listeners[0].Command, "owner command from the snapshot after the start");
        Assert.AreEqual(0L, finding.Omitted, "omitted");
    }

    /// <summary>
    /// Two stored start snapshots back to back: the second is compared with the first, and the tally holds two starts.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreListenerFindingsComparesSecondStartWithTheFirst()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var first = ListenerSamples.Snapshot("start", "all", ListenerSamples.Tcp("0.0.0.0", 22));

        // Act
        await store.WriteBatchAsync(ListenerSamples.Batch(Agent, ListenerSamples.Record(1, 0, first), StartRecord(2, 60)), TestContext.CancellationToken);

        var tallies = await store.ListenerTalliesAsync(TestContext.CancellationToken);
        var starts = await ReadAsync(store, ListenerFindingKind.AgentStart);

        // Assert
        Assert.AreEqual(2L, tallies.Agents[0].Starts, "two starts");
        Assert.AreEqual(1L, tallies.Agents[0].NotCompared, "the first start has no earlier inventory");
        Assert.AreEqual(1L, tallies.Agents[0].ComparedAll, "the second start is compared on all ports");
        Assert.AreEqual(2L, starts.Total, "both starts are findings");
        Assert.HasCount(2, starts.Findings, "listed start findings");
        Assert.AreEqual(2UL, starts.Findings[0].Seq, "the latest first");
        Assert.AreEqual(ListenerOutcome.AllPorts, starts.Findings[0].Outcome, "second start outcome");
        Assert.AreEqual(Samples.Base, starts.Findings[0].BaseCapturedAt, "the second start is compared with the first");
        Assert.AreEqual("tcp 0.0.0.0:23", ListenerSamples.Joined(starts.Findings[0]), "second start listeners");
        Assert.AreEqual(ListenerOutcome.NoEarlierInventory, starts.Findings[1].Outcome, "first start outcome");
        Assert.IsNull(starts.Findings[1].BaseCapturedAt, "the first start has no base");
        Assert.IsEmpty(starts.Findings[1].Listeners, "the first start lists nothing");
    }

    /// <summary>
    /// A start after a gap in the sequence numbers, with no snapshot of the stopped time, is one start compared with the last snapshot before it.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreListenerFindingsComparesStartAfterSeqGapWithTheBase()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        // Act
        await store.WriteBatchAsync(ListenerSamples.Batch(Agent, BaseRecord(1, 0), StartRecord(5, 600)), TestContext.CancellationToken);

        var tallies = await store.ListenerTalliesAsync(TestContext.CancellationToken);
        var starts = await ReadAsync(store, ListenerFindingKind.AgentStart);

        // Assert
        Assert.AreEqual(1L, tallies.Agents[0].Starts, "one start");
        Assert.AreEqual(1L, tallies.Agents[0].ComparedAll, "compared with the base");
        Assert.HasCount(1, starts.Findings, "one finding");
        Assert.AreEqual(Samples.Base, starts.Findings[0].BaseCapturedAt, "the base is the snapshot before the gap");
        Assert.AreEqual("tcp 0.0.0.0:23", ListenerSamples.Joined(starts.Findings[0]), "new listeners");
    }

    /// <summary>
    /// Two agents with interleaved sequence numbers are compared only within each agent, and each finding names its agent.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreListenerFindingsComparesInterleavedAgentsOnlyWithTheirOwn()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var startA = ListenerSamples.Snapshot("start", "all", ListenerSamples.Tcp("0.0.0.0", 22), ListenerSamples.Tcp("0.0.0.0", 23));
        var startB = ListenerSamples.Snapshot("start", "all", ListenerSamples.Tcp("0.0.0.0", 80), ListenerSamples.Tcp("0.0.0.0", 25));

        // Act
        await store.WriteBatchAsync(ListenerSamples.Batch("agent-a", ListenerSamples.Record(1, 0, ListenerSamples.Snapshot("none", "all", ListenerSamples.Tcp("0.0.0.0", 22))), ListenerSamples.Record(3, 120, startA)), TestContext.CancellationToken);
        await store.WriteBatchAsync(ListenerSamples.Batch("agent-b", ListenerSamples.Record(2, 60, ListenerSamples.Snapshot("none", "all", ListenerSamples.Tcp("0.0.0.0", 80))), ListenerSamples.Record(4, 180, startB)), TestContext.CancellationToken);

        var tallies = await store.ListenerTalliesAsync(TestContext.CancellationToken);
        var starts = await ReadAsync(store, ListenerFindingKind.AgentStart);
        var findingA = starts.Findings.Single(finding => finding.AgentId == "agent-a");
        var findingB = starts.Findings.Single(finding => finding.AgentId == "agent-b");

        // Assert
        Assert.HasCount(2, tallies.Agents, "two agents");
        Assert.AreEqual("agent-a", tallies.Agents[0].AgentId, "the lowest agent ID first");
        Assert.AreEqual(1L, tallies.Agents[0].Starts, "starts of agent a");
        Assert.AreEqual(1L, tallies.Agents[1].Starts, "starts of agent b");
        Assert.AreEqual(2L, starts.Total, "two findings");
        Assert.AreEqual("tcp 0.0.0.0:23", ListenerSamples.Joined(findingA), "new listeners of agent a");
        Assert.AreEqual(Samples.Base, findingA.BaseCapturedAt, "base of agent a");
        Assert.AreEqual("tcp 0.0.0.0:25", ListenerSamples.Joined(findingB), "new listeners of agent b");
        Assert.AreEqual(Samples.Base.AddSeconds(60), findingB.BaseCapturedAt, "base of agent b");
    }

    /// <summary>
    /// A compared start without a new listener writes no finding but counts in the tally.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreListenerFindingsComparedWithoutNewListenerIsNoFinding()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var same = ListenerSamples.Snapshot("start", "all", ListenerSamples.Tcp("0.0.0.0", 22, 999, "other-owner"));

        // Act
        await store.WriteBatchAsync(ListenerSamples.Batch(Agent, BaseRecord(1, 0), ListenerSamples.Record(2, 60, same)), TestContext.CancellationToken);

        var tallies = await store.ListenerTalliesAsync(TestContext.CancellationToken);
        var starts = await ReadAsync(store, ListenerFindingKind.AgentStart);

        // Assert
        Assert.AreEqual(1L, tallies.Agents[0].Starts, "the start is counted");
        Assert.AreEqual(1L, tallies.Agents[0].ComparedAll, "the start is counted as compared on all ports");
        Assert.AreEqual(0L, starts.Total, "no finding, also not for an owner change");
        Assert.IsEmpty(starts.Findings, "no listed finding");
    }

    /// <summary>
    /// A snapshot of the agent's own check lists its new listeners with their owner, the omitted count and the ports the agent compared, and a snapshot without new listeners is no finding.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreListenerFindingsKeepsAgentFindings()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var privileged = ListenerSamples.Found("privileged", [ListenerSamples.Tcp("0.0.0.0", 23, 7, "telnetd"), ListenerSamples.Listener(Proto.Udp, "0.0.0.0", 69, 8, "tftpd")], 3);
        var many = Enumerable.Range(0, 4096).Select(index => ListenerSamples.Tcp($"10.0.{index / 256}.{index % 256}", 1000)).ToList();

        // Act
        await store.WriteBatchAsync(ListenerSamples.Batch(Agent, ListenerSamples.Record(1, 0, privileged), ListenerSamples.Record(2, 60, ListenerSamples.Found("all", [], 0))), TestContext.CancellationToken);

        var first = await ReadAsync(store, ListenerFindingKind.Agent);

        await store.WriteBatchAsync(ListenerSamples.Batch(Agent, ListenerSamples.Record(3, 120, ListenerSamples.Found("all", many, 5))), TestContext.CancellationToken);

        var second = await ReadAsync(store, ListenerFindingKind.Agent);

        // Assert
        Assert.AreEqual(1L, first.Total, "one agent finding of two snapshots");
        Assert.HasCount(1, first.Findings, "listed agent findings");

        var finding = first.Findings[0];

        Assert.AreEqual(ListenerFindingKind.Agent, finding.Kind, "kind");
        Assert.AreEqual(Agent, finding.AgentId, "agent ID");
        Assert.AreEqual(1UL, finding.Seq, "sequence number");
        Assert.AreEqual(Samples.Base, finding.CapturedAt, "capture time");
        Assert.IsNull(finding.BaseCapturedAt, "an agent finding has no base");
        Assert.AreEqual(ListenerOutcome.PrivilegedOnly, finding.Outcome, "the ports the agent compared");
        Assert.AreEqual("tcp 0.0.0.0:23,udp 0.0.0.0:69", ListenerSamples.Joined(finding), "the agent's own list");
        Assert.AreEqual(7, finding.Listeners[0].Pid, "owner PID");
        Assert.AreEqual("telnetd", finding.Listeners[0].Command, "owner command");
        Assert.AreEqual(3L, finding.Omitted, "omitted count");

        Assert.AreEqual(2L, second.Total, "the snapshot with 4,096 new listeners is a second finding");
        Assert.AreEqual(3UL, second.Findings[0].Seq, "the latest first");
        Assert.AreEqual(ListenerOutcome.AllPorts, second.Findings[0].Outcome, "outcome of the check all");
        Assert.HasCount(64, second.Findings[0].Listeners, "the stored list is cut to 64");
        Assert.AreEqual("10.0.0.0:1000", second.Findings[0].Listeners[0].Local?.ToString(), "the first of the agent's list is kept");
        Assert.AreEqual("10.0.0.63:1000", second.Findings[0].Listeners[63].Local?.ToString(), "the 64th of the agent's list is the last kept");
        Assert.AreEqual(4037L, second.Findings[0].Omitted, "4,032 cut plus the agent's 5");
    }

    /// <summary>
    /// The same records stored in any order and any number of times give the same tally and the same finding.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreListenerFindingsSameForAnyStoreOrder()
    {
        // Arrange
        var rows = new Dictionary<string, int[][]>
                   {
                       ["base and start in one batch"] = [[1, 2, 3]],
                       ["start first in the batch"] = [[3, 1, 2]],
                       ["start in one batch, its base in a later one"] = [[3], [1, 2]],
                       ["older base, then the start, then the base between them"] = [[1], [3], [2]],
                       ["the same batch written twice"] = [[1, 2, 3], [1, 2, 3]]
                   };

        foreach (var row in rows)
        {
            using var directory = new TempDirectory();
            await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

            // Act
            foreach (var batch in row.Value)
            {
                await store.WriteBatchAsync(ListenerSamples.Batch(Agent, [.. batch.Select(Ordered)]), TestContext.CancellationToken);
            }

            var tallies = await store.ListenerTalliesAsync(TestContext.CancellationToken);
            var starts = await ReadAsync(store, ListenerFindingKind.AgentStart);

            // Assert
            Assert.HasCount(1, tallies.Agents, $"{row.Key}: agents");
            Assert.AreEqual(3L, tallies.Agents[0].Snapshots, $"{row.Key}: snapshots");
            Assert.AreEqual(Samples.Base, tallies.Agents[0].FirstAt, $"{row.Key}: first capture time");
            Assert.AreEqual(Samples.Base.AddSeconds(120), tallies.Agents[0].LastAt, $"{row.Key}: last capture time");
            Assert.AreEqual(1L, tallies.Agents[0].Starts, $"{row.Key}: starts");
            Assert.AreEqual(1L, tallies.Agents[0].ComparedAll, $"{row.Key}: compared on all ports");
            Assert.AreEqual(1L, starts.Total, $"{row.Key}: start findings");
            Assert.HasCount(1, starts.Findings, $"{row.Key}: listed start findings");
            Assert.AreEqual(Samples.Base.AddSeconds(60), starts.Findings[0].BaseCapturedAt, $"{row.Key}: the base is the snapshot with the next lower seq");
            Assert.AreEqual("tcp 0.0.0.0:25", ListenerSamples.Joined(starts.Findings[0]), $"{row.Key}: new listeners");
        }
    }

    /// <summary>
    /// Findings of two agents with the same capture time at the cap of 20 give the same page whichever agent was stored first.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreListenerFindingsSameAtTheCapForEitherOrder()
    {
        // Arrange
        var pages = new List<string>();

        foreach (var order in new[] { new[] { "agent-a", "agent-b" }, new[] { "agent-b", "agent-a" } })
        {
            using var directory = new TempDirectory();
            await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

            // Act
            foreach (var agent in order)
            {
                var records = Enumerable.Range(1, 11)
                                        .Select(index => ListenerSamples.Record((ulong)index, 10, ListenerSamples.Found("all", [ListenerSamples.Tcp("10.0.0.1", 1000 + index)], 0)))
                                        .ToArray();

                await store.WriteBatchAsync(ListenerSamples.Batch(agent, records), TestContext.CancellationToken);
            }

            var page = await ReadAsync(store, ListenerFindingKind.Agent);

            // Assert
            Assert.AreEqual(22L, page.Total, $"{string.Join(",", order)}: total");
            Assert.HasCount(20, page.Findings, $"{string.Join(",", order)}: the cap");
            pages.Add(ListenerSamples.Describe(page));
        }

        Assert.AreEqual(pages[0], pages[1], "the same page for either store order");
    }

    /// <summary>
    /// A stored base whose payload does not decode makes its start not comparable, and the batch is kept.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreListenerWriteUndecodableBaseIsNotComparable()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await store.WriteBatchAsync(ListenerSamples.Batch(Agent, BaseRecord(1, 0)), TestContext.CancellationToken);
        await ExecuteAsync(directory, "UPDATE records SET data = 'this is not json' WHERE seq = 1");

        // Act
        var result = await store.WriteBatchAsync(ListenerSamples.Batch(Agent, StartRecord(2, 60)), TestContext.CancellationToken);
        var starts = await ReadAsync(store, ListenerFindingKind.AgentStart);
        var tallies = await store.ListenerTalliesAsync(TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(new WriteResult(1, 0), result, "the batch is stored");
        Assert.AreEqual(1L, starts.Total, "one start finding");
        Assert.AreEqual(ListenerOutcome.NotComparable, starts.Findings[0].Outcome, "outcome");
        Assert.IsEmpty(starts.Findings[0].Listeners, "no listeners");
        Assert.AreEqual(1L, tallies.Agents[0].NotCompared, "the start counts as not compared");
    }

    /// <summary>
    /// A stored base whose data is NULL makes its start not comparable, and the batch is kept.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreListenerWriteNullBaseDataIsNotComparable()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await store.WriteBatchAsync(ListenerSamples.Batch(Agent, BaseRecord(1, 0)), TestContext.CancellationToken);
        await ExecuteAsync(directory, "UPDATE records SET data = NULL WHERE seq = 1");

        // Act
        var result = await store.WriteBatchAsync(ListenerSamples.Batch(Agent, StartRecord(2, 60)), TestContext.CancellationToken);
        var starts = await ReadAsync(store, ListenerFindingKind.AgentStart);

        // Assert
        Assert.AreEqual(new WriteResult(1, 0), result, "the batch is stored");
        Assert.AreEqual(ListenerOutcome.NotComparable, starts.Findings[0].Outcome, "outcome");
        Assert.IsEmpty(starts.Findings[0].Listeners, "no listeners");
    }

    /// <summary>
    /// A start whose base inventory is cut is incomplete, lists nothing, and reads back as not compared.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreListenerFindingsStartWithCutBaseIsPrivilegedIncomplete()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var cut = ListenerSamples.Record(1, 0, ListenerSamples.Snapshot("none", "cut", ListenerSamples.Tcp("0.0.0.0", 22)));

        // Act
        await store.WriteBatchAsync(ListenerSamples.Batch(Agent, cut, StartRecord(2, 60)), TestContext.CancellationToken);

        var tallies = await store.ListenerTalliesAsync(TestContext.CancellationToken);
        var starts = await ReadAsync(store, ListenerFindingKind.AgentStart);

        // Assert
        Assert.AreEqual(ListenerOutcome.PrivilegedIncomplete, starts.Findings[0].Outcome, "outcome");
        Assert.IsEmpty(starts.Findings[0].Listeners, "no listeners");
        Assert.AreEqual(1L, tallies.Agents[0].NotCompared, "the second start is not compared");
        Assert.AreEqual(0L, tallies.Agents[0].ComparedAll, "not compared on all ports");
        Assert.AreEqual(0L, tallies.Agents[0].ComparedPrivileged, "not compared on privileged ports");
    }

    /// <summary>
    /// A start compared with a base that lists only privileged ports keeps the new privileged listener only, and counts as compared on privileged ports.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreListenerFindingsStartWithPrivilegedBaseIsPrivilegedOnly()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var earlier = ListenerSamples.Record(1, 0, ListenerSamples.Snapshot("none", "privileged", ListenerSamples.Tcp("0.0.0.0", 22)));
        var start = ListenerSamples.Record(2, 60, ListenerSamples.Snapshot("start", "all", ListenerSamples.Tcp("0.0.0.0", 22), ListenerSamples.Tcp("0.0.0.0", 23), ListenerSamples.Tcp("0.0.0.0", 8080)));

        // Act
        await store.WriteBatchAsync(ListenerSamples.Batch(Agent, earlier, start), TestContext.CancellationToken);

        var tallies = await store.ListenerTalliesAsync(TestContext.CancellationToken);
        var starts = await ReadAsync(store, ListenerFindingKind.AgentStart);

        // Assert
        Assert.AreEqual(ListenerOutcome.PrivilegedOnly, starts.Findings[0].Outcome, "outcome");
        Assert.AreEqual("tcp 0.0.0.0:23", ListenerSamples.Joined(starts.Findings[0]), "only the privileged listener");
        Assert.AreEqual(1L, tallies.Agents[0].ComparedPrivileged, "compared on privileged ports");
        Assert.AreEqual(0L, tallies.Agents[0].ComparedAll, "not compared on all ports");
    }

    /// <summary>
    /// A stored listener list that does not decode fails the read with a store error that names the step.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreListenerFindingsUndecodableStoredListenersThrowStoreException()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await store.WriteBatchAsync(ListenerSamples.Batch(Agent, BaseRecord(1, 0), StartRecord(2, 60)), TestContext.CancellationToken);
        await ExecuteAsync(directory, "UPDATE listener_comparisons SET new_listeners = 'this is not json'");

        // Act
        var exception = await Assert.ThrowsExactlyAsync<StoreException>(() => ReadAsync(store, ListenerFindingKind.AgentStart));

        // Assert
        Assert.Contains("decoding listeners", exception.Message, "the failing step");
    }

    /// <summary>
    /// A finding query with a defined kind and a limit from 1 to the maximum is accepted.
    /// </summary>
    /// <param name="kind">The kind</param>
    /// <param name="limit">The limit</param>
    [TestMethod]
    [DataRow(ListenerFindingKind.Agent, 1)]
    [DataRow(ListenerFindingKind.AgentStart, ListenerReport.MaxFindings)]
    public void ListenerQueriesValidateAcceptsTheLimitBounds(ListenerFindingKind kind, int limit)
    {
        // Arrange
        var query = new ListenerFindingQuery
                    {
                        Kind = kind,
                        Limit = limit
                    };

        // Act
        ListenerQueries.Validate(query);

        // Assert
        Assert.AreEqual(limit, query.Limit, "the query is accepted unchanged");
    }

    /// <summary>
    /// A finding query with a limit of 0 or above the maximum, or an undefined kind, is refused.
    /// </summary>
    /// <param name="kind">The kind value</param>
    /// <param name="limit">The limit</param>
    [TestMethod]
    [DataRow(0, 0)]
    [DataRow(1, 21)]
    [DataRow(0, -1)]
    [DataRow(99, 5)]
    public void ListenerQueriesValidateRefusesBrokenQuery(int kind, int limit)
    {
        // Arrange
        var query = new ListenerFindingQuery
                    {
                        Kind = (ListenerFindingKind)kind,
                        Limit = limit
                    };

        // Act & Assert
        Assert.ThrowsExactly<InvalidQueryException>(() => ListenerQueries.Validate(query), "the query is refused");
    }

    /// <summary>
    /// Two snapshots at the wire's limits are stored and compared without a decoding error.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreListenerWriteMaximalSnapshotRoundTrips()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var command = new string('a', 1024);
        var listeners = Enumerable.Range(0, 4096).Select(index => ListenerSamples.Tcp($"10.0.{index / 256}.{index % 256}", 2000, index + 1, command)).ToArray();
        var later = listeners.Take(4095).Append(ListenerSamples.Tcp("192.168.0.1", 2000, 7, command)).ToArray();

        // Act
        var first = await store.WriteBatchAsync(ListenerSamples.Batch(Agent, ListenerSamples.Record(1, 0, ListenerSamples.Snapshot("none", "all", listeners))), TestContext.CancellationToken);
        var second = await store.WriteBatchAsync(ListenerSamples.Batch(Agent, ListenerSamples.Record(2, 60, ListenerSamples.Snapshot("start", "all", later))), TestContext.CancellationToken);
        var starts = await ReadAsync(store, ListenerFindingKind.AgentStart);

        // Assert
        Assert.AreEqual(new WriteResult(1, 0), first, "the base is stored");
        Assert.AreEqual(new WriteResult(1, 0), second, "the start is stored");
        Assert.AreEqual(1L, starts.Total, "one finding");
        Assert.AreEqual(ListenerOutcome.AllPorts, starts.Findings[0].Outcome, "the stored payloads decode and compare on all ports");
        Assert.AreEqual("tcp 192.168.0.1:2000", ListenerSamples.Joined(starts.Findings[0]), "the one new listener");
        Assert.AreEqual(command, starts.Findings[0].Listeners[0].Command, "the command of the owner");
    }

    /// <summary>
    /// A start captured before its base because the clock stepped back is still compared with that base, which the sequence number orders.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreListenerFindingsOrderBySeqWhenTheClockStepsBack()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        // Act
        await store.WriteBatchAsync(ListenerSamples.Batch(Agent, BaseRecord(1, 3600), StartRecord(2, 0)), TestContext.CancellationToken);

        var starts = await ReadAsync(store, ListenerFindingKind.AgentStart);

        // Assert
        Assert.AreEqual(1L, starts.Total, "one finding");
        Assert.AreEqual(ListenerOutcome.AllPorts, starts.Findings[0].Outcome, "compared although the capture time is earlier");
        Assert.AreEqual(Samples.Base, starts.Findings[0].CapturedAt, "the capture time of the start as stored");
        Assert.AreEqual(Samples.Base.AddSeconds(3600), starts.Findings[0].BaseCapturedAt, "the capture time of the base as stored");
        Assert.AreEqual("tcp 0.0.0.0:23", ListenerSamples.Joined(starts.Findings[0]), "new listeners");
    }

    /// <summary>
    /// Snapshots of another origin or source, or without the 1.1 fields, are no side of a comparison, no finding and not counted.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreListenerFindingsIgnoresOtherSnapshots()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var noCheck = ListenerSamples.Snapshot("none", "all", ListenerSamples.Tcp("0.0.0.0", 22));
        var noInventory = ListenerSamples.Snapshot("none", "all", ListenerSamples.Tcp("0.0.0.0", 22));
        var noTables = ListenerSamples.Snapshot("none", "all", ListenerSamples.Tcp("0.0.0.0", 22));
        var found = ListenerSamples.Found("all", [ListenerSamples.Tcp("0.0.0.0", 23)], 0);
        var imported = ListenerSamples.Snapshot("none", "all", ListenerSamples.Tcp("0.0.0.0", 22));

        noCheck.ListenerCheck = null;
        noInventory.Inventory = null;
        noTables.Tables = null;
        found.Tables = null;
        imported.AggregatesOnly = false;

        var importRecord = ListenerSamples.Record(1, 0, imported);

        importRecord.Origin = RecordOrigin.Import;
        importRecord.Source = "lsof";
        importRecord.Seq = 0;

        var otherSource = ListenerSamples.Record(5, 40, ListenerSamples.Snapshot("start", "all", ListenerSamples.Tcp("0.0.0.0", 22), ListenerSamples.Tcp("0.0.0.0", 23)));

        otherSource.Source = "netstat";

        // Act
        await store.WriteBatchAsync(Samples.Batch(importRecord), TestContext.CancellationToken);
        await store.WriteBatchAsync(ListenerSamples.Batch(Agent, ListenerSamples.Record(1, 10, noCheck), ListenerSamples.Record(2, 20, noInventory), ListenerSamples.Record(3, 30, noTables), ListenerSamples.Record(4, 35, found), otherSource), TestContext.CancellationToken);

        var tallies = await store.ListenerTalliesAsync(TestContext.CancellationToken);
        var starts = await ReadAsync(store, ListenerFindingKind.AgentStart);
        var agents = await ReadAsync(store, ListenerFindingKind.Agent);

        await store.WriteBatchAsync(ListenerSamples.Batch(Agent, StartRecord(6, 60)), TestContext.CancellationToken);

        var afterwards = await ReadAsync(store, ListenerFindingKind.AgentStart);

        // Assert
        Assert.IsEmpty(tallies.Agents, "no agent is counted");
        Assert.AreEqual(0L, starts.Total, "no start finding");
        Assert.AreEqual(0L, agents.Total, "no agent finding, also not for a snapshot without tables");
        Assert.AreEqual(1L, afterwards.Total, "a later usable start is a finding");
        Assert.AreEqual(ListenerOutcome.NoEarlierInventory, afterwards.Findings[0].Outcome, "no unusable snapshot was a side of the comparison");
        Assert.IsNull(afterwards.Findings[0].BaseCapturedAt, "no base");
    }

    /// <summary>
    /// The tallies list the 64 lowest agent IDs and count the one that is left out.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreListenerTalliesListsAtMost64Agents()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        for (var index = 64; index >= 0; index--)
        {
            await store.WriteBatchAsync(ListenerSamples.Batch($"agent-{index:D2}", BaseRecord(1, 0)), TestContext.CancellationToken);
        }

        // Act
        var tallies = await store.ListenerTalliesAsync(TestContext.CancellationToken);

        // Assert
        Assert.HasCount(64, tallies.Agents, "the cap");
        Assert.AreEqual(1L, tallies.Omitted, "one agent ID left out");
        Assert.AreEqual("agent-00", tallies.Agents[0].AgentId, "the lowest first");
        Assert.AreEqual("agent-63", tallies.Agents[63].AgentId, "the 64th lowest last");
    }

    /// <summary>
    /// A thousand agent findings never push out a start finding: each kind has its own page and total.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreListenerFindingsCapsEachKindSeparately()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var records = Enumerable.Range(0, 1000)
                                .Select(index => ListenerSamples.Record((ulong)(3 + index), 100 + index, ListenerSamples.Found("all", [ListenerSamples.Tcp("10.0.0.1", 1000 + index)], 0)))
                                .ToArray();

        await store.WriteBatchAsync(ListenerSamples.Batch(Agent, BaseRecord(1, 0), StartRecord(2, 60)), TestContext.CancellationToken);
        await store.WriteBatchAsync(ListenerSamples.Batch(Agent, records), TestContext.CancellationToken);

        // Act
        var starts = await ReadAsync(store, ListenerFindingKind.AgentStart);
        var agents = await ReadAsync(store, ListenerFindingKind.Agent);

        // Assert
        Assert.AreEqual(1L, starts.Total, "one start finding");
        Assert.HasCount(1, starts.Findings, "the start finding is listed");
        Assert.AreEqual("tcp 0.0.0.0:23", ListenerSamples.Joined(starts.Findings[0]), "tcp 0.0.0.0:23");
        Assert.AreEqual(1000L, agents.Total, "agent findings");
        Assert.HasCount(20, agents.Findings, "the latest 20 agent findings");
        Assert.AreEqual(1002UL, agents.Findings[0].Seq, "the latest agent finding first");
        Assert.AreEqual(983UL, agents.Findings[19].Seq, "the 20th latest agent finding last");
    }

    /// <summary>
    /// The reads use the derived tables only: with every stored payload replaced the tallies and both pages are unchanged.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreListenerFindingsReadsNoPayload()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await store.WriteBatchAsync(ListenerSamples.Batch(Agent, BaseRecord(1, 0), StartRecord(2, 60), ListenerSamples.Record(3, 120, ListenerSamples.Found("privileged", [ListenerSamples.Tcp("0.0.0.0", 24, 5, "x")], 2))), TestContext.CancellationToken);

        var tallyBefore = ListenerSamples.Describe(await store.ListenerTalliesAsync(TestContext.CancellationToken));
        var startBefore = ListenerSamples.Describe(await ReadAsync(store, ListenerFindingKind.AgentStart));
        var agentBefore = ListenerSamples.Describe(await ReadAsync(store, ListenerFindingKind.Agent));

        // Act
        await ExecuteAsync(directory, $"UPDATE records SET data = '{{}}' WHERE kind = '{RecordKind.ConnectionSnapshot}'");

        var tallyAfter = ListenerSamples.Describe(await store.ListenerTalliesAsync(TestContext.CancellationToken));
        var startAfter = ListenerSamples.Describe(await ReadAsync(store, ListenerFindingKind.AgentStart));
        var agentAfter = ListenerSamples.Describe(await ReadAsync(store, ListenerFindingKind.Agent));

        // Assert
        Assert.Contains("total 1|", startBefore + "|", "the start finding exists");
        Assert.Contains("total 1|", agentBefore + "|", "the agent finding exists");
        Assert.AreEqual(tallyBefore, tallyAfter, "tallies");
        Assert.AreEqual(startBefore, startAfter, "start findings");
        Assert.AreEqual(agentBefore, agentAfter, "agent findings");
    }

    /// <summary>
    /// Creates the base snapshot: one listener, port 22.
    /// </summary>
    /// <param name="seq">The sequence number</param>
    /// <param name="seconds">Seconds after <see cref="Samples.Base"/></param>
    /// <returns>The record</returns>
    private static DataRecord BaseRecord(ulong seq, int seconds)
    {
        return ListenerSamples.Record(seq, seconds, ListenerSamples.Snapshot("none", "all", ListenerSamples.Tcp("0.0.0.0", 22, 100, "sshd")));
    }

    /// <summary>
    /// Creates the start snapshot: the listener of the base and a new one on port 23.
    /// </summary>
    /// <param name="seq">The sequence number</param>
    /// <param name="seconds">Seconds after <see cref="Samples.Base"/></param>
    /// <returns>The record</returns>
    private static DataRecord StartRecord(ulong seq, int seconds)
    {
        return ListenerSamples.Record(seq, seconds, ListenerSamples.Snapshot("start", "all", ListenerSamples.Tcp("0.0.0.0", 22, 100, "sshd"), ListenerSamples.Tcp("0.0.0.0", 23, 200, "telnetd")));
    }

    /// <summary>
    /// Creates one of the three records of the order tests: a base, a base between, and a start that adds port 25 to the second base.
    /// </summary>
    /// <param name="seq">1 for the first base, 2 for the second base, 3 for the start</param>
    /// <returns>The record</returns>
    private static DataRecord Ordered(int seq)
    {
        return seq switch
               {
                   1 => ListenerSamples.Record(1, 0, ListenerSamples.Snapshot("none", "all", ListenerSamples.Tcp("0.0.0.0", 22))),
                   2 => ListenerSamples.Record(2, 60, ListenerSamples.Snapshot("none", "all", ListenerSamples.Tcp("0.0.0.0", 22), ListenerSamples.Tcp("0.0.0.0", 23))),
                   _ => ListenerSamples.Record(3, 120, ListenerSamples.Snapshot("start", "all", ListenerSamples.Tcp("0.0.0.0", 22), ListenerSamples.Tcp("0.0.0.0", 23), ListenerSamples.Tcp("0.0.0.0", 25)))
               };
    }

    /// <summary>
    /// Reads the latest 20 findings of a kind.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="kind">The kind</param>
    /// <returns>A task that returns the page</returns>
    private Task<ListenerFindingPage> ReadAsync(SqliteStore store, ListenerFindingKind kind)
    {
        return store.ListenerFindingsAsync(new ListenerFindingQuery
                                           {
                                               Kind = kind,
                                               Limit = ListenerReport.MaxFindings
                                           },
                                           TestContext.CancellationToken);
    }

    /// <summary>
    /// Runs statements on the database file of a directory.
    /// </summary>
    /// <param name="directory">The directory of the store</param>
    /// <param name="sql">The statements</param>
    /// <returns>A task that completes when the statements ran</returns>
    private async Task ExecuteAsync(TempDirectory directory, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(directory.Path, StorageLimits.FileName)};Pooling=False");

        await connection.OpenAsync(TestContext.CancellationToken);

        using var command = connection.CreateCommand();

        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(TestContext.CancellationToken);
    }

    #endregion // Methods
}