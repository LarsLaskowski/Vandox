using System.Net;
using System.Text.Json.Nodes;

using Microsoft.Data.Sqlite;

using Vandox.Core.Model;
using Vandox.Core.Reports;

namespace Vandox.Storage.Tests;

/// <summary>
/// Tests for <see cref="ListenerMarkWriter"/>
/// </summary>
[TestClass]
public class ListenerMarkWriterTests
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
    /// A backfill of any size below one stored start decodes the start and its predecessor once per batch, two payloads in all.
    /// </summary>
    /// <param name="backfill">The number of snapshots written below the stored start</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow(10)]
    [DataRow(1000)]
    public async Task ListenerMarkWriterDecodesEachStartOncePerBatch(int backfill)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var start = ListenerSamples.Snapshot("start", "all", ListenerSamples.Tcp("0.0.0.0", 22), ListenerSamples.Tcp("0.0.0.0", 23));

        await store.WriteBatchAsync(ListenerSamples.Batch(Agent, ListenerSamples.Record(5000, 100000, start)), TestContext.CancellationToken);

        await using var connection = new SqliteConnection($"Data Source={Path.Combine(directory.Path, StorageLimits.FileName)};Pooling=False");

        await connection.OpenAsync(TestContext.CancellationToken);

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(TestContext.CancellationToken);
        using var writer = new ListenerMarkWriter(connection, transaction);

        // Act
        for (var index = 1; index <= backfill; index++)
        {
            var record = ListenerSamples.Record((ulong)index, index, ListenerSamples.Snapshot("none", "all", ListenerSamples.Tcp("0.0.0.0", 22)));
            var id = await InsertAsync(connection, transaction, record);

            writer.Write(id, Agent, record, (ConnectionSnapshot)record.Data!);
        }

        writer.WriteComparisons(TestContext.CancellationToken);

        await transaction.CommitAsync(TestContext.CancellationToken);

        var starts = await store.ListenerFindingsAsync(new ListenerFindingQuery
                                                       {
                                                           Kind = ListenerFindingKind.AgentStart,
                                                           Limit = ListenerReport.MaxFindings
                                                       },
                                                       TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(2L, writer.PayloadDecodes, "the start and its predecessor are decoded once for the whole batch");
        Assert.AreEqual(1L, starts.Total, "the start is compared with the newest backfilled snapshot");
        Assert.AreEqual(Samples.Base.AddSeconds(backfill), starts.Findings[0].BaseCapturedAt, "the base is the snapshot with the highest seq below the start");
        Assert.AreEqual(ListenerOutcome.AllPorts, starts.Findings[0].Outcome, "outcome");
    }

    /// <summary>
    /// The new listeners an agent reports and the new listeners of a start comparison are stored with their UID (also 0), user and mixed-UIDs flag.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ListenerMarkWriterKeepsTheSocketUidInStoredNewListeners()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var baseline = ListenerSamples.Snapshot("none", "all", ListenerSamples.Tcp("0.0.0.0", 22));
        var found = ListenerSamples.Found("all",
                                          [
                                              new Listener
                                              {
                                                  Proto = Proto.Tcp,
                                                  Local = new IPEndPoint(IPAddress.Parse("0.0.0.0"), 23),
                                                  Pid = 7,
                                                  Command = "telnetd",
                                                  Uid = 0,
                                                  User = "root"
                                              }
                                          ],
                                          0);
        var start = ListenerSamples.Snapshot("start",
                                             "all",
                                             ListenerSamples.Tcp("0.0.0.0", 22),
                                             new Listener
                                             {
                                                 Proto = Proto.Udp,
                                                 Local = new IPEndPoint(IPAddress.Parse("0.0.0.0"), 5353),
                                                 Uid = 105,
                                                 User = "avahi",
                                                 MixedUids = true
                                             });

        // Act
        await store.WriteBatchAsync(ListenerSamples.Batch(Agent, ListenerSamples.Record(1, 0, baseline), ListenerSamples.Record(2, 60, found), ListenerSamples.Record(3, 120, start)), TestContext.CancellationToken);

        var marks = JsonNode.Parse(await ReadScalarAsync(directory, "SELECT new_listeners FROM connection_marks WHERE seq = 2"))!.AsArray();
        var comparisons = JsonNode.Parse(await ReadScalarAsync(directory, "SELECT new_listeners FROM listener_comparisons WHERE seq = 3"))!.AsArray();

        // Assert
        Assert.AreEqual(0, (int)marks[0]!["uid"]!, "connection_marks: uid 0 is stored");
        Assert.AreEqual("root", (string)marks[0]!["user"]!, "connection_marks: user");
        Assert.AreEqual(105, (int)comparisons[0]!["uid"]!, "listener_comparisons: uid");
        Assert.AreEqual("avahi", (string)comparisons[0]!["user"]!, "listener_comparisons: user");
        Assert.IsTrue((bool)comparisons[0]!["mixed_uids"]!, "listener_comparisons: mixed_uids");
    }

    /// <summary>
    /// A start whose socket UID changed against its base stores the changes with their count and is a finding without a new listener, a start without a change is no finding and a start that was not compared is one without changes.
    /// </summary>
    /// <param name="baseUid">The socket UID in the base snapshot</param>
    /// <param name="startUid">The socket UID in the start snapshot</param>
    /// <param name="baseInventory">The inventory state of the base snapshot</param>
    /// <param name="expectedChanged">The expected stored change count</param>
    /// <param name="expectedFinding">The expected finding flag</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow(110u, 1001u, "all", "1", "1")]
    [DataRow(110u, 110u, "all", "0", "0")]
    [DataRow(110u, 1001u, "skipped", "0", "1")]
    public async Task ListenerMarkWriterStoresChangesOfAStart(uint baseUid, uint startUid, string baseInventory, string expectedChanged, string expectedFinding)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var baseline = ListenerSamples.Snapshot("none", baseInventory, ListenerSamples.Owned("0.0.0.0", 3306, baseUid, "mysql", pid: 10, command: "mariadbd"));
        var start = ListenerSamples.Snapshot("start", "all", ListenerSamples.Owned("0.0.0.0", 3306, startUid, "alice", pid: 42, command: "evil"));

        // Act
        await store.WriteBatchAsync(ListenerSamples.Batch(Agent, ListenerSamples.Record(1, 0, baseline), ListenerSamples.Record(2, 60, start)), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(expectedChanged, await ReadValueAsync(directory, "SELECT changed_count FROM listener_comparisons WHERE seq = 2"), "changed_count");
        Assert.AreEqual(expectedFinding, await ReadValueAsync(directory, "SELECT finding FROM listener_comparisons WHERE seq = 2"), "finding");
        Assert.AreEqual("0", await ReadValueAsync(directory, "SELECT new_count FROM listener_comparisons WHERE seq = 2"), "new_count");

        if (expectedChanged == "1")
        {
            var changes = JsonNode.Parse(await ReadValueAsync(directory, "SELECT changed_listeners FROM listener_comparisons WHERE seq = 2"))!.AsArray();

            Assert.HasCount(1, changes, "stored changes");
            Assert.AreEqual("tcp", (string)changes[0]!["proto"]!, "proto");
            Assert.AreEqual("0.0.0.0:3306", (string)changes[0]!["local"]!, "local");
            Assert.AreEqual(1001, (int)changes[0]!["uid"]!, "uid");
            Assert.AreEqual("alice", (string)changes[0]!["user"]!, "user");
            Assert.AreEqual(110, (int)changes[0]!["previous_uid"]!, "previous_uid");
        }
        else
        {
            Assert.AreEqual(string.Empty, await ReadValueAsync(directory, "SELECT ifnull(changed_listeners, '') FROM listener_comparisons WHERE seq = 2"), "no changes are stored");
        }
    }

    /// <summary>
    /// The changes an agent reports in a snapshot are stored with their count and the first 64 of the list, only for a check of all or privileged.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ListenerMarkWriterStoresTheChangesOfAnAgentSnapshot()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var few = ListenerSamples.FoundChanges("all", [ListenerSamples.Changed("0.0.0.0", 3306, 1001, 110), ListenerSamples.Changed("0.0.0.0", 8443, 0, 1001)], 2);
        var many = ListenerSamples.FoundChanges("privileged", [.. Enumerable.Range(0, 100).Select(index => ListenerSamples.Changed("10.0.0.1", 1000 + index, 1001, 110))], 5);
        var none = ListenerSamples.Snapshot("none", "all", ListenerSamples.Tcp("0.0.0.0", 22));

        // Act
        await store.WriteBatchAsync(ListenerSamples.Batch(Agent, ListenerSamples.Record(1, 0, few), ListenerSamples.Record(2, 60, many), ListenerSamples.Record(3, 120, none)), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual("4", await ReadValueAsync(directory, "SELECT changed_count FROM connection_marks WHERE seq = 1"), "listed plus omitted");
        Assert.HasCount(2, JsonNode.Parse(await ReadValueAsync(directory, "SELECT changed_listeners FROM connection_marks WHERE seq = 1"))!.AsArray(), "stored entries of the short list");
        Assert.AreEqual("105", await ReadValueAsync(directory, "SELECT changed_count FROM connection_marks WHERE seq = 2"), "listed plus omitted of the long list");
        Assert.HasCount(ListenerComparer.MaxChangedListeners, JsonNode.Parse(await ReadValueAsync(directory, "SELECT changed_listeners FROM connection_marks WHERE seq = 2"))!.AsArray(), "the stored list is cut to 64");
        Assert.AreEqual("0", await ReadValueAsync(directory, "SELECT changed_count FROM connection_marks WHERE seq = 3"), "a check of none stores no change count");
        Assert.AreEqual(string.Empty, await ReadValueAsync(directory, "SELECT ifnull(changed_listeners, '') FROM connection_marks WHERE seq = 3"), "a check of none stores no list");
    }

    /// <summary>
    /// The changed listeners of a mark and of a comparison are stored with an empty command and the user kept, and the input is not changed.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ListenerMarkWriterStoresChangedListenersWithoutCommand()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var changed = ListenerSamples.Changed("0.0.0.0", 3306, 1001, 110, "alice", 42, "evil");
        var found = ListenerSamples.FoundChanges("all", [changed], 0);
        var baseline = ListenerSamples.Snapshot("none", "all", ListenerSamples.Owned("0.0.0.0", 3306, 110, "mysql", pid: 10, command: "mariadbd"));
        var start = ListenerSamples.Snapshot("start", "all", ListenerSamples.Owned("0.0.0.0", 3306, 1001, "alice", pid: 42, command: "evil"));

        // Act
        await store.WriteBatchAsync(ListenerSamples.Batch(Agent, ListenerSamples.Record(1, 0, baseline), ListenerSamples.Record(2, 60, start), ListenerSamples.Record(3, 120, found)), TestContext.CancellationToken);

        var mark = await ReadValueAsync(directory, "SELECT changed_listeners FROM connection_marks WHERE seq = 3");
        var comparison = await ReadValueAsync(directory, "SELECT changed_listeners FROM listener_comparisons WHERE seq = 2");

        // Assert
        foreach (var (name, text) in new[] { ("mark", mark), ("comparison", comparison) })
        {
            var entry = JsonNode.Parse(text)!.AsArray()[0]!;

            Assert.AreEqual(string.Empty, (string)entry["command"]!, $"{name}: the command is stored empty");
            Assert.AreEqual("alice", (string)entry["user"]!, $"{name}: the user is kept");
            Assert.AreEqual(1001, (int)entry["uid"]!, $"{name}: the uid is kept");
            Assert.AreEqual(110, (int)entry["previous_uid"]!, $"{name}: the previous uid is kept");
        }

        Assert.AreEqual("evil", changed.Command, "the snapshot of the caller keeps its command");
    }

    /// <summary>
    /// The text a changed-listener list is stored as holds an empty command, every other member, and leaves the listeners as they were.
    /// </summary>
    [TestMethod]
    public void ListenerQueriesSerializeChangedListenersEmptiesTheCommand()
    {
        // Arrange
        var changed = ListenerSamples.Changed("0.0.0.0", 3306, 1001, 110, "alice", 42, "evil");

        changed.MixedUids = true;
        changed.PreviousMixedUids = true;

        // Act
        var text = ListenerQueries.SerializeChangedListeners([changed]);
        var entry = JsonNode.Parse(text)!.AsArray()[0]!;

        // Assert
        Assert.AreEqual(string.Empty, (string)entry["command"]!, "command");
        Assert.AreEqual(42, (int)entry["pid"]!, "pid");
        Assert.AreEqual("alice", (string)entry["user"]!, "user");
        Assert.IsTrue((bool)entry["mixed_uids"]!, "mixed_uids");
        Assert.AreEqual(110, (int)entry["previous_uid"]!, "previous_uid");
        Assert.IsTrue((bool)entry["previous_mixed_uids"]!, "previous_mixed_uids");
        Assert.AreEqual("evil", changed.Command, "the input keeps its command");
    }

    /// <summary>
    /// The pair of a base and a start gives the same comparison row whichever of the two is stored first.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ListenerMarkWriterChangesIndependentOfStoringOrder()
    {
        // Arrange
        var baseline = ListenerSamples.Record(1, 0, ListenerSamples.Snapshot("none", "all", ListenerSamples.Owned("0.0.0.0", 3306, 110, "mysql", pid: 10, command: "mariadbd")));
        var start = ListenerSamples.Record(2, 60, ListenerSamples.Snapshot("start", "all", ListenerSamples.Owned("0.0.0.0", 3306, 1001, "alice", pid: 42, command: "evil")));
        var rows = new List<string>();

        foreach (var order in new[] { new[] { baseline, start }, [start, baseline] })
        {
            using var directory = new TempDirectory();
            await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

            foreach (var record in order)
            {
                await store.WriteBatchAsync(ListenerSamples.Batch(Agent, record), TestContext.CancellationToken);
            }

            rows.Add(await ReadValueAsync(directory, "SELECT changed_count || '|' || ifnull(changed_listeners, '') || '|' || finding FROM listener_comparisons WHERE seq = 2"));
        }

        // Assert
        Assert.StartsWith("1|", rows[0], "the base first stores the change");
        Assert.AreEqual(rows[0], rows[1], "the start first gives the same row");
    }

    /// <summary>
    /// Reads the first column of the first row of a query as text.
    /// </summary>
    /// <param name="directory">The directory of the store</param>
    /// <param name="sql">The query</param>
    /// <returns>A task that returns the text</returns>
    private async Task<string> ReadValueAsync(TempDirectory directory, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(directory.Path, StorageLimits.FileName)};Pooling=False");

        await connection.OpenAsync(TestContext.CancellationToken);

        using var command = connection.CreateCommand();

        command.CommandText = sql;

        return Convert.ToString(await command.ExecuteScalarAsync(TestContext.CancellationToken), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    /// <summary>
    /// Reads the first column of the first row of a query on the database file of a directory.
    /// </summary>
    /// <param name="directory">The directory of the store</param>
    /// <param name="sql">The query</param>
    /// <returns>A task that returns the text</returns>
    private async Task<string> ReadScalarAsync(TempDirectory directory, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(directory.Path, StorageLimits.FileName)};Pooling=False");

        await connection.OpenAsync(TestContext.CancellationToken);

        using var command = connection.CreateCommand();

        command.CommandText = sql;

        return (string)(await command.ExecuteScalarAsync(TestContext.CancellationToken))!;
    }

    /// <summary>
    /// Inserts a record the way the batch writer does.
    /// </summary>
    /// <param name="connection">The connection</param>
    /// <param name="transaction">The transaction</param>
    /// <param name="record">The record</param>
    /// <returns>A task that returns the ID of the record</returns>
    private async Task<long> InsertAsync(SqliteConnection connection, SqliteTransaction transaction, DataRecord record)
    {
        using var command = connection.CreateCommand();

        command.Transaction = transaction;
        command.CommandText = "INSERT INTO records(kind, origin, source, agent_id, seq, captured_at, received_at, boot_id, clock_offset_ns, data) "
                              + "VALUES ($kind, $origin, $source, $agent, $seq, $captured, $received, NULL, NULL, CAST($data AS TEXT)) RETURNING id";
        command.Parameters.AddWithValue("$kind", record.Kind);
        command.Parameters.AddWithValue("$origin", record.Origin);
        command.Parameters.AddWithValue("$source", record.Source);
        command.Parameters.AddWithValue("$agent", Agent);
        command.Parameters.AddWithValue("$seq", (long)record.Seq);
        command.Parameters.AddWithValue("$captured", StorageTime.ToNanoseconds(record.CapturedAt));
        command.Parameters.AddWithValue("$received", StorageTime.ToNanoseconds(record.CapturedAt));
        command.Parameters.AddWithValue("$data", PayloadJson.Serialize(record.Data!, StorageLimits.MaxPayloadBytes));

        return (long)(await command.ExecuteScalarAsync(TestContext.CancellationToken))!;
    }

    #endregion // Methods
}