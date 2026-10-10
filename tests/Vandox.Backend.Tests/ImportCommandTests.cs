using Vandox.Backend.Hosting;
using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <c>vandoxd import</c> and the other command-line paths of <see cref="BackendApp"/>
/// </summary>
[TestClass]
[OSCondition(OperatingSystems.Linux)]
public class ImportCommandTests
{
    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// An import stores the records, prints the summary and exits with 0; a second run reports the files as already imported.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImportCommandImportsDirectoryAndPrintsSummary()
    {
        // Arrange
        using var fixture = new BackendFixture();
        var logs = Path.Combine(fixture.Folder.Path, "logs");

        Directory.CreateDirectory(logs);
        await File.WriteAllTextAsync(Path.Combine(logs, "a.log"), "LOG one\nbad line\nLOG two\n", TestContext.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(logs, "note.txt"), "no parser\n", TestContext.CancellationToken);

        var hooks = new ServeHooks
                    {
                        Parsers = [new LineParser("test")]
                    };

        // Act
        var first = await fixture.RunAsync(["import", logs], hooks, TestContext.CancellationToken);
        var firstOutput = fixture.Out.ToString();

        fixture.Out.GetStringBuilder().Clear();

        var second = await fixture.RunAsync(["import", logs], hooks, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0, first, "exit code of the first run");
        Assert.Contains("Files found:         2", firstOutput, "files found");
        Assert.Contains("  imported:          1", firstOutput, "imported");
        Assert.Contains("  not recognized:    1", firstOutput, "not recognized");
        Assert.Contains("Records stored:      2", firstOutput, "records");
        Assert.Contains("Lines skipped:       1", firstOutput, "skipped");
        Assert.Contains("Time range:          2026-10-01T10:00:01Z to 2026-10-01T10:00:03Z", firstOutput, "time range");
        Assert.Contains("\"note.txt\": \"no parser recognized the file\"", firstOutput, "listed file with reason");
        Assert.Contains("line 2: \"bad line\"", firstOutput, "skipped line");
        Assert.Contains("\"msg\":\"file finished\"", fixture.Error.ToString(), "progress goes to standard error");
        Assert.AreEqual(0, second, "exit code of the second run");
        Assert.Contains("  already imported:  1", fixture.Out.ToString(), "the second run imports nothing");
        Assert.Contains("Time range:          no records were stored", fixture.Out.ToString(), "no records in the second run");
    }

