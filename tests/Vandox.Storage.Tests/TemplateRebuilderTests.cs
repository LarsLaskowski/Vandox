using System.Globalization;

using Microsoft.Data.Sqlite;

using Vandox.Core.Model;

namespace Vandox.Storage.Tests;

/// <summary>
/// Tests for <see cref="TemplateRebuilder"/>
/// </summary>
[TestClass]
public class TemplateRebuilderTests
{
    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// The clear empties all three template tables in its transaction, writes the run marker and returns the highest record ID.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task TemplateRebuilderClearEmptiesAllThreeTablesAndWritesMarker()
    {
        // Arrange
        using var directory = new TempDirectory();

        await StoreLinesAsync(directory.Path);

        await using var connection = await OpenAsync(directory.Path);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(TestContext.CancellationToken);
        var marker = TemplateRebuilder.NewMarker();

        Assert.IsGreaterThan(0L, await ScalarAsync(connection, transaction, c => c.CommandText = "SELECT count(*) FROM log_templates"), "the write path counted templates");
        Assert.IsGreaterThan(0L, await ScalarAsync(connection, transaction, c => c.CommandText = "SELECT count(*) FROM log_template_hours"), "the write path counted template hours");
        Assert.IsGreaterThan(0L, await ScalarAsync(connection, transaction, c => c.CommandText = "SELECT count(*) FROM log_source_hours"), "the write path counted source hours");

        // Act
        var highId = await TemplateRebuilder.ClearAsync(connection, transaction, marker, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0L, await ScalarAsync(connection, transaction, c => c.CommandText = "SELECT count(*) FROM log_templates"), "log_templates is empty");
        Assert.AreEqual(0L, await ScalarAsync(connection, transaction, c => c.CommandText = "SELECT count(*) FROM log_template_hours"), "log_template_hours is empty");
        Assert.AreEqual(0L, await ScalarAsync(connection, transaction, c => c.CommandText = "SELECT count(*) FROM log_source_hours"), "log_source_hours is empty");
        Assert.AreEqual(await ScalarAsync(connection, transaction, c => c.CommandText = "SELECT max(id) FROM records"), highId, "the highest record ID");
        Assert.IsTrue(await TemplateRebuilder.HoldsMarkerAsync(connection, transaction, marker, TestContext.CancellationToken), "the run marker is written");
    }

    /// <summary>
    /// The finish removes the run marker and returns the number of templates.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task TemplateRebuilderFinishRemovesMarkerAndReturnsTemplateCount()
    {
        // Arrange
        using var directory = new TempDirectory();

        await StoreLinesAsync(directory.Path);

        await using var connection = await OpenAsync(directory.Path);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(TestContext.CancellationToken);
        var marker = TemplateRebuilder.NewMarker();
        var templates = await ScalarAsync(connection, transaction, c => c.CommandText = "SELECT count(*) FROM log_templates");

        await TemplateRebuilder.ClearAsync(connection, transaction, marker, TestContext.CancellationToken);
        await TemplateRebuilder.CountBatchAsync(connection, transaction, 0, long.MaxValue, 100, TestContext.CancellationToken);

        // Act
        var result = await TemplateRebuilder.FinishAsync(connection, transaction, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(templates, result, "the count of the rebuilt templates");
        Assert.IsFalse(await TemplateRebuilder.HoldsMarkerAsync(connection, transaction, marker, TestContext.CancellationToken), "the run marker is removed");
    }

    /// <summary>
    /// Stores log lines through the write path, which counts them into the three template tables.
    /// </summary>
    /// <param name="directory">The database directory</param>
    /// <returns>A task that completes when the lines are stored</returns>
    private async Task StoreLinesAsync(string directory)
    {
        await using var store = await SqliteStore.OpenAsync(directory, TestContext.CancellationToken);
        var batch = Samples.Batch(Samples.Log(RecordOrigin.Backend, 0, 0, "Accepted password for root"),
                                  Samples.Log(RecordOrigin.Backend, 0, 1, "Accepted password for admin"),
                                  Samples.Log(RecordOrigin.Backend, 0, 3700, "Connection closed"));
        var result = await store.WriteBatchAsync(batch, TestContext.CancellationToken);

        Assert.AreEqual(new WriteResult(3, 0), result, "all records of the arranged batch are stored");
    }

    /// <summary>
    /// Opens a connection to the database file.
    /// </summary>
    /// <param name="directory">The database directory</param>
    /// <returns>A task that returns the open connection</returns>
    private async Task<SqliteConnection> OpenAsync(string directory)
    {
        var connection = new SqliteConnection($"Data Source={Path.Combine(directory, StorageLimits.FileName)};Mode=ReadWrite;Pooling=False");

        await connection.OpenAsync(TestContext.CancellationToken);

        return connection;
    }

    /// <summary>
    /// Runs a query that returns one integer in the transaction.
    /// </summary>
    /// <param name="connection">The connection</param>
    /// <param name="transaction">The open transaction</param>
    /// <param name="prepare">Sets the query's text</param>
    /// <returns>A task that returns the value</returns>
    private async Task<long> ScalarAsync(SqliteConnection connection, SqliteTransaction transaction, Action<SqliteCommand> prepare)
    {
        await using var command = connection.CreateCommand();

        command.Transaction = transaction;
        prepare(command);

        return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.CancellationToken), CultureInfo.InvariantCulture);
    }

    #endregion // Methods
}