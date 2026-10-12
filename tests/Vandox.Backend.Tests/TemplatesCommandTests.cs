using System.Globalization;

using Microsoft.Data.Sqlite;

using Vandox.Backend.Hosting;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <c>vandoxd templates</c>
/// </summary>
[TestClass]
[OSCondition(OperatingSystems.Linux)]
public class TemplatesCommandTests
{
    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// The command rebuilds the template statistics, prints the summary to standard output, logs to standard error and exits with 0.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task TemplatesCommandRebuildsAndPrintsSummary()
    {
        // Arrange
        using var fixture = new BackendFixture();

        await StoreLinesAsync(fixture.Storage);

        var expected = await SnapshotAsync(fixture.Storage);

        await ExecuteAsync(fixture.Storage, "DELETE FROM log_template_hours", "DELETE FROM log_templates", "DELETE FROM log_source_hours");

        // Act
        var code = await fixture.RunAsync(["templates"], null, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0, code, "exit code");
        Assert.AreEqual(string.Concat(Row("Log lines:", 3), Row("Batches:", 1), Row("Templates:", 2)), fixture.Out.ToString().Replace("\r\n", "\n", StringComparison.Ordinal), "standard output");
        Assert.Contains("template rebuild started", fixture.Error.ToString(), "the start is logged");
        Assert.Contains("template rebuild progress", fixture.Error.ToString(), "the progress is logged");
        Assert.IsNotEmpty(expected, "the arranged lines were counted by the write path");
        Assert.AreSequenceEqual(expected, await SnapshotAsync(fixture.Storage), "the tables equal the write path's");
    }

    /// <summary>
    /// An invalid configuration stops the command with exit code 1 and no output.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task TemplatesCommandExitsOneOnInvalidConfiguration()
    {
        // Arrange
        using var fixture = new BackendFixture();

        await File.WriteAllTextAsync(fixture.ConfigPath, "incidents:\n  window_before: 25h\n", TestContext.CancellationToken);

        // Act
        var code = await fixture.RunAsync(["templates"], null, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(1, code, "exit code");
        Assert.Contains("configuration invalid", fixture.Error.ToString(), "configuration log line");
        Assert.AreEqual(string.Empty, fixture.Out.ToString(), "nothing on standard output");
    }

    /// <summary>
    /// A storage directory that cannot be opened stops the command with exit code 1 and no output.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task TemplatesCommandExitsOneWhenStorageCannotBeOpened()
    {
        // Arrange
        using var fixture = new BackendFixture();

        Directory.Delete(fixture.Storage);

        // Act
        var code = await fixture.RunAsync(["templates"], null, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(1, code, "exit code");
        Assert.Contains("opening database failed", fixture.Error.ToString(), "the failure is logged");
        Assert.AreEqual(string.Empty, fixture.Out.ToString(), "nothing on standard output");
    }

    /// <summary>
    /// A token that is cancelled before the run logs the interruption, prints no summary and exits with 1.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task TemplatesCommandExitsOneWhenInterrupted()
    {
        // Arrange
        using var fixture = new BackendFixture();
        using var cancelled = new CancellationTokenSource();

        await StoreLinesAsync(fixture.Storage);
        await cancelled.CancelAsync();

        // Act
        var code = await fixture.RunAsync(["templates"], null, cancelled.Token);

        // Assert
        Assert.AreEqual(1, code, "exit code");
        Assert.Contains("template rebuild interrupted", fixture.Error.ToString(), "the interruption is logged");
        Assert.AreEqual(string.Empty, fixture.Out.ToString(), "nothing on standard output");
    }

    /// <summary>
    /// A token that is cancelled after the start was logged logs the interruption, prints no summary and exits with 1.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task TemplatesCommandExitsOneWhenInterruptedDuringRun()
    {
        // Arrange
        using var fixture = new BackendFixture();
        using var cancelled = new CancellationTokenSource();
        await using var error = new CancelOnTextWriter(cancelled, "template rebuild started");

        await StoreLinesAsync(fixture.Storage);

        // Act
        var code = await BackendApp.RunAsync(["-config", fixture.ConfigPath, "templates"], [], fixture.Out, error, new ServeHooks(), cancelled.Token);

        // Assert
        Assert.AreEqual(1, code, "exit code");
        Assert.Contains("template rebuild started", error.ToString(), "the start was logged");
        Assert.Contains("template rebuild interrupted", error.ToString(), "the interruption is logged");
        Assert.AreEqual(string.Empty, fixture.Out.ToString(), "nothing on standard output");
    }

    /// <summary>
    /// A store failure during the run is logged, prints no summary and exits with 1.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task TemplatesCommandExitsOneWhenStoreFailsDuringRun()
    {
        // Arrange
        using var fixture = new BackendFixture();

        await StoreLinesAsync(fixture.Storage);
        await ExecuteAsync(fixture.Storage, "DROP TABLE log_source_hours");

        // Act
        var code = await fixture.RunAsync(["templates"], null, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(1, code, "exit code");
        Assert.Contains("\"msg\":\"template rebuild failed\"", fixture.Error.ToString(), "the failure is logged");
        Assert.Contains("store: rebuilding templates: ", fixture.Error.ToString(), "the log carries the store's text");
        Assert.AreEqual(string.Empty, fixture.Out.ToString(), "nothing on standard output");
    }

    /// <summary>
    /// Creates a line of the summary: the label padded to 21 characters, the value and a line feed.
    /// </summary>
    /// <param name="label">The label</param>
    /// <param name="value">The value</param>
    /// <returns>The line</returns>
    private static string Row(string label, int value)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{label.PadRight(21)}{value}\n");
    }

    /// <summary>
    /// Stores three log lines of two templates in a new store and closes it.
    /// </summary>
    /// <param name="directory">The storage directory</param>
    /// <returns>A task that completes when the lines are stored</returns>
    private async Task StoreLinesAsync(string directory)
    {
        await using var store = await SqliteStore.OpenAsync(directory, TestContext.CancellationToken);

        await IncidentSamples.WriteAsync(store,
                                         TestContext.CancellationToken,
                                         IncidentSamples.Line(IncidentSamples.Base, "sshd", "alpha event"),
                                         IncidentSamples.Line(IncidentSamples.Base.AddMinutes(1), "sshd", "alpha event"),
                                         IncidentSamples.Line(IncidentSamples.Base.AddHours(1), "sshd", "beta event"));
    }

    /// <summary>
    /// Reads the three template tables by key, not by ID.
    /// </summary>
    /// <param name="directory">The storage directory</param>
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
    /// Runs statements on the database of a store directory.
    /// </summary>
    /// <param name="directory">The storage directory</param>
    /// <param name="statements">The statements</param>
    /// <returns>A task that completes when the statements ran</returns>
    private async Task ExecuteAsync(string directory, params string[] statements)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(directory, StorageLimits.FileName)};Pooling=False");

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
    /// <param name="directory">The storage directory</param>
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