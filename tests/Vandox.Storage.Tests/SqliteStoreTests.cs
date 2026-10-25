using Microsoft.Data.Sqlite;

using Vandox.Core.Model;

namespace Vandox.Storage.Tests;

/// <summary>
/// Tests for the waiting of the writer of <see cref="SqliteStore"/>
/// </summary>
[TestClass]
public class SqliteStoreTests
{
    #region Constants

    private const double ConfiguredBusyTimeoutSeconds = 5.0;

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// A write that waits for a lock another connection holds gives up after the busy timeout of 5 seconds, not after the 30 seconds that the retry of
    /// Microsoft.Data.Sqlite adds by default. The wait is measured as the ratio to the configured timeout and must be at least about the timeout and less than
    /// twice it.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreWriteGivesUpAfterBusyTimeout()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        await using var blocker = new SqliteConnection($"Data Source={Path.Combine(directory.Path, StorageLimits.FileName)};Pooling=False");

        await blocker.OpenAsync(TestContext.CancellationToken);
        await Execute(blocker, "BEGIN IMMEDIATE");

        var batch = Samples.AgentBatch(Samples.Metric(RecordOrigin.Agent, 1, 0, "cpu"));
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Act
        var write = Task.Run(() => store.WriteBatchAsync(batch, TestContext.CancellationToken), TestContext.CancellationToken);
        var winner = await Task.WhenAny(write, Task.Delay(TimeSpan.FromSeconds(2 * ConfiguredBusyTimeoutSeconds), TestContext.CancellationToken));

        stopwatch.Stop();

        await Execute(blocker, "ROLLBACK");

        // Assert
        Assert.AreSame(write, winner, "the write gave up before twice the busy timeout");

        var failure = await Assert.ThrowsExactlyAsync<StoreException>(() => write, "the write fails");
        var ratio = stopwatch.Elapsed.TotalSeconds / ConfiguredBusyTimeoutSeconds;

        Assert.IsInstanceOfType<SqliteException>(failure.InnerException, "the cause is the locked database");
        Assert.IsGreaterThanOrEqualTo(0.9, ratio, $"the write waited {stopwatch.Elapsed.TotalSeconds:F2} seconds, at least about the busy timeout");
        Assert.IsLessThan(2.0, ratio, $"the write waited {stopwatch.Elapsed.TotalSeconds:F2} seconds, less than twice the busy timeout");
    }

    /// <summary>
    /// Runs one statement.
    /// </summary>
    /// <param name="connection">The connection</param>
    /// <param name="sql">The statement</param>
    /// <returns>A task that completes when the statement ran</returns>
    private async Task Execute(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();

        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(TestContext.CancellationToken);
    }

    #endregion // Methods
}