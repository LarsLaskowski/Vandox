using Vandox.Core.Model;

namespace Vandox.Import.Tests;

/// <summary>
/// Tests for <see cref="Importer"/> on directories and files
/// </summary>
[TestClass]
public class ImporterDirectoryTests
{
    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// A directory is imported in lexical order, every recognized file with its records, lines and capture range.
    /// </summary>
    /// <param name="fallback">Whether paths are resolved without <c>openat2</c></param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ImporterImportsDirectoryInLexicalOrder(bool fallback)
    {
        // Arrange
        using var harness = new ImportHarness();

        harness.Fallback = fallback;

        harness.Write("b.log", TestInputs.Log(3));
        harness.Write("sub/c.log", TestInputs.Log(2, "LOG other"));
        harness.Write("a.log", TestInputs.Log(1, "LOG first"));

        // Act
        var run = await harness.RunAsync(TestContext.CancellationToken);

        // Assert
        Assert.IsNull(run.Error, "no error");
        Assert.AreEqual("a.log,b.log,sub/c.log", string.Join(',', run.Summary.Files.Select(file => file.Path)), "files in lexical order");
        Assert.AreEqual(6L, run.Summary.Records, "records");
        Assert.AreEqual(6L, run.Summary.Lines, "lines");
        Assert.AreEqual(3, run.Summary.Count(ImportOutcome.Imported), "imported files");
        Assert.AreEqual(LineParser.Base.AddSeconds(1), run.Summary.First, "first capture time");
        Assert.AreEqual(LineParser.Base.AddSeconds(3), run.Summary.Last, "last capture time");
        Assert.AreEqual("test", run.Summary.Files[0].SourceType, "source type");
        Assert.AreEqual(6, harness.Store.Records.Count, "records in the store");
        Assert.IsFalse(run.Summary.Interrupted, "not interrupted");
        Assert.AreEqual(harness.Clock.GetUtcNow(), run.Summary.Started, "start time");
    }

