using Microsoft.Data.Sqlite;

using Vandox.Backend.Hosting;
using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <c>vandoxd report</c>
/// </summary>
[TestClass]
[OSCondition(OperatingSystems.Linux)]
public class ReportCommandTests
{
    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// The command writes only the Markdown report to standard output, logs its start to standard error and exits with 0.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ReportCommandWritesMarkdownAndExitsZero()
    {
        // Arrange
        using var fixture = new BackendFixture();

        await StoreKillAsync(fixture.Storage);
        Assert.AreEqual(0, await fixture.RunAsync(["incidents"], null, TestContext.CancellationToken), "the incidents are rebuilt first");
        fixture.Out.GetStringBuilder().Clear();
        fixture.Error.GetStringBuilder().Clear();

        // Act
        var code = await fixture.RunAsync(["report"], null, TestContext.CancellationToken);
        var output = fixture.Out.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);

        // Assert
        Assert.AreEqual(0, code, "exit code");
        Assert.StartsWith("# Vandox forensics report\n", output, "standard output holds the report");
        Assert.Contains("### Outage #1 at 2026-10-02 04:59 UTC", output, "the stored outage is listed");
        Assert.Contains("` mariadbd `", output, "the victim as a code span");
        Assert.Contains("report started", fixture.Error.ToString(), "the start is logged to standard error");
        Assert.DoesNotContain("report started", output, "the log is not part of the report");
        Assert.DoesNotContain("\"level\"", output, "no log line on standard output");
    }

    /// <summary>
    /// The flag <c>-lang de</c> writes the report in German.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ReportCommandWritesGermanWithLangDe()
    {
        // Arrange
        using var fixture = new BackendFixture();

        await StoreKillAsync(fixture.Storage);
        Assert.AreEqual(0, await fixture.RunAsync(["incidents"], null, TestContext.CancellationToken), "the incidents are rebuilt first");
        fixture.Out.GetStringBuilder().Clear();

        // Act
        var code = await fixture.RunAsync(["report", "-lang", "de"], null, TestContext.CancellationToken);
        var output = fixture.Out.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);

        // Assert
        Assert.AreEqual(0, code, "exit code");
        Assert.StartsWith("# Vandox-Forensikbericht\n", output, "the German title");
        Assert.Contains("\n## Übersicht\n", output, "the German summary heading, with its umlaut");
        Assert.Contains("### Ausfall #1 am 2026-10-02 04:59 UTC", output, "the German outage heading");
        Assert.DoesNotContain("## Summary", output, "no English heading");
    }

    /// <summary>
    /// An invalid configuration stops the command with exit code 1 and no output.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ReportCommandExitsOneOnInvalidConfiguration()
    {
        // Arrange
        using var fixture = new BackendFixture();

        await File.WriteAllTextAsync(fixture.ConfigPath, "incidents:\n  window_before: 25h\n", TestContext.CancellationToken);

        // Act
        var code = await fixture.RunAsync(["report"], null, TestContext.CancellationToken);

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
    public async Task ReportCommandExitsOneWhenStorageCannotBeOpened()
    {
        // Arrange
        using var fixture = new BackendFixture();

        Directory.Delete(fixture.Storage);

        // Act
        var code = await fixture.RunAsync(["report"], null, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(1, code, "exit code");
        Assert.Contains("opening database failed", fixture.Error.ToString(), "the failure is logged");
        Assert.AreEqual(string.Empty, fixture.Out.ToString(), "nothing on standard output");
    }

    /// <summary>
    /// A cancelled run logs the interruption and exits with 1.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ReportCommandExitsOneWhenInterrupted()
    {
        // Arrange
        using var fixture = new BackendFixture();
        using var cancelled = new CancellationTokenSource();

        await StoreKillAsync(fixture.Storage);
        await cancelled.CancelAsync();

        // Act
        var code = await fixture.RunAsync(["report"], null, cancelled.Token);

        // Assert
        Assert.AreEqual(1, code, "exit code");
        Assert.Contains("report interrupted", fixture.Error.ToString(), "the interruption is logged");
    }

    /// <summary>
    /// A token that is cancelled after the start was logged logs the interruption, writes no report and exits with 1.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ReportCommandExitsOneWhenInterruptedDuringRun()
    {
        // Arrange
        using var fixture = new BackendFixture();
        using var cancelled = new CancellationTokenSource();
        await using var error = new CancelOnTextWriter(cancelled, "report started");

        await StoreKillAsync(fixture.Storage);

        // Act
        var code = await BackendApp.RunAsync(["-config", fixture.ConfigPath, "report"], [], fixture.Out, error, new ServeHooks(), cancelled.Token);

        // Assert
        Assert.AreEqual(1, code, "exit code");
        Assert.Contains("report started", error.ToString(), "the start was logged");
        Assert.Contains("report interrupted", error.ToString(), "the interruption is logged");
        Assert.DoesNotContain("### Outage #", fixture.Out.ToString(), "no outage was written");
    }

    /// <summary>
    /// A store failure during the run is logged with the store's text and exits with 1.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ReportCommandExitsOneWhenStoreFailsDuringRun()
    {
        // Arrange
        using var fixture = new BackendFixture();

        await StoreKillAsync(fixture.Storage);
        await DropIncidentTableAsync(fixture.Storage);

        // Act
        var code = await fixture.RunAsync(["report"], null, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(1, code, "exit code");
        Assert.Contains("\"msg\":\"report failed\"", fixture.Error.ToString(), "the failure is logged");
        Assert.Contains("store: reading incidents", fixture.Error.ToString(), "the log carries the store's text");
    }

    /// <summary>
    /// Stores one OOM kill line in a new store and closes it.
    /// </summary>
    /// <param name="directory">The storage directory</param>
    /// <returns>A task that completes when the line is stored</returns>
    private async Task StoreKillAsync(string directory)
    {
        await using var store = await SqliteStore.OpenAsync(directory, TestContext.CancellationToken);

        await IncidentSamples.WriteAsync(store, TestContext.CancellationToken, IncidentSamples.Kill(IncidentSamples.Base));
    }

    /// <summary>
    /// Drops the incident table of a store, so that every incident statement fails.
    /// </summary>
    /// <param name="directory">The storage directory</param>
    /// <returns>A task that completes when the table is dropped</returns>
    private async Task DropIncidentTableAsync(string directory)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(directory, StorageLimits.FileName)};Pooling=False");

        await connection.OpenAsync(TestContext.CancellationToken);

        using var command = connection.CreateCommand();

        command.CommandText = "DROP TABLE incidents";
        await command.ExecuteNonQueryAsync(TestContext.CancellationToken);
    }

    #endregion // Methods
}