namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <c>vandoxd import</c> and the other command-line paths of <see cref="BackendApp"/>
/// </summary>
[TestClass]
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

    #endregion // Methods
}