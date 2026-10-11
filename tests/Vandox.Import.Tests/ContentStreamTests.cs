using System.Security.Cryptography;
using System.Text;

namespace Vandox.Import.Tests;

/// <summary>
/// Tests for <see cref="ContentStream"/>
/// </summary>
[TestClass]
public class ContentStreamTests
{
    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Skipping reads and hashes the bytes, returns the line feeds in them, counts no line and leaves the next byte unread, for every read size.
    /// </summary>
    /// <param name="chunk">The most bytes one read returns</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow(1)]
    [DataRow(7)]
    [DataRow(1000000)]
    public async Task ContentStreamSkipHashesTheBytesAndReturnsTheLineFeeds(int chunk)
    {
        // Arrange
        var data = Encoding.UTF8.GetBytes(TestInputs.Log(10));
        var count = Encoding.UTF8.GetByteCount(TestInputs.Log(3));
        var inner = new ChunkedStream(data, chunk);
        using var stream = new ContentStream(inner, data.Length, NoProgress);

        // Act
        var skipped = await stream.SkipAsync(count, TestContext.CancellationToken);
        var linesAfterSkip = stream.Lines;
        var next = new byte[1];
        var read = await stream.ReadAsync(next, TestContext.CancellationToken);

        await stream.CopyToAsync(Stream.Null, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(3L, skipped, "three line feeds in the skipped bytes");
        Assert.AreEqual(0L, linesAfterSkip, "the skipped lines are not counted");
        Assert.AreEqual(1, read, "one byte read");
        Assert.AreEqual(data[count], next[0], "the byte after the skipped ones is read next");
        Assert.AreEqual(7L, stream.Lines, "only the lines after the skip are counted");
        Assert.AreEqual((long)data.Length, stream.BytesRead, "the skipped bytes count as read");
        Assert.AreSequenceEqual(SHA256.HashData(data), stream.GetHash(), "the hash covers the whole content");
    }

    /// <summary>
    /// Skipping reports no progress, and the progress marks count the lines after the skip.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ContentStreamSkipReportsNoProgress()
    {
        // Arrange
        var marks = new List<long>();
        var skippedLines = (int)ImportLimits.ProgressLines + 5;
        var data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("L\n", skippedLines + 3)));
        using var stream = new ContentStream(new MemoryStream(data), data.Length, marks.Add);

        // Act
        var skipped = await stream.SkipAsync(skippedLines * 2L, TestContext.CancellationToken);

        await stream.CopyToAsync(Stream.Null, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual((long)skippedLines, skipped, "the line feeds of the skipped bytes");
        Assert.IsEmpty(marks, "no progress for the skipped lines, and the lines after them do not reach a mark");
        Assert.AreEqual(3L, stream.Lines, "the lines after the skip");
    }

    /// <summary>
    /// Skipping never reads beyond the limit.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ContentStreamSkipNeverReadsBeyondTheLimit()
    {
        // Arrange
        var data = Encoding.UTF8.GetBytes(TestInputs.Log(10));
        var inner = new ChunkedStream(data, 1000);
        var limit = Encoding.UTF8.GetByteCount(TestInputs.Log(2));
        using var stream = new ContentStream(inner, limit, NoProgress);

        // Act
        var skipped = await stream.SkipAsync(data.Length, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(2L, skipped, "the line feeds within the limit");
        Assert.AreEqual((long)limit, stream.BytesRead, "no byte beyond the limit is read");
    }

    /// <summary>
    /// Skipping returns early at the end of the input.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ContentStreamSkipReturnsEarlyAtTheEndOfTheInput()
    {
        // Arrange
        var data = Encoding.UTF8.GetBytes(TestInputs.Log(2));
        using var stream = new ContentStream(new ChunkedStream(data, 5), 1000, NoProgress);

        // Act
        var skipped = await stream.SkipAsync(500, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(2L, skipped, "the line feeds of the whole input");
        Assert.AreEqual((long)data.Length, stream.BytesRead, "the whole input is read");
    }

    /// <summary>
    /// Skipping nothing reads nothing.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ContentStreamSkipOfZeroReadsNothing()
    {
        // Arrange
        var inner = new ChunkedStream(Encoding.UTF8.GetBytes(TestInputs.Log(2)), 5);
        using var stream = new ContentStream(inner, 1000, NoProgress);

        // Act
        var skipped = await stream.SkipAsync(0, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0L, skipped, "no line feeds");
        Assert.AreEqual(0, inner.Consumed, "nothing read from the inner stream");
    }

    /// <summary>
    /// A cancelled token stops the skip.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ContentStreamSkipHonoursCancellation()
    {
        // Arrange
        using var cancelled = new CancellationTokenSource();
        using var stream = new ContentStream(new MemoryStream(Encoding.UTF8.GetBytes(TestInputs.Log(5))), 1000, NoProgress);

        await cancelled.CancelAsync();

        // Act and Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() => stream.SkipAsync(10, cancelled.Token), "cancelled skip");
    }

    /// <summary>
    /// Ignores a progress mark.
    /// </summary>
    /// <param name="mark">The mark</param>
    private static void NoProgress(long mark)
    {
        // Nothing to do.
    }

    #endregion // Methods
}