    /// <summary>
    /// <c>vandoxd import</c> reads a syslog file and a journal export with the built-in parsers, with no hook, in the configured time zone.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImportCommandImportsSyslogFileAndJournalExportWithBuiltInParsersInTheConfiguredZone()
    {
        // Arrange
        using var fixture = new BackendFixture();
        var logs = Path.Combine(fixture.Folder.Path, "logs");

        Directory.CreateDirectory(logs);
        await WriteLogsAsync(logs, TestContext.CancellationToken);
        await File.AppendAllTextAsync(fixture.ConfigPath, "import:\n  time_zone: Europe/Berlin\n", TestContext.CancellationToken);

        // Act
        var code = await fixture.RunAsync(["import", logs], null, TestContext.CancellationToken);
        var output = fixture.Out.ToString();
        var syslog = await ReadLogLinesAsync(fixture.Storage, "syslog", TestContext.CancellationToken);
        var journal = await ReadLogLinesAsync(fixture.Storage, "journal", TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0, code, "exit code");
        Assert.Contains("  imported:          2", output, "both files are imported");
        Assert.Contains("  not recognized:    0", output, "every file is recognized");
        Assert.HasCount(1, syslog, "one syslog record");
        Assert.AreEqual(new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero), syslog[0].Record.CapturedAt, "12:00:00 in Berlin summer time is 10:00:00 UTC");
        Assert.AreEqual("hello from syslog", ((LogLine)syslog[0].Record.Data!).Message, "syslog message");
        Assert.AreEqual("syslog", ((LogLine)syslog[0].Record.Data!).Log, "log is the path as the import lists it");
        Assert.HasCount(1, journal, "one journal record");
        Assert.AreEqual("hello from journal", ((LogLine)journal[0].Record.Data!).Message, "journal message");
        Assert.AreEqual("web-1", ((LogLine)journal[0].Record.Data!).Host, "journal host");
    }

    /// <summary>
    /// Without <c>import.time_zone</c> the journal export is imported and the syslog file fails with a fixed reason; a second run with the option completes the file.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImportCommandFailsSyslogFileWithoutTimeZoneAndASecondRunWithItCompletesTheFile()
    {
        // Arrange
        using var fixture = new BackendFixture();
        var logs = Path.Combine(fixture.Folder.Path, "logs");

        Directory.CreateDirectory(logs);
        await WriteLogsAsync(logs, TestContext.CancellationToken);

        // Act
        var first = await fixture.RunAsync(["import", logs], null, TestContext.CancellationToken);
        var firstOutput = fixture.Out.ToString();
        var afterFirst = await ReadLogLinesAsync(fixture.Storage, "syslog", TestContext.CancellationToken);

        fixture.Out.GetStringBuilder().Clear();
        await File.AppendAllTextAsync(fixture.ConfigPath, "import:\n  time_zone: Europe/Berlin\n", TestContext.CancellationToken);

        var second = await fixture.RunAsync(["import", logs], null, TestContext.CancellationToken);
        var secondOutput = fixture.Out.ToString();
        var syslog = await ReadLogLinesAsync(fixture.Storage, "syslog", TestContext.CancellationToken);
        var journal = await ReadLogLinesAsync(fixture.Storage, "journal", TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(1, first, "exit code of the run without the option");
        Assert.Contains("  imported:          1", firstOutput, "the journal export is imported");
        Assert.Contains("  failed:            1", firstOutput, "the syslog file failed");
        Assert.Contains("Files that failed:", firstOutput, "failed files are listed");
        Assert.Contains("\"syslog\": \"import.time_zone is not set\"", firstOutput, "the reason is the fixed text");
        Assert.IsEmpty(afterFirst, "no guessed times are stored");
        Assert.AreEqual(0, second, "exit code of the run with the option");
        Assert.Contains("  imported:          1", secondOutput, "the syslog file is imported");
        Assert.Contains("  already imported:  1", secondOutput, "the journal export is already imported");
        Assert.HasCount(1, syslog, "the syslog line is stored once");
        Assert.AreEqual(new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero), syslog[0].Record.CapturedAt, "12:00:00 in Berlin summer time is 10:00:00 UTC");
        Assert.HasCount(1, journal, "the journal record is stored once");
    }

    /// <summary>
    /// <c>vandoxd import</c> reads the MariaDB error log fixture with the built-in parsers, with no hook, in the configured time zone, and stores its events.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImportCommandImportsTheMariaDbErrorLogWithBuiltInParsersInTheConfiguredZone()
    {
        // Arrange
        using var fixture = new BackendFixture();
        var logs = Path.Combine(fixture.Folder.Path, "logs");

        Directory.CreateDirectory(Path.Combine(logs, "mysql"));
        File.Copy(RepositoryFiles.Path("testdata/logs/mariadb-error.log"), Path.Combine(logs, "mysql", "error.log"));
        await File.AppendAllTextAsync(fixture.ConfigPath, "import:\n  time_zone: Europe/Berlin\n", TestContext.CancellationToken);

        // Act
        var code = await fixture.RunAsync(["import", logs], null, TestContext.CancellationToken);
        var output = fixture.Out.ToString();
        var records = await ReadLogLinesAsync(fixture.Storage, "mariadb", TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0, code, "exit code");
        Assert.Contains("  imported:          1", output, "the file is imported");
        Assert.Contains("  not recognized:    0", output, "the file is recognized");
        Assert.Contains("Records stored:      22", output, "records");
        Assert.HasCount(22, records, "22 records of source mariadb");
        Assert.AreEqual(new DateTimeOffset(2026, 3, 1, 22, 0, 1, TimeSpan.Zero), records[0].Record.CapturedAt, "23:00:01 in Berlin winter time is 22:00:01 UTC");
        Assert.AreEqual("mysql/error.log", ((LogLine)records[0].Record.Data!).Log, "log is the path as the import lists it");
        Assert.AreEqual("mariadb.shutdown|-|-|-|mariadb.shutdown_complete|mariadb.start|-|mariadb.ready|mariadb.start|-|mariadb.recovery_start|-|-|mariadb.recovery_end|mariadb.ready|-|-|mariadb.abort|mariadb.start|mariadb.recovery_start|mariadb.recovery_end|mariadb.ready", string.Join('|', records.Select(record => ((LogLine)record.Record.Data!).Event is { Length: > 0 } name ? name : "-")), "events in fixture order");
    }

    /// <summary>
    /// Without <c>import.time_zone</c> the MariaDB error log fails with the fixed reason, nothing of it is stored and the exit code is 1.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImportCommandFailsTheMariaDbErrorLogWithoutTimeZone()
    {
        // Arrange
        using var fixture = new BackendFixture();
        var logs = Path.Combine(fixture.Folder.Path, "logs");

        Directory.CreateDirectory(Path.Combine(logs, "mysql"));
        File.Copy(RepositoryFiles.Path("testdata/logs/mariadb-error.log"), Path.Combine(logs, "mysql", "error.log"));

        // Act
        var code = await fixture.RunAsync(["import", logs], null, TestContext.CancellationToken);
        var output = fixture.Out.ToString();
        var records = await ReadLogLinesAsync(fixture.Storage, "mariadb", TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(1, code, "exit code");
        Assert.Contains("  failed:            1", output, "the file failed");
        Assert.Contains("\"mysql/error.log\": \"import.time_zone is not set\"", output, "the reason is the fixed text");
        Assert.IsEmpty(records, "nothing of the file is stored");
    }

    /// <summary>
    /// A file that fails makes the exit code 1, and paths in the summary are quoted.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImportCommandExitsWithOneWhenAFileFails()
    {
        // Arrange
        using var fixture = new BackendFixture();
        var logs = Path.Combine(fixture.Folder.Path, "logs");

        Directory.CreateDirectory(logs);
        await File.WriteAllTextAsync(Path.Combine(logs, "bad\nname.log"), "LOG one\nboom\n", TestContext.CancellationToken);

        // Act
        var code = await fixture.RunAsync(["import", logs],
                                          new ServeHooks
                                          {
                                              Parsers = [new LineParser("test")]
                                          },
                                          TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(1, code, "exit code");
        Assert.Contains("Files that failed:", fixture.Out.ToString(), "failed files are listed");
        Assert.Contains("\"bad\\nname.log\": \"the parser gave up\"", fixture.Out.ToString(), "the path is quoted");
    }

    /// <summary>
    /// A cancelled import reports the interruption and exits with 1.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImportCommandReportsInterruption()
    {
        // Arrange
        using var fixture = new BackendFixture();
        using var cancelled = new CancellationTokenSource();
        var logs = Path.Combine(fixture.Folder.Path, "logs");

        Directory.CreateDirectory(logs);
        await File.WriteAllTextAsync(Path.Combine(logs, "a.log"), "LOG one\n", TestContext.CancellationToken);
        await cancelled.CancelAsync();

        // Act
        var code = await fixture.RunAsync(["import", logs],
                                          new ServeHooks
                                          {
                                              Parsers = [new LineParser("test")]
                                          },
                                          cancelled.Token);

        // Assert
        Assert.AreEqual(1, code, "exit code");
        Assert.Contains("import interrupted", fixture.Error.ToString(), "log says so");
    }

    /// <summary>
    /// An import that is interrupted while it runs prints the summary of what was done and exits with 1.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImportCommandPrintsSummaryWhenInterruptedWhileRunning()
    {
        // Arrange
        using var fixture = new BackendFixture();
        using var cancelled = new CancellationTokenSource();
        var logs = Path.Combine(fixture.Folder.Path, "logs");

        Directory.CreateDirectory(logs);
        await File.WriteAllTextAsync(Path.Combine(logs, "a.log"), "LOG one\nLOG two\n", TestContext.CancellationToken);

        // Act
        var code = await fixture.RunAsync(["import", logs],
                                          new ServeHooks
                                          {
                                              Parsers = [new CancellingParser(cancelled)]
                                          },
                                          cancelled.Token);

        // Assert
        Assert.AreEqual(1, code, "exit code");
        Assert.Contains("The import was interrupted; run it again to continue.", fixture.Out.ToString(), "summary says so");
        Assert.Contains("import interrupted", fixture.Error.ToString(), "log says so");
    }

    /// <summary>
    /// An input that cannot be opened exits with 1 and prints no summary.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImportCommandFailsForMissingInput()
    {
        // Arrange
        using var fixture = new BackendFixture();

        // Act
        var code = await fixture.RunAsync(["import", Path.Combine(fixture.Folder.Path, "missing")], null, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(1, code, "exit code");
        Assert.AreEqual(string.Empty, fixture.Out.ToString(), "no summary");
        Assert.Contains("import failed", fixture.Error.ToString(), "log line");
    }

    /// <summary>
    /// A bad configuration, a missing storage directory and invalid parsers stop the import with exit code 1.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImportCommandRefusesBadSetup()
    {
        // Arrange
        using var badConfig = new BackendFixture();
        using var noStorage = new BackendFixture();
        using var badParsers = new BackendFixture();

        await File.WriteAllTextAsync(badConfig.ConfigPath, "unknown: 1\n", TestContext.CancellationToken);
        Directory.Delete(noStorage.Storage);

        // Act
        var config = await badConfig.RunAsync(["import", "/tmp"], null, TestContext.CancellationToken);
        var storage = await noStorage.RunAsync(["import", "/tmp"], null, TestContext.CancellationToken);
        var parsers = await badParsers.RunAsync(["import", "/tmp"],
                                                new ServeHooks
                                                {
                                                    Parsers = [new LineParser("test"), new LineParser("test")]
                                                },
                                                TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(1, config, "bad configuration");
        Assert.Contains("configuration invalid", badConfig.Error.ToString(), "configuration log line");
        Assert.AreEqual(1, storage, "missing storage directory");
        Assert.Contains("opening database failed", noStorage.Error.ToString(), "storage log line");
        Assert.AreEqual(1, parsers, "invalid parsers");
        Assert.Contains("parsers invalid", badParsers.Error.ToString(), "parsers log line");
    }

    /// <summary>
    /// The version, the help and usage errors print what the command line asks for and exit with 0 or 2.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task BackendAppHandlesVersionHelpAndUsageErrors()
    {
        // Arrange
        using var fixture = new BackendFixture();

        // Act
        var version = await BackendApp.RunAsync(["-version"], [], fixture.Out, fixture.Error, new ServeHooks(), TestContext.CancellationToken);
        var versionText = fixture.Out.ToString();
        var help = await BackendApp.RunAsync(["-h"], [], fixture.Out, fixture.Error, new ServeHooks(), TestContext.CancellationToken);
        var helpText = fixture.Error.ToString();

        fixture.Error.GetStringBuilder().Clear();

        var subHelp = await BackendApp.RunAsync(["import", "-h"], [], fixture.Out, fixture.Error, new ServeHooks(), TestContext.CancellationToken);
        var bad = await BackendApp.RunAsync(["-nope"], [], fixture.Out, fixture.Error, new ServeHooks(), TestContext.CancellationToken);
        var extra = await BackendApp.RunAsync(["import"], [], fixture.Out, fixture.Error, new ServeHooks(), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0, version, "version exit code");
        Assert.AreEqual("vandoxd dev (commit unknown, built unknown)\n", versionText.Replace("\r\n", "\n", StringComparison.Ordinal), "version text");
        Assert.AreEqual(0, help, "help exit code");
        Assert.Contains("Usage of vandoxd:", helpText, "usage text");
        Assert.AreEqual(0, subHelp, "sub-command help exit code");
        Assert.Contains("Usage of vandoxd import", fixture.Error.ToString(), "sub-command usage text");
        Assert.AreEqual(2, bad, "unknown flag exit code");
        Assert.Contains("flag provided but not defined: -nope", fixture.Error.ToString(), "unknown flag message");
        Assert.AreEqual(2, extra, "missing path exit code");
    }

    /// <summary>
    /// Writes a syslog file with a year-less line and a journal export into a directory.
    /// </summary>
    /// <param name="directory">The directory</param>
    /// <param name="cancellationToken">The token</param>
    /// <returns>A task that completes when the files are written</returns>
    private static async Task WriteLogsAsync(string directory, CancellationToken cancellationToken)
    {
        var syslog = Path.Combine(directory, "syslog");

        await File.WriteAllTextAsync(syslog, "Jul  1 12:00:00 web-1 sshd[1]: hello from syslog\n", cancellationToken);
        File.SetLastWriteTimeUtc(syslog, new DateTime(2026, 7, 2, 0, 0, 0, DateTimeKind.Utc));
        await File.WriteAllTextAsync(Path.Combine(directory, "journal.export"), "__CURSOR=s=1;i=1\n__REALTIME_TIMESTAMP=1772368215123456\n_HOSTNAME=web-1\nSYSLOG_IDENTIFIER=sshd\nMESSAGE=hello from journal\n\n", cancellationToken);
    }

    /// <summary>
    /// Reads the log lines of one source from the database of the backend.
    /// </summary>
    /// <param name="storage">The storage directory</param>
    /// <param name="source">The source</param>
    /// <param name="cancellationToken">The token</param>
    /// <returns>A task that returns the stored records</returns>
    private static async Task<IReadOnlyList<StoredRecord>> ReadLogLinesAsync(string storage, string source, CancellationToken cancellationToken)
    {
        await using var store = await SqliteStore.OpenAsync(storage, cancellationToken);

        return await store.RecordsAsync(new RecordQuery
                                        {
                                            Kind = RecordKind.LogLine,
                                            Source = source,
                                            From = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero),
                                            To = new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero),
                                            Limit = 100
                                        },
                                        cancellationToken);
    }

    #endregion // Methods
}