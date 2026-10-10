namespace Vandox.Import.Tests;

/// <summary>
/// Tests for <see cref="PathText"/> and <see cref="FormatSniffer"/>
/// </summary>
[TestClass]
public class PathTextTests
{
    #region Methods

    /// <summary>
    /// Names are cleaned lexically like a slash-separated path.
    /// </summary>
    /// <param name="name">The name</param>
    /// <param name="expected">The cleaned name</param>
    [TestMethod]
    [DataRow("a/b", "a/b")]
    [DataRow("./a//b/./c", "a/b/c")]
    [DataRow("a/b/../c", "a/c")]
    [DataRow("/etc/passwd", "etc/passwd")]
    [DataRow("/../x", "x")]
    [DataRow("../x", "../x")]
    [DataRow("a/../../x", "../x")]
    [DataRow("", ".")]
    [DataRow("/", ".")]
    [DataRow("a/..", ".")]
    public void PathTextCleanNameCleansLexically(string name, string expected)
    {
        // Act
        var cleaned = PathText.CleanName(name);

        // Assert
        Assert.AreEqual(expected, cleaned, "cleaned name");
    }

    /// <summary>
    /// A path is cut at a character boundary and the gzip suffix is removed in any case.
    /// </summary>
    [TestMethod]
    public void PathTextCutsPathsAndTrimsGzipSuffix()
    {
        // Arrange
        var longPath = new string('ä', ImportLimits.MaxPathBytes);

        // Act
        var cut = PathText.Cut(longPath);

        // Assert
        Assert.AreEqual(ImportLimits.MaxPathBytes / 2, cut.Length, "cut at a character boundary");
        Assert.AreEqual("short", PathText.Cut("short"), "a short path is kept");
        Assert.IsTrue(PathText.IsTooLong(longPath), "too long");
        Assert.IsFalse(PathText.IsTooLong("short"), "not too long");
        Assert.AreEqual("a.log", PathText.TrimGzip("a.log.GZ"), "suffix removed");
        Assert.AreEqual(".gz", PathText.TrimGzip(".gz"), "a name that is only the suffix is kept");
        Assert.AreEqual("a.log", PathText.TrimGzip("a.log"), "no suffix");
    }

    /// <summary>
    /// The format of a content is told from its first bytes.
    /// </summary>
    [TestMethod]
    public void FormatSnifferClassifiesHeads()
    {
        // Arrange
        var tar = new byte[512];

        "ustar\0"u8.CopyTo(tar.AsSpan(257));
        "00"u8.CopyTo(tar.AsSpan(263));

        var gnu = new byte[512];

        "ustar  \0"u8.CopyTo(gnu.AsSpan(257));

        // Act
        var empty = FormatSniffer.Sniff([]);
        var gzip = FormatSniffer.Sniff([0x1f, 0x8b, 0x08, 0]);
        var plain = FormatSniffer.Sniff("LOG line"u8);
        var ustar = FormatSniffer.Sniff(tar);
        var gnuTar = FormatSniffer.Sniff(gnu);
        var zip = FormatSniffer.Sniff("PK\x03\x04rest"u8);
        var zstd = FormatSniffer.Sniff([0x28, 0xb5, 0x2f, 0xfd, 1]);
        var lz4 = FormatSniffer.Sniff([0x04, 0x22, 0x4d, 0x18, 1]);
        var sevenZip = FormatSniffer.Sniff([(byte)'7', (byte)'z', 0xbc, 0xaf, 0x27, 0x1c]);

        // Assert
        Assert.AreEqual(ContentFormat.Empty, empty.Format, "empty");
        Assert.AreEqual(ContentFormat.Gzip, gzip.Format, "gzip");
        Assert.AreEqual(ContentFormat.Plain, plain.Format, "plain");
        Assert.AreEqual(ContentFormat.Tar, ustar.Format, "USTAR");
        Assert.AreEqual(ContentFormat.Tar, gnuTar.Format, "GNU tar");
        Assert.AreEqual("zip", zip.Name, "zip");
        Assert.AreEqual("zstd", zstd.Name, "zstd");
        Assert.AreEqual("lz4", lz4.Name, "lz4");
        Assert.AreEqual("7z", sevenZip.Name, "7z");
    }

    #endregion // Methods
}