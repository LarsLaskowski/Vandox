using System.Text.Json;

using Microsoft.Data.Sqlite;

using Vandox.Core.Incidents;

namespace Vandox.Storage.Tests;

/// <summary>
/// Tests for <see cref="IncidentRepository"/>
/// </summary>
[TestClass]
public class IncidentRepositoryTests
{
    #region Constants

    private const string SelectColumns = "SELECT id, started_at, ended_at, first_kill_at, kill_count, created_at, updated_at, data FROM incidents";

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Reading the stored worst-case incident allocates the stored bytes about once beyond what decoding those bytes costs, not a UTF-16 copy of them.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IncidentRepositoryReadAllocatesTheStoredBytesOnce()
    {
        // Arrange
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, StorageLimits.FileName);

        await using (var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken))
        {
            await store.SaveIncidentAsync(Samples.WorstCase(), Samples.Base.AddDays(1), TestContext.CancellationToken);
        }

        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False;Mode=ReadOnly");

        await connection.OpenAsync(TestContext.CancellationToken);

        using var command = connection.CreateCommand();

        command.CommandText = SelectColumns;

        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        Assert.IsTrue(await reader.ReadAsync(TestContext.CancellationToken), "the stored incident is read");

        var stored = reader.GetFieldValue<byte[]>(7);
        var size = (long)stored.Length;

        IncidentRepository.Read(reader);
        JsonSerializer.Deserialize<Incident>(stored, IncidentJson.Options);

        // Act
        var beforeRead = GC.GetAllocatedBytesForCurrentThread();
        var read = IncidentRepository.Read(reader);
        var readBytes = GC.GetAllocatedBytesForCurrentThread() - beforeRead;
        var beforeDecode = GC.GetAllocatedBytesForCurrentThread();
        var decoded = JsonSerializer.Deserialize<Incident>(stored, IncidentJson.Options);
        var decodeBytes = GC.GetAllocatedBytesForCurrentThread() - beforeDecode;
        var extra = readBytes - decodeBytes;

        // Assert
        Assert.IsGreaterThan(1_000_000L, size, "the stored document is large");
        Assert.IsNotNull(decoded, "the reference decode gives an incident");
        Assert.AreEqual(decoded.KillCount, read.Incident.KillCount, "the read gives the stored incident");
        Assert.IsLessThan((long)(1.5 * size), extra, $"bytes allocated by the read beyond decoding the stored bytes (read {readBytes}, decode {decodeBytes}, stored {size})");
    }

    #endregion // Methods
}