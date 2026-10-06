using System.Formats.Tar;
using System.Text;

using Vandox.Core.Model;

namespace Vandox.Import.Tests;

/// <summary>
/// Tests for <see cref="Importer"/> on tar archives
/// </summary>
[TestClass]
public class ImporterArchiveTests
{
    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// The entries of a tar archive and a tar.gz archive are imported in one pass each, with compressed entries decompressed.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterImportsArchiveEntries()
    {
        // Arrange
        using var harness = new ImportHarness();
        var plain = TestInputs.Tar(("var/log/a.log", TestInputs.Bytes(TestInputs.Log(2))), ("var/log/b.log.gz", TestInputs.Gzip(TestInputs.Log(3, "LOG gz"))), ("readme.txt", TestInputs.Bytes("note")));

        harness.Write("logs.tar", plain);
        harness.Write("more.tar.gz", TestInputs.Gzip(TestInputs.Tar(("c.log", TestInputs.Bytes(TestInputs.Log(4, "LOG more"))))));

        // Act
        var run = await harness.RunAsync(TestContext.CancellationToken);
        var byPath = run.Summary.Files.ToDictionary(file => file.Path);

        // Assert
        Assert.IsNull(run.Error, "no error");
        Assert.AreEqual(2L, byPath["logs.tar:var/log/a.log"].Records, "plain entry");
        Assert.AreEqual(3L, byPath["logs.tar:var/log/b.log.gz"].Records, "compressed entry");
        Assert.AreEqual(ImportOutcome.Unrecognized, byPath["logs.tar:readme.txt"].Outcome, "entry no parser claims");
        Assert.AreEqual(4L, byPath["more.tar.gz:c.log"].Records, "entry of a compressed archive");
        Assert.AreEqual(9L, run.Summary.Records, "all records");
        Assert.IsTrue(harness.Parser.Seen.Any(file => file.Name == "var/log/b.log"), "the parser gets the entry name without .gz");
    }

    /// <summary>
    /// Links, directories, special entries and archives inside archives are listed or skipped, never followed or opened.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterListsOddArchiveEntries()
    {
        // Arrange
        using var harness = new ImportHarness();
        var inner = TestInputs.Tar(("inner.log", TestInputs.Bytes(TestInputs.Log(1))));
        var link = new PaxTarEntry(TarEntryType.SymbolicLink, "link.log")
                   {
                       LinkName = "/etc/passwd"
                   };
        var hard = new PaxTarEntry(TarEntryType.HardLink, "hard.log")
                   {
                       LinkName = "a.log"
                   };
        var directory = new PaxTarEntry(TarEntryType.Directory, "folder/");
        var fifo = new PaxTarEntry(TarEntryType.Fifo, "pipe");
        var file = new PaxTarEntry(TarEntryType.RegularFile, "a.log")
                   {
                       DataStream = new MemoryStream(TestInputs.Bytes(TestInputs.Log(2)))
                   };
        var nested = new PaxTarEntry(TarEntryType.RegularFile, "nested.tar")
                     {
                         DataStream = new MemoryStream(inner)
                     };
        var global = new PaxGlobalExtendedAttributesTarEntry(new Dictionary<string, string>
                                                             {
                                                                 ["comment"] = "x"
                                                             });

        harness.Write("odd.tar", TestInputs.Tar(global, directory, link, hard, fifo, file, nested));

        // Act
        var run = await harness.RunAsync(TestContext.CancellationToken);
        var reasons = run.Summary.Files.ToDictionary(item => item.Path, item => item.Reason);

        // Assert
        Assert.AreEqual("symbolic link (not followed)", reasons["odd.tar:link.log"], "symbolic link");
        Assert.AreEqual("hard link (the content is in the linked entry)", reasons["odd.tar:hard.log"], "hard link");
        Assert.AreEqual("not a regular file", reasons["odd.tar:pipe"], "FIFO");
        Assert.AreEqual("archive inside an archive (not opened)", reasons["odd.tar:nested.tar"], "nested archive");
        Assert.AreEqual(string.Empty, reasons["odd.tar:a.log"], "the regular file is imported");
        Assert.IsFalse(reasons.ContainsKey("odd.tar:folder"), "directories are not listed");
        Assert.AreEqual(2L, run.Summary.Records, "only the regular file is imported");
    }

