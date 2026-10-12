using System.Globalization;
using System.Text;

using Microsoft.Data.Sqlite;

using Vandox.Core.Model;

namespace Vandox.Storage.Tests;

/// <summary>
/// Tests for the template rebuild of <see cref="SqliteStore"/>
/// </summary>
[TestClass]
public class SqliteStoreTemplateRebuildTests
{
    #region Constants

    private const string Superseded = "store: rebuilding templates: superseded by another rebuild";

    private const string MarkerCount = "SELECT count(*) FROM meta WHERE key = 'template_rebuild'";

    private const string CountSum = "SELECT coalesce(sum(count), 0) FROM log_templates";

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// After the three tables were emptied, a rebuild restores exactly what the write path had written for the stored lines.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreRebuildTemplatesRestoresWritePathStatistics()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        var longMessage = string.Join(' ', Enumerable.Range(0, 400).Select(index => "word" + new string((char)('a' + (index % 26)), 3)));
        var lone = "broken \uD800 text";
        var resent = Samples.LogAt(RecordOrigin.Agent, 1, "syslog", "sshd", Hour(0).AddMinutes(2), "login failed for user");

        await WriteAsync(store,
                         Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "sshd", Hour(0), "connect from unknown[203.0.113.7] port 22"),
                         Samples.Metric(RecordOrigin.Backend, 0, 0, "cpu"),
                         Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "cron", Hour(1), "job 4711 finished"),
                         Samples.LogAt(RecordOrigin.Backend, 0, "journal", "sshd", Hour(2), "connect from unknown[198.51.100.2] port 2222"),
                         Samples.LogAt(RecordOrigin.Backend, 0, "journal", "cron", Hour(2).AddMinutes(1), longMessage),
                         Samples.LogAt(RecordOrigin.Backend, 0, "journal", "cron", Hour(1).AddMinutes(1), lone));
        await store.WriteBatchAsync(Samples.AgentBatch(resent), TestContext.CancellationToken);
        await store.WriteBatchAsync(Samples.AgentBatch(resent, Samples.LogAt(RecordOrigin.Agent, 2, "syslog", "cron", Hour(1).AddMinutes(9), "job 12 finished")), TestContext.CancellationToken);
        await WriteAsync(store, Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "sshd", Hour(0).AddMinutes(-30), "connect from unknown[192.0.2.9] port 22"));

        var expected = await SnapshotAsync(directory.Path);
        var lines = long.Parse((await RowsAsync(directory.Path, "SELECT count(*) FROM log_lines"))[0], CultureInfo.InvariantCulture);
        var templates = long.Parse((await RowsAsync(directory.Path, "SELECT count(*) FROM log_templates"))[0], CultureInfo.InvariantCulture);

        await EmptyTablesAsync(directory.Path);

        // Act
        var result = await store.RebuildTemplatesAsync(2, null, TestContext.CancellationToken);

        // Assert
        Assert.Contains("\uD800", lone, "the arranged message holds the lone surrogate");
        Assert.IsGreaterThan(1024, Encoding.UTF8.GetByteCount(longMessage), "the long message exceeds 1,024 UTF-8 bytes");
        Assert.AreEqual(8L, lines, "the stored log lines: the resent record is stored once");
        Assert.AreSequenceEqual(expected, await SnapshotAsync(directory.Path), "the rebuilt tables equal the write path's, by key");
        Assert.AreEqual(lines, result.Lines, "lines");
        Assert.AreEqual((int)((lines + 1) / 2), result.Batches, "batches");
        Assert.AreEqual(templates, result.Templates, "templates");
        Assert.AreEqual("0", (await RowsAsync(directory.Path, MarkerCount))[0], "the completed run removed its marker");
    }

    /// <summary>
    /// Stale statistics are replaced: rows that no stored line produces are gone and changed values are back to the write path's.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreRebuildTemplatesReplacesStaleStatistics()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteAsync(store,
                         Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "sshd", Hour(0), "connect from unknown[203.0.113.7]"),
                         Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "sshd", Hour(1), "connect from unknown[198.51.100.2]"),
                         Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "cron", Hour(1), "job finished"));

        var expected = await SnapshotAsync(directory.Path);

        await ExecuteAsync(directory.Path,
                           "UPDATE log_templates SET count = count + 40, first_seen = 0, last_seen = 1",
                           "UPDATE log_template_hours SET count = count + 7",
                           "UPDATE log_source_hours SET count = count + 9",
                           "INSERT INTO log_templates(id, source, program, template, first_seen, last_seen, count) VALUES (900, 'syslog', 'sshd', 'old rules <n>', 5, 6, 3)",
                           "INSERT INTO log_template_hours(template_id, hour, count) VALUES (900, 0, 3)",
                           "INSERT INTO log_source_hours(source, hour, count) VALUES ('ghost', 0, 4)");

        // Act
        await store.RebuildTemplatesAsync(10, null, TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual(expected, await SnapshotAsync(directory.Path), "stale rows are gone and values are the write path's");
    }

    /// <summary>
    /// The lines are counted in batches of at most the batch size, one committed transaction each.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreRebuildTemplatesCountsInBatchesOfAtMostBatchLines()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var reported = new List<long>();
        var seen = new List<string>();

        await WriteAsync(store, FiveLines());

        // Act
        var result = await store.RebuildTemplatesAsync(2,
                                                       async (lines, _) =>
                                                       {
                                                           reported.Add(lines);

                                                           if (reported.Count == 1)
                                                           {
                                                               seen.AddRange(await RowsAsync(directory.Path, CountSum));
                                                           }
                                                       },
                                                       TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual([2L, 4L, 5L], reported, "the callback gets the lines counted so far");
        Assert.AreSequenceEqual(["2"], seen, "a reader sees the first batch only: it is committed on its own");
        Assert.AreEqual(5L, result.Lines, "lines");
        Assert.AreEqual(3, result.Batches, "batches");
    }

    /// <summary>
    /// A database without log lines ends with all three tables empty, and the callback is never called.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreRebuildTemplatesWithoutLogLinesEmptiesTables()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var calls = 0;

        await WriteAsync(store, Samples.Metric(RecordOrigin.Backend, 0, 0, "cpu"), Samples.Metric(RecordOrigin.Backend, 0, 1, "cpu"));
        await ExecuteAsync(directory.Path,
                           "INSERT INTO log_templates(id, source, program, template, first_seen, last_seen, count) VALUES (900, 'syslog', 'sshd', 'old rules', 5, 6, 3)",
                           "INSERT INTO log_template_hours(template_id, hour, count) VALUES (900, 0, 3)",
                           "INSERT INTO log_source_hours(source, hour, count) VALUES ('ghost', 0, 4)");

        // Act
        var result = await store.RebuildTemplatesAsync(2,
                                                       (_, _) =>
                                                       {
                                                           calls++;

                                                           return Task.CompletedTask;
                                                       },
                                                       TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(await SnapshotAsync(directory.Path), "all three tables are empty");
        Assert.AreEqual(0L, result.Lines, "lines");
        Assert.AreEqual(0, result.Batches, "batches");
        Assert.AreEqual(0L, result.Templates, "templates");
        Assert.AreEqual(0, calls, "the callback is never called");
    }

    /// <summary>
    /// Lines stored by the write path during a rebuild are counted exactly once.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreRebuildTemplatesCountsLinesWrittenDuringRebuildOnce()
    {
        // Arrange
        using var directory = new TempDirectory();
        using var reference = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        await using var other = await SqliteStore.OpenAsync(reference.Path, TestContext.CancellationToken);
        var calls = 0;

        DataRecord[] initial = [
                                   Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "sshd", Hour(0), "alpha event"),
                                   Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "sshd", Hour(0).AddMinutes(1), "alpha event"),
                                   Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "sshd", Hour(1), "beta event"),
                                   Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "sshd", Hour(1).AddMinutes(1), "beta event")
                               ];
        DataRecord[] meanwhile = [
                                     Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "sshd", Hour(2), "alpha event"),
                                     Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "sshd", Hour(2).AddMinutes(1), "beta event")
                                 ];

        await WriteAsync(store, initial);
        await WriteAsync(other, initial);
        await WriteAsync(other, meanwhile);

        // Act
        var result = await store.RebuildTemplatesAsync(2,
                                                       async (_, _) =>
                                                       {
                                                           calls++;

                                                           if (calls == 1)
                                                           {
                                                               await WriteAsync(store, meanwhile);
                                                           }
                                                       },
                                                       TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual(await SnapshotAsync(reference.Path), await SnapshotAsync(directory.Path), "the tables equal those of the write path for all six lines");
        Assert.AreEqual(4L, result.Lines, "only the lines up to the high-water ID are counted by the rebuild");
    }

    /// <summary>
    /// A rebuild cancelled after its first batch keeps the committed batch and the marker, and a following run restores the statistics.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreRebuildTemplatesCancelledKeepsCommittedBatchesAndRerunRestores()
    {
        // Arrange
        using var directory = new TempDirectory();
        using var cancel = new CancellationTokenSource();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteAsync(store, FiveLines());

        var expected = await SnapshotAsync(directory.Path);

        // Act
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.RebuildTemplatesAsync(2, (_, _) => cancel.CancelAsync(), cancel.Token), "the cancelled run");

        var sum = await RowsAsync(directory.Path, CountSum);
        var marker = await RowsAsync(directory.Path, MarkerCount);

        await store.RebuildTemplatesAsync(2, null, TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual(["2"], sum, "the first batch stays committed, nothing of the second is written");
        Assert.AreSequenceEqual(["1"], marker, "the incomplete state stays recorded");
        Assert.AreSequenceEqual(expected, await SnapshotAsync(directory.Path), "the following run restores the write path's statistics");
        Assert.AreSequenceEqual(["0"], await RowsAsync(directory.Path, MarkerCount), "and removes the marker");
    }

    /// <summary>
    /// A batch size outside 1 to 20,000 is refused before anything is written.
    /// </summary>
    /// <param name="batchLines">The batch size</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    [DataRow(20001)]
    public async Task SqliteStoreRebuildTemplatesRefusesBatchLinesOutOfRange(int batchLines)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteAsync(store, FiveLines());

        var before = await SnapshotAsync(directory.Path);

        // Act
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => store.RebuildTemplatesAsync(batchLines, null, TestContext.CancellationToken), $"batch size {batchLines}");

        // Assert
        Assert.IsNotEmpty(before, "the arranged lines were counted");
        Assert.AreSequenceEqual(before, await SnapshotAsync(directory.Path), "the tables keep their rows");
        Assert.AreSequenceEqual(["0"], await RowsAsync(directory.Path, MarkerCount), "no marker was written");
    }

    /// <summary>
    /// The batch sizes 1 and 20,000 are accepted.
    /// </summary>
    /// <param name="batchLines">The batch size</param>
    /// <param name="batches">The expected number of batches for five lines</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow(1, 5)]
    [DataRow(20000, 1)]
    public async Task SqliteStoreRebuildTemplatesAcceptsBatchLinesAtTheLimits(int batchLines, int batches)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteAsync(store, FiveLines());

        var expected = await SnapshotAsync(directory.Path);

        // Act
        var result = await store.RebuildTemplatesAsync(batchLines, null, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(5L, result.Lines, "lines");
        Assert.AreEqual(batches, result.Batches, "batches");
        Assert.AreSequenceEqual(expected, await SnapshotAsync(directory.Path), "the statistics are the write path's");
    }

    /// <summary>
    /// A database failure is a store error with the rebuilding context.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreRebuildTemplatesWrapsDatabaseFailure()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteAsync(store, FiveLines());
        await ExecuteAsync(directory.Path, "DROP TABLE log_source_hours");

        // Act
        var exception = await Assert.ThrowsExactlyAsync<StoreException>(() => store.RebuildTemplatesAsync(2, null, TestContext.CancellationToken), "rebuild after a table is gone");

        // Assert
        Assert.StartsWith("store: rebuilding templates: ", exception.Message, "message prefix");
        Assert.IsInstanceOfType<SqliteException>(exception.InnerException, "the database error is kept as the inner exception");
    }

    /// <summary>
    /// A run whose marker was replaced by a completed run fails and counts nothing on top of it.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreRebuildTemplatesSupersededByCompletedRunFails()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var calls = 0;

        await WriteAsync(store, SixLines());

        var expected = await SnapshotAsync(directory.Path);

        // Act
        var exception = await Assert.ThrowsExactlyAsync<StoreException>(() => store.RebuildTemplatesAsync(2,
                                                                                                          async (_, token) =>
                                                                                                          {
                                                                                                              calls++;

                                                                                                              await store.RebuildTemplatesAsync(2, null, token);
                                                                                                          },
                                                                                                          TestContext.CancellationToken),
                                                                        "run A after run B completed");

        // Assert
        Assert.AreEqual(Superseded, exception.Message, "message");
        Assert.AreEqual(1, calls, "the callback of run A was called once");
        Assert.AreSequenceEqual(expected, await SnapshotAsync(directory.Path), "the tables hold B's counts only");
        Assert.AreSequenceEqual(["0"], await RowsAsync(directory.Path, MarkerCount), "no marker is left");
    }

    /// <summary>
    /// A run whose marker was replaced by an interrupted run fails, and a third run restores the statistics.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreRebuildTemplatesSupersededByInterruptedRunFails()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await WriteAsync(store, SixLines());

        var expected = await SnapshotAsync(directory.Path);

        // Act
        var exception = await Assert.ThrowsExactlyAsync<StoreException>(() => store.RebuildTemplatesAsync(2,
                                                                                                          async (_, _) =>
                                                                                                          {
                                                                                                              using var cancel = new CancellationTokenSource();

                                                                                                              await Assert.ThrowsAsync<OperationCanceledException>(() => store.RebuildTemplatesAsync(2, (_, _) => cancel.CancelAsync(), cancel.Token), "run B");
                                                                                                          },
                                                                                                          TestContext.CancellationToken),
                                                                        "run A after run B was interrupted");

        var sum = await RowsAsync(directory.Path, CountSum);
        var marker = await RowsAsync(directory.Path, MarkerCount);

        await store.RebuildTemplatesAsync(2, null, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(Superseded, exception.Message, "message");
        Assert.AreSequenceEqual(["2"], sum, "only B's first batch is counted");
        Assert.AreSequenceEqual(["1"], marker, "B's marker is present");
        Assert.AreSequenceEqual(expected, await SnapshotAsync(directory.Path), "a third run restores the write path's statistics");
        Assert.AreSequenceEqual(["0"], await RowsAsync(directory.Path, MarkerCount), "and removes the marker");
    }

    /// <summary>
    /// A run whose marker was replaced after its last batch fails in the finish transaction and leaves the other run's marker.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreRebuildTemplatesSupersededBeforeFinishFails()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var calls = 0;

        await WriteAsync(store, SixLines());

        // Act
        var exception = await Assert.ThrowsExactlyAsync<StoreException>(() => store.RebuildTemplatesAsync(10,
                                                                                                          async (_, _) =>
                                                                                                          {
                                                                                                              calls++;

                                                                                                              using var cancel = new CancellationTokenSource();

                                                                                                              await Assert.ThrowsAsync<OperationCanceledException>(() => store.RebuildTemplatesAsync(2, (_, _) => cancel.CancelAsync(), cancel.Token), "run B");
                                                                                                          },
                                                                                                          TestContext.CancellationToken),
                                                                        "run A after run B was interrupted");

        // Assert
        Assert.AreEqual(Superseded, exception.Message, "message");
        Assert.AreEqual(1, calls, "the callback of run A was called once");
        Assert.AreSequenceEqual(["1"], await RowsAsync(directory.Path, MarkerCount), "B's marker remains");
        Assert.AreSequenceEqual(["2"], await RowsAsync(directory.Path, CountSum), "only B's first batch is counted");
    }

    /// <summary>
    /// Gets an hour relative to <see cref="Samples.Base"/>, which starts on a full hour.
    /// </summary>
    /// <param name="offset">The hours after the base</param>
    /// <returns>The start of the hour</returns>
    private static DateTimeOffset Hour(int offset)
    {
        return Samples.Base.AddHours(offset);
    }

    /// <summary>
    /// Creates five log lines of two templates.
    /// </summary>
    /// <returns>The records</returns>
    private static DataRecord[] FiveLines()
    {
        return
        [
            Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "sshd", Hour(0), "alpha event"),
            Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "sshd", Hour(0).AddMinutes(1), "beta event"),
            Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "sshd", Hour(1), "alpha event"),
            Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "cron", Hour(1).AddMinutes(1), "alpha event"),
            Samples.LogAt(RecordOrigin.Backend, 0, "journal", "sshd", Hour(2), "gamma event")
        ];
    }

    /// <summary>
    /// Creates six log lines of two templates.
    /// </summary>
    /// <returns>The records</returns>
    private static DataRecord[] SixLines()
    {
        return [.. FiveLines(), Samples.LogAt(RecordOrigin.Backend, 0, "syslog", "sshd", Hour(2).AddMinutes(1), "beta event")];
    }

    /// <summary>
    /// Writes the records as one batch.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="records">The records</param>
    /// <returns>A task that completes when the batch is stored</returns>
    private async Task WriteAsync(SqliteStore store, params DataRecord[] records)
    {
        var result = await store.WriteBatchAsync(Samples.Batch(records), TestContext.CancellationToken);

        Assert.AreEqual(new WriteResult(records.Length, 0), result, "all records of the arranged batch are stored");
    }

    /// <summary>
    /// Reads the three template tables by key, not by ID.
    /// </summary>
    /// <param name="directory">The database directory</param>
    /// <returns>A task that returns the rows as text</returns>
    private async Task<IReadOnlyList<string>> SnapshotAsync(string directory)
    {
        var rows = new List<string>();

        rows.AddRange(await RowsAsync(directory, "SELECT 'template', source, program, template, first_seen, last_seen, count FROM log_templates ORDER BY source, program, template"));
        rows.AddRange(await RowsAsync(directory, "SELECT 'template hour', t.source, t.program, t.template, h.hour, h.count FROM log_template_hours h JOIN log_templates t ON t.id = h.template_id ORDER BY t.source, t.program, t.template, h.hour"));
        rows.AddRange(await RowsAsync(directory, "SELECT 'source hour', source, hour, count FROM log_source_hours ORDER BY source, hour"));

        return rows;
    }

    /// <summary>
    /// Empties the three template tables behind the store's back, as after an upgrade from schema version 7.
    /// </summary>
    /// <param name="directory">The database directory</param>
    /// <returns>A task that completes when the tables are empty</returns>
    private Task EmptyTablesAsync(string directory)
    {
        return ExecuteAsync(directory, "DELETE FROM log_template_hours", "DELETE FROM log_templates", "DELETE FROM log_source_hours");
    }

    /// <summary>
    /// Runs statements on the database behind the store's back.
    /// </summary>
    /// <param name="directory">The database directory</param>
    /// <param name="statements">The statements</param>
    /// <returns>A task that completes when the statements ran</returns>
    private async Task ExecuteAsync(string directory, params string[] statements)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(directory, StorageLimits.FileName)};Mode=ReadWrite;Pooling=False");

        await connection.OpenAsync(TestContext.CancellationToken);

        foreach (var statement in statements)
        {
            await using var command = connection.CreateCommand();

#pragma warning disable CA2100
            command.CommandText = statement;
#pragma warning restore CA2100

            await command.ExecuteNonQueryAsync(TestContext.CancellationToken);
        }
    }

    /// <summary>
    /// Reads the rows of a query, each as its values joined with a bar.
    /// </summary>
    /// <param name="directory">The database directory</param>
    /// <param name="sql">The query</param>
    /// <returns>A task that returns the rows as text</returns>
    private async Task<IReadOnlyList<string>> RowsAsync(string directory, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(directory, StorageLimits.FileName)};Mode=ReadOnly;Pooling=False");

        await connection.OpenAsync(TestContext.CancellationToken);

        await using var command = connection.CreateCommand();

#pragma warning disable CA2100
        command.CommandText = sql;
#pragma warning restore CA2100

        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);
        var rows = new List<string>();

        while (await reader.ReadAsync(TestContext.CancellationToken))
        {
            rows.Add(string.Join('|', Enumerable.Range(0, reader.FieldCount).Select(column => Convert.ToString(reader.GetValue(column), CultureInfo.InvariantCulture))));
        }

        return rows;
    }

    #endregion // Methods
}