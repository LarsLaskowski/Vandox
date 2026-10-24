using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Data.Sqlite;

using Vandox.Core.Model;

namespace Vandox.Storage.Tests;

/// <summary>
/// Tests for <see cref="BatchWriter"/>
/// </summary>
[TestClass]
public class BatchWriterTests
{
    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// The labels are stored exactly as the serializer would write them with the keys in ordinal order, so rows written before and
    /// after the writer was optimized (record 0083) are alike.
    /// </summary>
    /// <param name="first">The first label key</param>
    /// <param name="second">The second label key</param>
    /// <param name="value">The label value</param>
    [TestMethod]
    [DataRow("cpu", "a", "0")]
    [DataRow("b", "B", "x<y>&'z\"")]
    [DataRow("mount", "device", "sda1/ü€😀")]
    [DataRow("é", "e", "tab\there\nnewline")]
    public void BatchWriterSerializesLabelsInOrdinalOrder(string first, string second, string value)
    {
        // Arrange
        var labels = new Dictionary<string, string>
                     {
                         [first] = value,
                         [second] = "other"
                     };
        var expected = JsonSerializer.Serialize(new SortedDictionary<string, string>(labels, StringComparer.Ordinal));

        // Act
        var json = BatchWriter.SerializeLabels(labels);

        // Assert
        Assert.AreEqual(expected, json, "stored text");
    }

    /// <summary>
    /// A single label and repeated use of the same serializer give the same text.
    /// </summary>
    [TestMethod]
    public void BatchWriterSerializesSingleLabel()
    {
        // Arrange
        var labels = new Dictionary<string, string>
                     {
                         ["only"] = "one"
                     };

        // Act and assert
        Assert.AreEqual("""{"only":"one"}""", BatchWriter.SerializeLabels(labels), "single label");
        Assert.AreEqual("""{"only":"one"}""", BatchWriter.SerializeLabels(labels), "second call");
    }

    /// <summary>
    /// The stored payload of a connection snapshot keeps the UID (also 0), the user and the mixed-UIDs flag of listeners, new listeners and connections, and leaves absent ones out.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task BatchWriterKeepsTheSocketUidInTheStoredPayload()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var snapshot = ListenerSamples.Snapshot("all",
                                                "all",
                                                new Listener
                                                {
                                                    Proto = Proto.Tcp,
                                                    Local = new IPEndPoint(IPAddress.Parse("0.0.0.0"), 22),
                                                    Uid = 0,
                                                    User = "root"
                                                },
                                                new Listener
                                                {
                                                    Proto = Proto.Udp,
                                                    Local = new IPEndPoint(IPAddress.Parse("0.0.0.0"), 5353),
                                                    Uid = 105,
                                                    User = "avahi",
                                                    MixedUids = true
                                                },
                                                ListenerSamples.Tcp("0.0.0.0", 80));

        snapshot.AggregatesOnly = false;
        snapshot.NewListeners = [
                                    new Listener
                                    {
                                        Proto = Proto.Tcp,
                                        Local = new IPEndPoint(IPAddress.Parse("0.0.0.0"), 23),
                                        Uid = 1001,
                                        User = "alice",
                                        MixedUids = true
                                    }
                                ];
        snapshot.Connections = [
                                   new Connection
                                   {
                                       Proto = Proto.Tcp,
                                       Local = new IPEndPoint(IPAddress.Parse("10.0.0.1"), 80),
                                       Remote = new IPEndPoint(IPAddress.Parse("192.0.2.1"), 5000),
                                       State = "ESTABLISHED",
                                       Uid = 33,
                                       User = "www-data"
                                   }
                               ];

        // Act
        await store.WriteBatchAsync(ListenerSamples.Batch("agent-1", ListenerSamples.Record(1, 0, snapshot)), TestContext.CancellationToken);

        var stored = await ReadScalarAsync(directory, "SELECT data FROM records WHERE seq = 1");
        var data = JsonNode.Parse(stored)!.AsObject();
        var listeners = data["listeners"]!.AsArray();

        // Assert
        Assert.AreEqual(0, (int)listeners[0]!["uid"]!, "uid 0 is stored");
        Assert.AreEqual("root", (string)listeners[0]!["user"]!, "user of uid 0");
        Assert.IsFalse(listeners[0]!.AsObject().ContainsKey("mixed_uids"), "an unset mixed_uids is not stored");
        Assert.AreEqual(105, (int)listeners[1]!["uid"]!, "uid of the mixed endpoint");
        Assert.IsTrue((bool)listeners[1]!["mixed_uids"]!, "mixed_uids is stored");

        foreach (var key in new[] { "uid", "user", "mixed_uids" })
        {
            Assert.IsFalse(listeners[2]!.AsObject().ContainsKey(key), $"{key} of an unknown owner is not stored");
        }

        Assert.AreEqual(1001, (int)data["new_listeners"]![0]!["uid"]!, "uid of the new listener");
        Assert.AreEqual("alice", (string)data["new_listeners"]![0]!["user"]!, "user of the new listener");
        Assert.AreEqual(33, (int)data["connections"]![0]!["uid"]!, "uid of the connection");
        Assert.AreEqual("www-data", (string)data["connections"]![0]!["user"]!, "user of the connection");
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

    #endregion // Methods
}