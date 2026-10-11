using System.Security.Cryptography;
using System.Text;

using Vandox.Storage;

namespace Vandox.Import.Tests;

/// <summary>
/// Tests for <see cref="HashingStream"/>
/// </summary>
[TestClass]
public class HashingStreamTests
{
    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Every base whose hash equals the hash of the first bytes read is listed with the position after the last line feed in them, for every read size.
    /// </summary>
    /// <param name="chunk">The most bytes one read returns</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow(1)]
    [DataRow(7)]
    [DataRow(100)]
    [DataRow(1000000)]
    public async Task HashingStreamListsMatchingBasesWithTheirContinuationPoints(int chunk)
    {
        // Arrange
        var data = Content();
        var sizes = new long[] { 1, 5, 9, 10, 11, 99, 100, 101, 150, data.Length - 1, data.Length };
        List<ImportBase> bases = [.. sizes.Select(size => Base(data, size)), Unrelated("different", 150), Unrelated("whole", data.Length + 500)];

        await using var stream = new HashingStream(new ChunkedStream(data, chunk), 1000, NoProgress, bases);

        // Act
        await stream.CopyToAsync(Stream.Null, TestContext.CancellationToken);

        var expected = sizes.Where(size => Array.IndexOf(data, (byte)'\n', 0, (int)size) >= 0)
                            .Select(size => $"{size}:{Array.LastIndexOf(data, (byte)'\n', (int)size - 1) + 1}")
                            .Order()
                            .ToList();
        var actual = stream.Matches.Select(match => $"{match.Size}:{match.Offset}").Order().ToList();

        // Assert
        Assert.AreEqual(string.Join(',', expected), string.Join(',', actual), "the matching bases with their offsets");
        Assert.IsGreaterThan(0, expected.Count, "the test lists bases");
        Assert.AreEqual((long)data.Length, stream.BytesRead, "the bytes read are unchanged");
        Assert.AreSequenceEqual(SHA256.HashData(data), stream.GetHash(), "the total hash is unchanged");
    }

    /// <summary>
    /// A base whose prefix has no line feed is not listed, and a base with the offset after the first line feed is.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task HashingStreamDoesNotListABaseWithoutLineFeed()
    {
        // Arrange
        var data = Encoding.UTF8.GetBytes("LOG first line\nLOG second line\n");
        List<ImportBase> bases = [Base(data, 14), Base(data, 15)];

        await using var stream = new HashingStream(new MemoryStream(data), 1000, NoProgress, bases);

        // Act
        await stream.CopyToAsync(Stream.Null, TestContext.CancellationToken);

        // Assert
        Assert.HasCount(1, stream.Matches, "only the base with a line feed");
        Assert.AreEqual(15L, stream.Matches[0].Size, "its size");
        Assert.AreEqual(15L, stream.Matches[0].Offset, "its continuation point is its end");
    }

    /// <summary>
    /// Two bases of equal size with different hashes give only the one that matches, and the hash it carries is the one of the base.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task HashingStreamListsOnlyTheMatchingOfTwoBasesOfEqualSize()
    {
        // Arrange
        var data = Content();
        var right = Base(data, 120);
        var wrong = Unrelated("wrong", 120);

        await using var stream = new HashingStream(new ChunkedStream(data, 50), 1000, NoProgress, [wrong, right]);

        // Act
        await stream.CopyToAsync(Stream.Null, TestContext.CancellationToken);

        // Assert
        Assert.HasCount(1, stream.Matches, "one match");
        Assert.AreSequenceEqual(right.Sha256, stream.Matches[0].Sha256, "the hash of the matching base");
    }

    /// <summary>
    /// The progress marks are reported as before, with bases given.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task HashingStreamReportsProgressMarksWithBases()
    {
        // Arrange
        var data = Content();
        var marks = new List<long>();

        await using var stream = new HashingStream(new ChunkedStream(data, 33), 100, marks.Add, [Base(data, 70)]);

        // Act
        await stream.CopyToAsync(Stream.Null, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual("100,200,300", string.Join(',', marks.Where(mark => mark <= 300)), "a report at every multiple of the step");
    }

    /// <summary>
    /// Ignores a progress mark.
    /// </summary>
    /// <param name="mark">The mark</param>
    private static void NoProgress(long mark)
    {
        // Nothing to do.
    }

    /// <summary>
    /// Creates a base whose hash is not the hash of any prefix of a content.
    /// </summary>
    /// <param name="text">The text that is hashed</param>
    /// <param name="size">The size of the base</param>
    /// <returns>The base</returns>
    private static ImportBase Unrelated(string text, long size)
    {
        return new ImportBase
               {
                   Sha256 = SHA256.HashData(Encoding.UTF8.GetBytes(text)),
                   Size = size
               };
    }

    /// <summary>
    /// Builds a content of lines of different length, more than 300 bytes.
    /// </summary>
    /// <returns>The bytes</returns>
    private static byte[] Content()
    {
        return Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 40).Select(number => $"LOG {new string('x', number % 7)}{number}\n")));
    }

    /// <summary>
    /// Creates the base that is the first bytes of a content.
    /// </summary>
    /// <param name="data">The content</param>
    /// <param name="size">The size of the base</param>
    /// <returns>The base</returns>
    private static ImportBase Base(byte[] data, long size)
    {
        return new ImportBase
               {
                   Sha256 = SHA256.HashData(data.AsSpan(0, (int)size)),
                   Size = size
               };
    }

    #endregion // Methods
}