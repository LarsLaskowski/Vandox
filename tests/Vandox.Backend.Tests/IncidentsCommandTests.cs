using System.Globalization;

using Microsoft.Data.Sqlite;

using Vandox.Backend.Hosting;
using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <c>vandoxd incidents</c>
/// </summary>
[TestClass]
[OSCondition(OperatingSystems.Linux)]
public class IncidentsCommandTests
{
    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// The command rebuilds the incidents of the stored kills, prints one line per incident and the summary to standard output, logs to standard error and exits with 0; a second run reports the incident as unchanged.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IncidentsCommandPrintsIncidentsAndExitsZero()
    {
        // Arrange
        using var fixture = new BackendFixture();

        await StoreKillAsync(fixture.Storage);

        // Act
        var first = await fixture.RunAsync(["incidents"], null, TestContext.CancellationToken);
        var firstOutput = Normalize(fixture.Out.ToString());
        var firstLog = fixture.Error.ToString();

        fixture.Out.GetStringBuilder().Clear();

        var second = await fixture.RunAsync(["incidents"], null, TestContext.CancellationToken);
        var secondOutput = Normalize(fixture.Out.ToString());

        // Assert
        Assert.AreEqual(0, first, "exit code of the first run");
        Assert.AreEqual("#1 created 2026-10-02T04:59:00Z kills=1 victim=\"mariadbd\"\n" + Summary(1, 0, 0), firstOutput, "standard output of the first run");
        Assert.Contains("incident reconstruction started", firstLog, "the start is logged to standard error");
        Assert.AreEqual(0, second, "exit code of the second run");
        Assert.AreEqual("#1 unchanged 2026-10-02T04:59:00Z kills=1 victim=\"mariadbd\"\n" + Summary(0, 0, 1), secondOutput, "standard output of the second run");
    }

    /// <summary>
    /// An invalid configuration stops the command with exit code 1 and no output.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IncidentsCommandExitsOneOnInvalidConfiguration()
    {
        // Arrange
        using var fixture = new BackendFixture();

        await File.WriteAllTextAsync(fixture.ConfigPath, "incidents:\n  window_before: 25h\n", TestContext.CancellationToken);

        // Act
        var code = await fixture.RunAsync(["incidents"], null, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(1, code, "exit code");
        Assert.Contains("configuration invalid", fixture.Error.ToString(), "configuration log line");
        Assert.AreEqual(string.Empty, fixture.Out.ToString(), "nothing on standard output");
    }

    /// <summary>
    /// A cancelled run logs the interruption, prints no summary and exits with 1.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IncidentsCommandExitsOneWhenInterrupted()
    {
        // Arrange
        using var fixture = new BackendFixture();
        using var cancelled = new CancellationTokenSource();

        await StoreKillAsync(fixture.Storage);
        await cancelled.CancelAsync();

        // Act
        var code = await fixture.RunAsync(["incidents"], null, cancelled.Token);

        // Assert
        Assert.AreEqual(1, code, "exit code");
        Assert.Contains("incident reconstruction interrupted", fixture.Error.ToString(), "the interruption is logged");
        Assert.DoesNotContain("Incidents:", fixture.Out.ToString(), "no summary");
    }

    /// <summary>
    /// A storage directory that cannot be opened stops the command with exit code 1 and no output.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IncidentsCommandExitsOneWhenStorageCannotBeOpened()
    {
        // Arrange
        using var fixture = new BackendFixture();

        Directory.Delete(fixture.Storage);

        // Act
        var code = await fixture.RunAsync(["incidents"], null, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(1, code, "exit code");
        Assert.Contains("opening database failed", fixture.Error.ToString(), "the failure is logged");
        Assert.AreEqual(string.Empty, fixture.Out.ToString(), "nothing on standard output");
    }

    /// <summary>
    /// A store failure during the run is logged, prints no summary and exits with 1.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IncidentsCommandExitsOneWhenStoreFailsDuringRun()
    {
        // Arrange
        using var fixture = new BackendFixture();

        await StoreKillAsync(fixture.Storage);
        await DropIncidentTableAsync(fixture.Storage);

        // Act
        var code = await fixture.RunAsync(["incidents"], null, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(1, code, "exit code");
        Assert.Contains("\"msg\":\"incident reconstruction failed\"", fixture.Error.ToString(), "the failure is logged");
        Assert.Contains("store: saving incident: ", fixture.Error.ToString(), "the log carries the store's text");
        Assert.DoesNotContain("Incidents:", fixture.Out.ToString(), "no summary");
    }

    /// <summary>
    /// A run that is cancelled after the first incident was printed logs the interruption, prints no summary and exits with 1.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IncidentsCommandExitsOneWhenInterruptedDuringRun()
    {
        // Arrange
        using var fixture = new BackendFixture();
        using var cancelled = new CancellationTokenSource();
        await using var output = new CancellingWriter(cancelled);

        await StoreKillsAsync(fixture.Storage);

        // Act
        var code = await BackendApp.RunAsync(["-config", fixture.ConfigPath, "incidents"], [], output, fixture.Error, new ServeHooks(), cancelled.Token);

        // Assert
        Assert.AreEqual(1, code, "exit code");
        Assert.Contains("incident reconstruction interrupted", fixture.Error.ToString(), "the interruption is logged");
        Assert.StartsWith("#1 created ", Normalize(output.ToString()), "the first incident was printed");
        Assert.DoesNotContain("Incidents:", output.ToString(), "no summary");
    }

    /// <summary>
    /// Builds the summary that the command prints for one kill.
    /// </summary>
    /// <param name="created">The number of created incidents</param>
    /// <param name="updated">The number of updated incidents</param>
    /// <param name="unchanged">The number of unchanged incidents</param>
    /// <returns>The summary</returns>
    private static string Summary(int created, int updated, int unchanged)
    {
        return string.Concat(Row("Incidents:", 1), Row("  created:", created), Row("  updated:", updated), Row("  unchanged:", unchanged), Row("  merged:", 0), Row("  kept:", 0), Row("  skipped:", 0), Row("OOM kills:", 1), Row("Log lines read:", 1));
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
    /// Replaces the line endings of a text by line feeds.
    /// </summary>
    /// <param name="text">The text</param>
    /// <returns>The text with line feeds</returns>
    private static string Normalize(string text)
    {
        return text.Replace("\r\n", "\n", StringComparison.Ordinal);
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
    /// Stores two OOM kill lines three hours apart, which are two incidents, in a new store and closes it.
    /// </summary>
    /// <param name="directory">The storage directory</param>
    /// <returns>A task that completes when the lines are stored</returns>
    private async Task StoreKillsAsync(string directory)
    {
        await using var store = await SqliteStore.OpenAsync(directory, TestContext.CancellationToken);

        await IncidentSamples.WriteAsync(store, TestContext.CancellationToken, IncidentSamples.Kill(IncidentSamples.Base), IncidentSamples.Kill(IncidentSamples.Base.AddHours(3)));
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