    /// <summary>
    /// Importing the same content again stores nothing and reports it as already imported.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterImportsSameContentOnlyOnce()
    {
        // Arrange
        using var harness = new ImportHarness();

        harness.Write("a.log", TestInputs.Log(4));
        harness.Write("copy.log", TestInputs.Log(4));

        // Act
        var first = await harness.RunAsync(TestContext.CancellationToken);
        var second = await harness.RunAsync(TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(1, first.Summary.Count(ImportOutcome.Imported), "the first of two identical files is imported");
        Assert.AreEqual(1, first.Summary.Count(ImportOutcome.AlreadyImported), "the copy is the same content");
        Assert.AreEqual(2, second.Summary.Count(ImportOutcome.AlreadyImported), "a second run imports nothing");
        Assert.AreEqual(4, harness.Store.Records.Count, "records are stored once");
    }

    /// <summary>
    /// A gzip file is decompressed and its name is given to the parser without the suffix.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterDecompressesGzipFiles()
    {
        // Arrange
        using var harness = new ImportHarness();

        harness.Write("logs/syslog.1.GZ", TestInputs.Gzip(TestInputs.Log(5)));

        // Act
        var run = await harness.RunAsync(TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(5L, run.Summary.Records, "records");
        Assert.AreEqual("logs/syslog.1", harness.Parser.Seen[0].Name, "the parser gets the name without .gz");
        Assert.IsNotNull(harness.Parser.Seen[0].ModTime, "modification time of the file");
        Assert.AreEqual("logs/syslog.1.GZ", run.Summary.Files[0].Path, "the display path keeps the suffix");
    }

    /// <summary>
    /// A single file can be the root.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterImportsSingleFileRoot()
    {
        // Arrange
        using var harness = new ImportHarness();
        var path = harness.Write("only.log", TestInputs.Log(2));

        harness.Write("other.log", TestInputs.Log(9));

        // Act
        var run = await harness.RunAsync(TestContext.CancellationToken, path);

        // Assert
        Assert.AreEqual(1, run.Summary.Files.Count, "one file");
        Assert.AreEqual("only.log", run.Summary.Files[0].Path, "display path");
        Assert.AreEqual(2L, run.Summary.Records, "records");
    }

    /// <summary>
    /// A root that is a symbolic link is resolved once.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterResolvesSymbolicLinkRoot()
    {
        // Arrange
        using var harness = new ImportHarness();
        using var links = new TempDirectory();

        harness.Write("a.log", TestInputs.Log(2));

        var link = Path.Combine(links.Path, "root-link");

        Directory.CreateSymbolicLink(link, harness.Directory.Path);

        // Act
        var run = await harness.RunAsync(TestContext.CancellationToken, link);

        // Assert
        Assert.AreEqual(2L, run.Summary.Records, "records through the link");
    }

    /// <summary>
    /// Files that no parser claims, empty files and other formats are listed with the reason.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterListsFilesItCannotImport()
    {
        // Arrange
        using var harness = new ImportHarness();

        harness.Write("empty.log", string.Empty);
        harness.Write("note.txt", "just a note\n");
        harness.Write("old.bz2", TestInputs.Bytes("BZh91AY&SY"));
        harness.Write("data.xz", new byte[] { 0xfd, (byte)'7', (byte)'z', (byte)'X', (byte)'Z', 0x00, 1, 2 });
        harness.Write("twice.gz", TestInputs.Gzip(TestInputs.Gzip(TestInputs.Log(2))));
        harness.Write("good.log", TestInputs.Log(1));

        // Act
        var run = await harness.RunAsync(TestContext.CancellationToken);
        var reasons = run.Summary.Files.ToDictionary(file => file.Path, file => $"{file.Outcome}:{file.Reason}");

        // Assert
        Assert.AreEqual("Unrecognized:empty", reasons["empty.log"], "empty file");
        Assert.AreEqual("Unrecognized:no parser recognized the file", reasons["note.txt"], "no parser");
        Assert.AreEqual("Unrecognized:unsupported format: bzip2", reasons["old.bz2"], "bzip2");
        Assert.AreEqual("Unrecognized:unsupported format: xz", reasons["data.xz"], "xz");
        Assert.AreEqual("Unrecognized:compressed twice", reasons["twice.gz"], "gzip inside gzip");
        Assert.AreEqual("Imported:", reasons["good.log"], "the recognized file");
    }

    /// <summary>
    /// Symbolic links and special files are listed and never followed or opened.
    /// </summary>
    /// <param name="fallback">Whether paths are resolved without <c>openat2</c></param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ImporterNeverFollowsLinksOrOpensSpecialFiles(bool fallback)
    {
        // Arrange
        using var harness = new ImportHarness();
        using var outside = new TempDirectory();

        harness.Fallback = fallback;
        await File.WriteAllTextAsync(Path.Combine(outside.Path, "outside.log"), TestInputs.Log(3), TestContext.CancellationToken);
        File.CreateSymbolicLink(Path.Combine(harness.Directory.Path, "file-link.log"), Path.Combine(outside.Path, "outside.log"));
        Directory.CreateSymbolicLink(Path.Combine(harness.Directory.Path, "dir-link"), outside.Path);
        harness.Write("real.log", TestInputs.Log(1));

        // Act
        var run = await harness.RunAsync(TestContext.CancellationToken);
        var reasons = run.Summary.Files.ToDictionary(file => file.Path, file => file.Reason);

        // Assert
        Assert.AreEqual("symbolic link (not followed)", reasons["file-link.log"], "linked file");
        Assert.AreEqual("symbolic link (not followed)", reasons["dir-link"], "linked directory");
        Assert.AreEqual(1L, run.Summary.Records, "only the real file is imported");
        Assert.IsFalse(run.Summary.Files.Any(file => file.Path.StartsWith("dir-link/", StringComparison.Ordinal)), "nothing below the linked directory");
    }

    /// <summary>
    /// A path longer than the limit fails, and an empty directory imports nothing.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterFailsPathsThatAreTooLong()
    {
        // Arrange
        using var harness = new ImportHarness();
        var folder = new string('d', 200);
        var nested = string.Join('/', Enumerable.Repeat(folder, 6));

        harness.Write($"{nested}/file.log", TestInputs.Log(1));

        // Act
        var run = await harness.RunAsync(TestContext.CancellationToken);
        var failed = run.Summary.Files.Single(file => file.Outcome == ImportOutcome.Failed);

        // Assert
        Assert.AreEqual("path too long", failed.Reason, "reason");
        Assert.IsLessThanOrEqualTo(ImportLimits.MaxPathBytes, failed.Path.Length, "the display path is cut");
        Assert.AreEqual(0L, run.Summary.Records, "nothing imported");
    }

    /// <summary>
    /// An input with more entries than the limit is refused while scanning.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterRefusesTooManyEntries()
    {
        // Arrange
        using var harness = new ImportHarness();

        foreach (var number in Enumerable.Range(0, ImportLimits.MaxFiles + 1))
        {
            await File.WriteAllBytesAsync(Path.Combine(harness.Directory.Path, $"f{number}"), [], TestContext.CancellationToken);
        }

        // Act
        var run = await harness.RunAsync(TestContext.CancellationToken);

        // Assert
        Assert.IsInstanceOfType<TooManyFilesException>(run.Error, "error");
        Assert.AreEqual(0L, run.Summary.Records, "nothing imported");
        Assert.IsEmpty(harness.Events.Where(item => item.Event == ProgressEvent.Scanned), "pass 1 did not finish");
    }

    /// <summary>
    /// A missing root, an empty path and a special file as root are refused with a safe message.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterRefusesBadRoots()
    {
        // Arrange
        using var harness = new ImportHarness();
        var missing = Path.Combine(harness.Directory.Path, "missing");

        // Act
        var absent = await harness.RunAsync(TestContext.CancellationToken, missing);
        var empty = await harness.RunAsync(TestContext.CancellationToken, string.Empty);
        var device = await harness.RunAsync(TestContext.CancellationToken, "/dev/null");

        // Assert
        Assert.AreEqual("importer: opening the input: no such file or directory", absent.Error?.Message, "missing root");
        Assert.AreEqual("importer: opening the input: empty path", empty.Error?.Message, "empty path");
        Assert.AreEqual("importer: opening the input: neither a directory nor a regular file", device.Error?.Message, "device");
        Assert.IsEmpty(absent.Summary.Files, "no files");
    }

    /// <summary>
    /// The progress events report the scan, every file and its result.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterReportsProgress()
    {
        // Arrange
        using var harness = new ImportHarness();

        harness.Write("a.log", TestInputs.Log(3));
        harness.Write("b.txt", "note\n");

        // Act
        var run = await harness.RunAsync(TestContext.CancellationToken, null, options => options.ProgressBytes = 10);
        var kinds = harness.Events.Select(item => item.Event).ToList();
        var scanned = harness.Events.Single(item => item.Event == ProgressEvent.Scanned);
        var finished = harness.Events.Where(item => item.Event == ProgressEvent.FileFinished).Select(item => item.Result!.Path).ToList();

        // Assert
        Assert.IsNull(run.Error, "no error");
        Assert.AreEqual(2, scanned.Files, "files found");
        Assert.AreEqual(1, scanned.Pending, "files to import");
        Assert.Contains(ProgressEvent.ScanProgress, kinds, "the hash of a.log passed the progress step");
        Assert.Contains(ProgressEvent.FileStarted, kinds, "a file started");
        Assert.AreEqual("b.txt,a.log", string.Join(',', finished), "the listed file is reported during the scan, the imported one after");
        Assert.AreEqual(ProgressEvent.Scanned, kinds[kinds.IndexOf(ProgressEvent.Scanned)], "scanned event present");
    }

    /// <summary>
    /// Invalid options are refused.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterRefusesInvalidOptions()
    {
        // Arrange
        using var harness = new ImportHarness();

        // Act and Assert
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => harness.RunAsync(TestContext.CancellationToken, null, options => options.Parsers = null), "no parsers");
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => harness.RunAsync(TestContext.CancellationToken, null, options => options.Store = null), "no store");
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => harness.RunAsync(TestContext.CancellationToken, null, options => options.Clock = null), "no clock");
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => harness.RunAsync(TestContext.CancellationToken, null, options => options.BatchRecords = -1), "negative batch");
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => harness.RunAsync(TestContext.CancellationToken, null, options => options.BatchRecords = 20001), "huge batch");
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => harness.RunAsync(TestContext.CancellationToken, null, options => options.BatchBytes = -1), "negative bytes");
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => harness.RunAsync(TestContext.CancellationToken, null, options => options.ProgressBytes = -1), "negative progress");
    }

    #endregion // Methods
}