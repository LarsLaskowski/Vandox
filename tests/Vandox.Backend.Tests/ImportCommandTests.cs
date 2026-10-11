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
        Assert.Contains("syslog", fixture.Out.ToString(), "the summary lists the files found so far");
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

    #endregion // Methods
}