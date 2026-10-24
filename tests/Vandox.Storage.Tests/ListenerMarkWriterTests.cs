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