    /// <summary>
    /// Entry names are cleaned lexically, and a name that is too long fails.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterCleansEntryNamesAndFailsLongOnes()
    {
        // Arrange
        using var harness = new ImportHarness();
        var longName = string.Join('/', Enumerable.Repeat(new string('x', 200), 6));

        harness.Write("names.tar", TestInputs.Tar(("./a//b/../c.log", TestInputs.Bytes(TestInputs.Log(1))), ($"{longName}.log", TestInputs.Bytes(TestInputs.Log(1, "LOG long")))));

        // Act
        var run = await harness.RunAsync(TestContext.CancellationToken);

        // Assert
        Assert.AreEqual("a/c.log", harness.Parser.Seen[0].Name, "the cleaned name");
        Assert.AreEqual("path too long", run.Summary.Files.Single(file => file.Outcome == ImportOutcome.Failed).Reason, "long name");
    }

    /// <summary>
    /// A truncated archive fails as a whole after the entries before the cut were listed.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterFailsTruncatedArchive()
    {
        // Arrange
        using var harness = new ImportHarness();
        var whole = TestInputs.Tar(("a.log", TestInputs.Bytes(TestInputs.Log(2))), ("b.log", TestInputs.Bytes(TestInputs.Log(50))));

        harness.Write("cut.tar", whole[..(whole.Length / 2)]);

        // Act
        var run = await harness.RunAsync(TestContext.CancellationToken);

        // Assert
        Assert.IsNull(run.Error, "the run goes on");
        Assert.IsTrue(run.Summary.Files.Any(file => file.Outcome == ImportOutcome.Failed), "the archive fails");
    }

    /// <summary>
    /// An archive whose extended header declares gigabytes of metadata fails without being read into memory.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterFailsArchiveWithHugeMetadataHeader()
    {
        // Arrange
        using var harness = new ImportHarness();
        var header = new byte[512];

        Encoding.ASCII.GetBytes("pax").CopyTo(header, 0);
        Encoding.ASCII.GetBytes("14000000000\0").CopyTo(header, 124);
        Encoding.ASCII.GetBytes("ustar\000").CopyTo(header, 257);
        header[156] = (byte)'x';

        harness.Write("bomb.tar", header);

        // Act
        var run = await harness.RunAsync(TestContext.CancellationToken);

        // Assert
        Assert.IsNull(run.Error, "the run goes on");
        Assert.IsTrue(run.Summary.Files.Any(file => file.Outcome == ImportOutcome.Failed && file.Reason == "tar metadata header is too large"), $"the archive fails with the reason: {string.Join(";", run.Summary.Files.Select(file => $"{file.Outcome}/{file.Reason}"))}");
    }

    /// <summary>
    /// A corrupt gzip file fails without stopping the run.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ImporterFailsCorruptGzip()
    {
        // Arrange
        using var harness = new ImportHarness();
        var whole = TestInputs.Gzip(TestInputs.Log(5000));

        harness.Write("cut.gz", whole[..(whole.Length - 20)]);
        harness.Write("junk.gz", new byte[] { 0x1f, 0x8b, 0x08, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff });
        harness.Write("ok.log", TestInputs.Log(1));

        // Act
        var run = await harness.RunAsync(TestContext.CancellationToken);
        var results = run.Summary.Files.ToDictionary(file => file.Path);

        // Assert
        Assert.IsNull(run.Error, "the run goes on");
        Assert.AreEqual(ImportOutcome.Failed, results["cut.gz"].Outcome, "truncated gzip");
        Assert.AreEqual(ImportOutcome.Failed, results["junk.gz"].Outcome, "corrupt gzip");
        Assert.AreEqual(ImportOutcome.Imported, results["ok.log"].Outcome, "the other file");
    }

    #endregion // Methods
}