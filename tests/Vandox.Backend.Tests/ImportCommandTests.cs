using System.Buffers.Binary;
using System.Text;

using Microsoft.Data.Sqlite;

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
    /// <c>vandoxd import</c> reads the Dovecot log file fixture with the built-in parsers, with no hook, in the configured time zone, and stores its events.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImportCommandImportsTheDovecotLogWithBuiltInParsersInTheConfiguredZone()
    {
        // Arrange
        using var fixture = new BackendFixture();
        var logs = Path.Combine(fixture.Folder.Path, "logs");

        Directory.CreateDirectory(logs);
        File.Copy(RepositoryFiles.Path("testdata/logs/dovecot.log"), Path.Combine(logs, "dovecot.log-20261010"));
        await File.AppendAllTextAsync(fixture.ConfigPath, "import:\n  time_zone: Europe/Berlin\n", TestContext.CancellationToken);

        // Act
        var code = await fixture.RunAsync(["import", logs], null, TestContext.CancellationToken);
        var output = fixture.Out.ToString();
        var records = await ReadLogLinesAsync(fixture.Storage, "dovecot", TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0, code, "exit code");
        Assert.Contains("  imported:          1", output, "the file is imported");
        Assert.Contains("  not recognized:    0", output, "the file is recognized");
        Assert.Contains("Records stored:      11", output, "records");
        Assert.HasCount(11, records, "11 records of source dovecot");
        Assert.AreEqual(new DateTimeOffset(2026, 10, 9, 1, 12, 12, TimeSpan.Zero), records[0].Record.CapturedAt, "03:12:12 in Berlin summer time is 01:12:12 UTC");
        Assert.AreEqual("dovecot.log-20261010", ((LogLine)records[0].Record.Data!).Log, "log is the path as the import lists it");
        Assert.AreEqual("-|-|dovecot.database_unavailable|dovecot.database_unavailable|-|dovecot.panic|dovecot.fatal|-|-|-|-", string.Join('|', records.Select(record => ((LogLine)record.Record.Data!).Event is { Length: > 0 } name ? name : "-")), "events in fixture order");
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
    /// When the lookup of the import state fails, the import prints the summary of the files found so far, logs the failure and exits with 1.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImportCommandExitsWithOneWhenReadingTheImportStateFails()
    {
        // Arrange
        using var fixture = new BackendFixture();
        var logs = Path.Combine(fixture.Folder.Path, "logs");

        Directory.CreateDirectory(logs);
        await WriteLogsAsync(logs, TestContext.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(logs, "a.txt"), "just a note\n", TestContext.CancellationToken);
        await File.AppendAllTextAsync(fixture.ConfigPath, "import:\n  time_zone: Europe/Berlin\n", TestContext.CancellationToken);

        var created = await SqliteStore.OpenAsync(fixture.Storage, TestContext.CancellationToken);

        await created.DisposeAsync();

        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(fixture.Storage, "vandox.db")};Pooling=False"))
        {
            await connection.OpenAsync(TestContext.CancellationToken);

            using var command = connection.CreateCommand();

            command.CommandText = "DROP INDEX import_files_head; ALTER TABLE import_files DROP COLUMN head_sha256";
            await command.ExecuteNonQueryAsync(TestContext.CancellationToken);
        }

        // Act
        var code = await fixture.RunAsync(["import", logs], null, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(1, code, "exit code");
        Assert.Contains("import failed", fixture.Error.ToString(), "log line");
        Assert.Contains("a.txt", fixture.Out.ToString(), "the summary lists the files found so far");
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
    /// A syslog file that grew since its import stores only its new lines, the summary lists it as continued, and the exit code stays 0.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImportCommandContinuesAGrownSyslogFileAndPrintsItInTheSummary()
    {
        // Arrange
        using var fixture = new BackendFixture();
        var logs = Path.Combine(fixture.Folder.Path, "logs");
        var first = "Jul  1 12:00:00 web-1 sshd[1]: m1\nJul  1 12:00:01 web-1 sshd[1]: m2\n";
        var grown = $"{first}Jul  1 12:00:02 web-1 sshd[1]: m3\nJul  1 12:00:03 web-1 sshd[1]: m4\n";

        Directory.CreateDirectory(Path.Combine(logs, "a"));
        Directory.CreateDirectory(Path.Combine(logs, "b"));
        await File.AppendAllTextAsync(fixture.ConfigPath, "import:\n  time_zone: Europe/Berlin\n", TestContext.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(logs, "a", "syslog"), first, TestContext.CancellationToken);
        File.SetLastWriteTimeUtc(Path.Combine(logs, "a", "syslog"), new DateTime(2026, 7, 2, 0, 0, 0, DateTimeKind.Utc));

        // Act
        var run1 = await fixture.RunAsync(["import", logs], null, TestContext.CancellationToken);

        fixture.Out.GetStringBuilder().Clear();
        File.Delete(Path.Combine(logs, "a", "syslog"));
        await File.WriteAllTextAsync(Path.Combine(logs, "b", "syslog"), grown, TestContext.CancellationToken);
        File.SetLastWriteTimeUtc(Path.Combine(logs, "b", "syslog"), new DateTime(2026, 7, 2, 0, 0, 0, DateTimeKind.Utc));

        var run2 = await fixture.RunAsync(["import", logs], null, TestContext.CancellationToken);
        var secondOutput = fixture.Out.ToString();

        fixture.Out.GetStringBuilder().Clear();

        var run3 = await fixture.RunAsync(["import", logs], null, TestContext.CancellationToken);
        var records = (await ReadLogLinesAsync(fixture.Storage, "syslog", TestContext.CancellationToken)).OrderBy(record => record.Record.CapturedAt).ToList();

        // Assert
        Assert.AreEqual(0, run1, "exit code of the first run");
        Assert.AreEqual(0, run2, "exit code of the run that continues a file");
        Assert.Contains("  imported:          0\n  continued:         1\n", secondOutput, "the file is counted as continued");
        Assert.Contains("\nFiles continued:\n  \"b/syslog\": continues \"a/syslog\" after line 2\n", secondOutput, "the summary lists the continued file");
        Assert.Contains("Records stored:      2", secondOutput, "only the new lines are stored");
        Assert.Contains("Lines read:          2", secondOutput, "only the new lines are read");
        Assert.AreEqual(0, run3, "exit code of the third run");
        Assert.Contains("  already imported:  1", fixture.Out.ToString(), "the third run imports nothing");
        Assert.AreEqual("m1|m2|m3|m4", string.Join('|', records.Select(record => ((LogLine)record.Record.Data!).Message)), "every line is stored once");
    }

    /// <summary>
    /// The new part of a grown syslog file is parsed with the name and modification time of the grown file, not those of the earlier one.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImportCommandAnchorsTheYearOfTheNewPartOnTheGrownFile()
    {
        // Arrange
        using var fixture = new BackendFixture();
        var logs = Path.Combine(fixture.Folder.Path, "logs");
        var february = "Feb  3 08:00:00 web-1 sshd[1]: feb one\nFeb  4 08:00:00 web-1 sshd[1]: feb two\n";
        var earlier = Path.Combine(logs, "a", "syslog");
        var later = Path.Combine(logs, "b", "syslog");

        Directory.CreateDirectory(Path.Combine(logs, "a"));
        Directory.CreateDirectory(Path.Combine(logs, "b"));
        await File.AppendAllTextAsync(fixture.ConfigPath, "import:\n  time_zone: Europe/Berlin\n", TestContext.CancellationToken);
        await File.WriteAllTextAsync(earlier, february, TestContext.CancellationToken);
        File.SetLastWriteTimeUtc(earlier, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));

        // Act
        await fixture.RunAsync(["import", logs], null, TestContext.CancellationToken);
        File.Delete(earlier);
        await File.WriteAllTextAsync(later, $"{february}Sep  5 12:00:00 web-1 sshd[1]: sep one\nSep  6 12:00:00 web-1 sshd[1]: sep two\n", TestContext.CancellationToken);
        File.SetLastWriteTimeUtc(later, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));

        var code = await fixture.RunAsync(["import", logs], null, TestContext.CancellationToken);
        var records = await ReadLogLinesAsync(fixture.Storage, "syslog", TestContext.CancellationToken);
        var september = records.Single(record => ((LogLine)record.Record.Data!).Message == "sep one");

        // Assert
        Assert.AreEqual(0, code, "exit code");
        Assert.Contains("continues \"a/syslog\" after line 2", fixture.Out.ToString(), "the file is continued");
        Assert.AreEqual(new DateTimeOffset(2026, 9, 5, 10, 0, 0, TimeSpan.Zero), september.Record.CapturedAt, "September is in 2026, the year of the grown file's modification time");
    }

    /// <summary>
    /// A grown MariaDB error log is continued, the continuation lines of the last entry of the earlier copy are skipped and named in the summary, and the entries after them are stored.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImportCommandContinuesAGrownMariaDbErrorLogAndNamesTheSkippedContinuationLines()
    {
        // Arrange
        using var fixture = new BackendFixture();
        var logs = Path.Combine(fixture.Folder.Path, "logs");
        var earlier = Path.Combine(logs, "a", "error.log");
        var first = "2026-03-01 23:00:01 0 [Note] first entry\n2026-03-01 23:00:02 0 [Note] second entry\nmore of the second entry\n";
        var grown = $"{first}even more\nand more\n2026-03-01 23:00:05 0 [Note] third entry\n2026-03-01 23:00:06 0 [Note] fourth entry\n";

        Directory.CreateDirectory(Path.Combine(logs, "a"));
        Directory.CreateDirectory(Path.Combine(logs, "b"));
        await File.AppendAllTextAsync(fixture.ConfigPath, "import:\n  time_zone: Europe/Berlin\n", TestContext.CancellationToken);
        await File.WriteAllTextAsync(earlier, first, TestContext.CancellationToken);

        // Act
        await fixture.RunAsync(["import", logs], null, TestContext.CancellationToken);
        File.Delete(earlier);
        fixture.Out.GetStringBuilder().Clear();
        await File.WriteAllTextAsync(Path.Combine(logs, "b", "error.log"), grown, TestContext.CancellationToken);

        var code = await fixture.RunAsync(["import", logs], null, TestContext.CancellationToken);
        var output = fixture.Out.ToString();
        var records = (await ReadLogLinesAsync(fixture.Storage, "mariadb", TestContext.CancellationToken)).OrderBy(record => record.Record.CapturedAt).ToList();

        // Assert
        Assert.AreEqual(0, code, "exit code");
        Assert.Contains("  \"b/error.log\": continues \"a/error.log\" after line 3, skipped 2 lines before its first record\n", output, "the summary names the skipped lines");
        Assert.Contains("    line 4: \"line before the first entry\"\n    line 5: \"line before the first entry\"\n", output, "the lines are skipped with their numbers in the whole file");
        Assert.Contains("Records stored:      2", output, "the entries after them are stored");
        Assert.HasCount(4, records, "four entries in all");
        Assert.Contains("third entry", ((LogLine)records[2].Record.Data!).Message, "the third entry is stored");
        Assert.Contains("fourth entry", ((LogLine)records[3].Record.Data!).Message, "the fourth entry is stored");
    }

    /// <summary>
    /// A grown journal export is imported as a whole, and a forged entry inside the payload of a binary field never becomes a record.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImportCommandImportsAGrownJournalExportAsAWholeAndStoresNoForgedEntry()
    {
        // Arrange
        using var fixture = new BackendFixture();
        var logs = Path.Combine(fixture.Folder.Path, "logs");
        var earlier = Path.Combine(logs, "a", "journal.export");
        var payload = "x\n\n__REALTIME_TIMESTAMP=1760000000000000\n_HOSTNAME=forged\n_COMM=sshd\nMESSAGE=forged\n\n";
        var cut = JournalExport(payload, "x\n\n__REALTI", false);

        Directory.CreateDirectory(Path.Combine(logs, "a"));
        Directory.CreateDirectory(Path.Combine(logs, "b"));
        await File.WriteAllBytesAsync(earlier, cut, TestContext.CancellationToken);

        // Act
        var run1 = await fixture.RunAsync(["import", logs], null, TestContext.CancellationToken);
        var firstOutput = fixture.Out.ToString();

        File.Delete(earlier);
        fixture.Out.GetStringBuilder().Clear();
        await File.WriteAllBytesAsync(Path.Combine(logs, "b", "journal.export"), JournalExport(payload, payload, true), TestContext.CancellationToken);

        var run2 = await fixture.RunAsync(["import", logs], null, TestContext.CancellationToken);
        var secondOutput = fixture.Out.ToString();
        var records = await ReadLogLinesAsync(fixture.Storage, "journal", TestContext.CancellationToken);
        var lines = records.Select(record => (LogLine)record.Record.Data!).ToList();

        // Assert
        Assert.AreEqual(0, run1, "exit code of the first run");
        Assert.Contains("Records stored:      1", firstOutput, "the first run stores the complete entry");
        Assert.Contains("\"truncated entry\"", firstOutput, "the cut entry is skipped");
        Assert.AreEqual(0, run2, "exit code of the second run");
        Assert.Contains("  imported:          1\n  continued:         0\n", secondOutput, "the grown export is imported as a whole");
        Assert.DoesNotContain("Files continued", secondOutput, "it is not listed as continued");
        Assert.Contains("Records stored:      2", secondOutput, "both genuine entries are stored");
        Assert.HasCount(3, lines, "the genuine entry of the first run is stored again");
        Assert.AreEqual(2, lines.Count(line => line.Message == "one"), "the first entry has two records");
        Assert.IsTrue(lines.All(line => line.Host == "genuine"), "every record has the genuine host");
        Assert.DoesNotContain(line => line.Host == "forged" || line.Program == "sshd" || line.Message == "forged", lines, "no forged entry is stored");
    }

    /// <summary>
    /// An older copy of a file that was imported before is recognized in a later run: nothing is stored for it, the summary lists it as contained, the exit code is 0, and the store holds each line once. The older copy may be gzip-compressed, and a third run gives the same result.
    /// </summary>
    /// <param name="gzip">Whether the older copy is gzip-compressed</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ImportCommandRecognizesAnOlderCopyImportedAfterTheNewerOne(bool gzip)
    {
        // Arrange
        using var fixture = new BackendFixture();
        var logs = Path.Combine(fixture.Folder.Path, "logs");
        var hooks = new ServeHooks
                    {
                        Parsers = [new LineParser("test")]
                    };

        Directory.CreateDirectory(Path.Combine(logs, "a"));
        Directory.CreateDirectory(Path.Combine(logs, "b"));
        await File.WriteAllTextAsync(Path.Combine(logs, "a", "syslog"), Log(8), TestContext.CancellationToken);

        // Act
        var run1 = await fixture.RunAsync(["import", logs], hooks, TestContext.CancellationToken);

        File.Delete(Path.Combine(logs, "a", "syslog"));

        if (gzip)
        {
            await File.WriteAllBytesAsync(Path.Combine(logs, "b", "syslog.gz"), Gzip(Log(5)), TestContext.CancellationToken);
        }
        else
        {
            await File.WriteAllTextAsync(Path.Combine(logs, "b", "syslog"), Log(5), TestContext.CancellationToken);
        }

        fixture.Out.GetStringBuilder().Clear();

        var run2 = await fixture.RunAsync(["import", logs], hooks, TestContext.CancellationToken);
        var secondOutput = fixture.Out.ToString();

        fixture.Out.GetStringBuilder().Clear();

        var run3 = await fixture.RunAsync(["import", logs], hooks, TestContext.CancellationToken);
        var thirdOutput = fixture.Out.ToString();
        var records = (await ReadLogLinesAsync(fixture.Storage, "import", TestContext.CancellationToken)).OrderBy(record => record.Record.CapturedAt).ToList();
        var name = gzip ? "b/syslog.gz" : "b/syslog";

        // Assert
        Assert.AreEqual(0, run1, "exit code of the first run");
        Assert.AreEqual(0, run2, "exit code of the run that finds the older copy");
        Assert.Contains("  imported:          0\n  continued:         0\n  already imported:  0\n  contained:         1\n", secondOutput, "the file is counted as contained");
        Assert.Contains($"\nFiles contained in an imported file:\n  \"{name}\": start of \"a/syslog\"\n", secondOutput, "the summary lists the contained file with its container");
        Assert.Contains("Records stored:      0", secondOutput, "nothing is stored");
        Assert.AreEqual(0, run3, "exit code of the third run");
        Assert.Contains("  contained:         1", thirdOutput, "the third run gives the same result");
        Assert.AreEqual(string.Join('|', Enumerable.Range(1, 8).Select(number => $"LOG line {number}")), string.Join('|', records.Select(record => ((LogLine)record.Record.Data!).Message)), "every line is stored once");
        Assert.AreEqual(1L, await CountAsync(fixture.Storage, "SELECT count(*) FROM import_files"), "the older copy has no state");
    }

    /// <summary>
    /// An older copy that comes later in the same input is recognized, the exit code is 0, and files that are imported, contained, already imported or not recognized do not fail the run.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImportCommandRecognizesAnOlderCopyLaterInTheSameRunAndExitsWithZero()
    {
        // Arrange
        using var fixture = new BackendFixture();
        var logs = Path.Combine(fixture.Folder.Path, "logs");
        var hooks = new ServeHooks
                    {
                        Parsers = [new LineParser("test")]
                    };

        Directory.CreateDirectory(Path.Combine(logs, "a"));
        Directory.CreateDirectory(Path.Combine(logs, "b"));
        await File.WriteAllTextAsync(Path.Combine(logs, "a", "syslog"), Log(8), TestContext.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(logs, "b", "syslog"), Log(5), TestContext.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(logs, "note.txt"), "no parser\n", TestContext.CancellationToken);

        // Act
        var first = await fixture.RunAsync(["import", logs], hooks, TestContext.CancellationToken);
        var firstOutput = fixture.Out.ToString();

        fixture.Out.GetStringBuilder().Clear();

        var second = await fixture.RunAsync(["import", logs], hooks, TestContext.CancellationToken);
        var secondOutput = fixture.Out.ToString();
        var records = await ReadLogLinesAsync(fixture.Storage, "import", TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0, first, "exit code of the first run");
        Assert.Contains("  imported:          1\n  continued:         0\n  already imported:  0\n  contained:         1\n  not recognized:    1\n", firstOutput, "imported, contained and not recognized");
        Assert.Contains("\nFiles contained in an imported file:\n  \"b/syslog\": start of \"a/syslog\"\n", firstOutput, "the older copy is listed");
        Assert.AreEqual(0, second, "exit code of the second run");
        Assert.Contains("  already imported:  1\n  contained:         1\n", secondOutput, "the second run imports nothing");
        Assert.HasCount(8, records, "each line is stored once");
    }

    /// <summary>
    /// An older copy of a journal export, whose parser is not framed by lines, is recognized as well.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImportCommandRecognizesAnOlderCopyOfAJournalExport()
    {
        // Arrange
        using var fixture = new BackendFixture();
        var logs = Path.Combine(fixture.Folder.Path, "logs");
        var full = string.Concat(Enumerable.Range(1, 3).Select(JournalEntry));
        var older = string.Concat(Enumerable.Range(1, 2).Select(JournalEntry));

        Directory.CreateDirectory(Path.Combine(logs, "a"));
        Directory.CreateDirectory(Path.Combine(logs, "b"));
        await File.WriteAllTextAsync(Path.Combine(logs, "a", "journal.export"), full, TestContext.CancellationToken);

        // Act
        var run1 = await fixture.RunAsync(["import", logs], null, TestContext.CancellationToken);

        File.Delete(Path.Combine(logs, "a", "journal.export"));
        await File.WriteAllTextAsync(Path.Combine(logs, "b", "journal.export"), older, TestContext.CancellationToken);
        fixture.Out.GetStringBuilder().Clear();

        var run2 = await fixture.RunAsync(["import", logs], null, TestContext.CancellationToken);
        var output = fixture.Out.ToString();
        var records = (await ReadLogLinesAsync(fixture.Storage, "journal", TestContext.CancellationToken)).OrderBy(record => record.Record.CapturedAt).ToList();

        // Assert
        Assert.AreEqual(0, run1, "exit code of the first run");
        Assert.AreEqual(0, run2, "exit code of the second run");
        Assert.Contains("  contained:         1\n", output, "the older copy is contained");
        Assert.Contains("\"b/journal.export\": start of \"a/journal.export\"", output, "listed with its container");
        Assert.AreEqual("entry 1|entry 2|entry 3", string.Join('|', records.Select(record => ((LogLine)record.Record.Data!).Message)), "each entry is stored once");
    }

    /// <summary>
    /// An import that is interrupted during the verification read prints the summary with the file as failed and exits with 1.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImportCommandPrintsSummaryWhenInterruptedDuringTheVerificationRead()
    {
        // Arrange
        using var fixture = new BackendFixture();
        using var cancelled = new CancellationTokenSource();
        var logs = Path.Combine(fixture.Folder.Path, "logs");

        Directory.CreateDirectory(logs);
        await File.WriteAllTextAsync(Path.Combine(logs, "a.log"), "LOG one\nLOG two\nLOG three\n", TestContext.CancellationToken);

        // Act
        var code = await fixture.RunAsync(["import", logs],
                                          new ServeHooks
                                          {
                                              Parsers = [new CancelAfterReadParser(cancelled)]
                                          },
                                          cancelled.Token);

        // Assert
        Assert.AreEqual(1, code, "exit code");
        Assert.Contains("import interrupted", fixture.Error.ToString(), "log says so");
        Assert.Contains("The import was interrupted; run it again to continue.", fixture.Out.ToString(), "summary says so");
        Assert.Contains("a.log", fixture.Out.ToString(), "the summary names the file");
        Assert.Contains("interrupted", fixture.Out.ToString().Replace("The import was interrupted", string.Empty, StringComparison.Ordinal), "with the reason interrupted");
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

    /// <summary>
    /// Builds a journal export of a complete entry and an entry with a binary message.
    /// </summary>
    /// <param name="payload">The payload the binary field declares</param>
    /// <param name="written">The part of the payload that is written</param>
    /// <param name="closed">Whether the field and the entry are closed</param>
    /// <returns>The bytes</returns>
    private static byte[] JournalExport(string payload, string written, bool closed)
    {
        var length = new byte[sizeof(ulong)];

        BinaryPrimitives.WriteUInt64LittleEndian(length, (ulong)Encoding.UTF8.GetByteCount(payload));

        using var output = new MemoryStream();

        output.Write(Encoding.UTF8.GetBytes("__REALTIME_TIMESTAMP=1760000000000000\n_HOSTNAME=genuine\nSYSLOG_IDENTIFIER=app\nMESSAGE=one\n\n"));
        output.Write(Encoding.UTF8.GetBytes("__REALTIME_TIMESTAMP=1760000001000000\n_HOSTNAME=genuine\nSYSLOG_IDENTIFIER=app\nMESSAGE\n"));
        output.Write(length);
        output.Write(Encoding.UTF8.GetBytes(written));

        if (closed)
        {
            output.Write(Encoding.UTF8.GetBytes("\n\n"));
        }

        return output.ToArray();
    }

    /// <summary>
    /// Returns log content for the test parser.
    /// </summary>
    /// <param name="lines">The number of lines</param>
    /// <returns>The text, starting with <c>LOG</c></returns>
    private static string Log(int lines)
    {
        return string.Concat(Enumerable.Range(1, lines).Select(number => $"LOG line {number}\n"));
    }

    /// <summary>
    /// Compresses a text with gzip.
    /// </summary>
    /// <param name="text">The text</param>
    /// <returns>The compressed bytes</returns>
    private static byte[] Gzip(string text)
    {
        using var output = new MemoryStream();

        using (var zip = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionMode.Compress, leaveOpen: true))
        {
            zip.Write(Encoding.UTF8.GetBytes(text));
        }

        return output.ToArray();
    }

    /// <summary>
    /// Builds an entry of a journal export.
    /// </summary>
    /// <param name="number">The number of the entry</param>
    /// <returns>The entry, ended by an empty line</returns>
    private static string JournalEntry(int number)
    {
        return $"__CURSOR=s=1;i={number}\n__REALTIME_TIMESTAMP={1772368215123456 + (number * 1000000L)}\n_HOSTNAME=web-1\nSYSLOG_IDENTIFIER=sshd\nMESSAGE=entry {number}\n\n";
    }

    /// <summary>
    /// Counts the rows a query returns in the database of the backend.
    /// </summary>
    /// <param name="storage">The storage directory</param>
    /// <param name="sql">The query: <c>SELECT count(*) ...</c></param>
    /// <returns>A task that returns the count</returns>
    private async Task<long> CountAsync(string storage, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(storage, StorageLimits.FileName)};Pooling=False");

        await connection.OpenAsync(TestContext.CancellationToken);

        using var command = connection.CreateCommand();

        command.CommandText = sql;

        return (long)(await command.ExecuteScalarAsync(TestContext.CancellationToken))!;
    }

    #endregion // Methods
}