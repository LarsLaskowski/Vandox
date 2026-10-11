using System.Security.Cryptography;
using System.Text;

using Vandox.Storage;

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
    /// With a writer set, the stream passes the first 16 bytes of the SHA-256 of the bytes up to every line end in rows of at most 4096 hashes, whatever the size of the reads; the rows are contiguous, and a last line without a line feed has no hash.
    /// </summary>
    /// <param name="chunk">The most bytes one read returns</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow(1)]
    [DataRow(7)]
    [DataRow(4096)]
    public async Task ContentStreamPassesLineHashesInRowsWhateverTheReadSize(int chunk)
    {
        // Arrange
        var data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("x\n", 9000)) + "tail");
        var rows = new List<ImportLineHashes>();
        using var stream = new ContentStream(new ChunkedStream(data, chunk), data.Length, NoProgress);

        stream.LineHashWriter = Keep(rows);

        // Act
        await stream.CopyToAsync(Stream.Null, TestContext.CancellationToken);
        await stream.FlushLineHashesAsync(TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(ExpectedLineHashes.Describe(ExpectedLineHashes.Rows(data)), ExpectedLineHashes.Describe(rows), "the rows");
        Assert.HasCount(3, rows, "4096, 4096 and 808 hashes");
        Assert.AreEqual(0L, rows[0].FirstOffset, "the first row starts at 0");
        Assert.AreEqual(rows[0].LastOffset, rows[1].FirstOffset, "each further row starts at the last line end of the row before");
        Assert.AreEqual(2L * 9000, rows[2].LastOffset, "the last row ends at the last line end, not at the end of the tail");
        Assert.AreEqual(0, stream.PendingLineHashes, "nothing is left after the flush");
    }

    /// <summary>
    /// The hashes cover the part that was skipped, and the rows are those of the whole content.
    /// </summary>
    /// <param name="chunk">The most bytes one read returns</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow(1)]
    [DataRow(7)]
    [DataRow(1000000)]
    public async Task ContentStreamIncludesTheSkippedPartInLineHashes(int chunk)
    {
        // Arrange
        var data = Encoding.UTF8.GetBytes(TestInputs.Log(20));
        var count = Encoding.UTF8.GetByteCount(TestInputs.Log(8));
        var rows = new List<ImportLineHashes>();
        using var stream = new ContentStream(new ChunkedStream(data, chunk), data.Length, NoProgress);

        stream.LineHashWriter = Keep(rows);

        // Act
        await stream.SkipAsync(count, TestContext.CancellationToken);
        await stream.CopyToAsync(Stream.Null, TestContext.CancellationToken);
        await stream.FlushLineHashesAsync(TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(ExpectedLineHashes.Describe(ExpectedLineHashes.Rows(data)), ExpectedLineHashes.Describe(rows), "one row with a hash for every line end of the whole content");
        Assert.AreEqual(20, rows.Sum(row => row.Hashes.Length) / StorageLimits.LineHashBytes, "twenty line ends");
    }

    /// <summary>
    /// The hashes stop at the limit of the stream.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ContentStreamKeepsNoLineHashBeyondTheLimit()
    {
        // Arrange
        var data = Encoding.UTF8.GetBytes(TestInputs.Log(10));
        var limit = Encoding.UTF8.GetByteCount(TestInputs.Log(6));
        var rows = new List<ImportLineHashes>();
        using var stream = new ContentStream(new ChunkedStream(data, 5), limit, NoProgress);

        stream.LineHashWriter = Keep(rows);

        // Act
        await stream.CopyToAsync(Stream.Null, TestContext.CancellationToken);
        await stream.FlushLineHashesAsync(TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(ExpectedLineHashes.Describe(ExpectedLineHashes.Rows(data[..limit])), ExpectedLineHashes.Describe(rows), "the rows of the first six lines");
    }

    /// <summary>
    /// A content without a line feed, and an empty one, write no row, and flushing again writes nothing more.
    /// </summary>
    /// <param name="text">The content</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("")]
    [DataRow("no line feed")]
    public async Task ContentStreamWritesNoRowWithoutALineFeed(string text)
    {
        // Arrange
        var data = Encoding.UTF8.GetBytes(text);
        var rows = new List<ImportLineHashes>();
        using var stream = new ContentStream(new MemoryStream(data), data.Length, NoProgress);

        stream.LineHashWriter = Keep(rows);

        // Act
        await stream.CopyToAsync(Stream.Null, TestContext.CancellationToken);
        await stream.FlushLineHashesAsync(TestContext.CancellationToken);
        await stream.FlushLineHashesAsync(TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(rows, "no row");
        Assert.AreEqual(0, stream.PendingLineHashes, "nothing pending");
    }

    /// <summary>
    /// Flushing twice writes the partial row once.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ContentStreamFlushWritesThePartialRowOnce()
    {
        // Arrange
        var data = Encoding.UTF8.GetBytes(TestInputs.Log(5));
        var rows = new List<ImportLineHashes>();
        using var stream = new ContentStream(new MemoryStream(data), data.Length, NoProgress);

        stream.LineHashWriter = Keep(rows);
        await stream.CopyToAsync(Stream.Null, TestContext.CancellationToken);

        // Act
        var pending = stream.PendingLineHashes;

        await stream.FlushLineHashesAsync(TestContext.CancellationToken);
        await stream.FlushLineHashesAsync(TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(5, pending, "five hashes wait for the flush");
        Assert.HasCount(1, rows, "one row");
    }

    /// <summary>
    /// Every complete row is written before the next read of the inner stream starts, and the number of hashes that wait is bounded by 4095 plus the bytes of the last read.
    /// </summary>
    /// <param name="read">The size of the buffer of every read</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow(1)]
    [DataRow(7)]
    [DataRow(3000)]
    [DataRow(100000)]
    public async Task ContentStreamWritesCompleteRowsBeforeTheNextInnerRead(int read)
    {
        // Arrange
        var data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("x\n", 9000)));
        var rows = new List<ImportLineHashes>();
        var violations = new List<string>();
        var inner = new ChunkedStream(data, 3000);
        var hooked = new HookedStream(inner, () => CheckRows(inner, rows, violations));
        using var stream = new ContentStream(hooked, data.Length, NoProgress);
        var buffer = new byte[read];
        var excess = new List<string>();

        var keep = Keep(rows);

        stream.LineHashWriter = async (row, token) =>
                                {
                                    await Task.Yield();
                                    await keep(row, token);
                                };

        // Act
        int count;

        do
        {
            count = await stream.ReadAsync(buffer, TestContext.CancellationToken);

            if (stream.PendingLineHashes > StorageLimits.MaxLineHashes - 1 + count)
            {
                excess.Add($"{stream.PendingLineHashes} hashes pending after a read of {count} bytes");
            }
        }
        while (count > 0);

        await stream.FlushLineHashesAsync(TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(violations, "a read of the inner stream started while a complete row was not written");
        Assert.IsEmpty(excess, "hashes waiting after a read");
        Assert.HasCount(3, rows, "all rows are written");
    }

    /// <summary>
    /// A failure of the writer reaches the caller of the read, and the inner stream is not read again.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ContentStreamPassesTheFailureOfTheWriterAndReadsNoMore()
    {
        // Arrange
        var data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("x\n", 9000)));
        var inner = new ChunkedStream(data, 3000);
        var hooked = new HookedStream(inner, Nothing);
        using var stream = new ContentStream(hooked, data.Length, NoProgress);
        var failure = new InvalidOperationException("the writer failed");

        stream.LineHashWriter = (_, _) => Task.FromException(failure);

        // Act
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => stream.CopyToAsync(Stream.Null, TestContext.CancellationToken), "failing writer");

        // Assert
        Assert.AreSame(failure, error, "the exception of the writer");
        Assert.AreEqual(3, hooked.Reads, "the row was complete after the third read, so the fourth read never started");
        Assert.AreEqual(9000, inner.Consumed, "three reads of 3000 bytes");
    }

    /// <summary>
    /// The writer gets the token of the read, and the flush gets its own.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ContentStreamPassesTheTokenToTheWriter()
    {
        // Arrange
        var data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("x\n", 5000)));
        var tokens = new List<CancellationToken>();
        using var readSource = new CancellationTokenSource();
        using var flushSource = new CancellationTokenSource();
        using var stream = new ContentStream(new ChunkedStream(data, 6000), data.Length, NoProgress);
        var buffer = new byte[6000];

        stream.LineHashWriter = (_, token) =>
                                {
                                    tokens.Add(token);

                                    return Task.CompletedTask;
                                };

        // Act
        var reads = new[]
                    {
                        await stream.ReadAsync(buffer, readSource.Token),
                        await stream.ReadAsync(buffer, readSource.Token),
                        await stream.ReadAsync(buffer, readSource.Token)
                    };

        await stream.FlushLineHashesAsync(flushSource.Token);

        // Assert
        Assert.AreEqual("6000,4000,0", string.Join(',', reads), "the bytes of the three reads");
        Assert.HasCount(2, tokens, "one full row and the rest");
        Assert.AreEqual(readSource.Token, tokens[0], "the full row is written by a read, with its token");
        Assert.AreEqual(flushSource.Token, tokens[1], "the rest is written by the flush, with its token");
    }

    /// <summary>
    /// Without a writer no hash is kept and nothing is written, and the synchronous read works as before.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ContentStreamKeepsNoLineHashWithoutAWriter()
    {
        // Arrange
        var data = Encoding.UTF8.GetBytes(TestInputs.Log(50));
        using var stream = new ContentStream(new MemoryStream(data), data.Length, NoProgress);

        // Act
        await stream.CopyToAsync(Stream.Null, TestContext.CancellationToken);
        await stream.FlushLineHashesAsync(TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0, stream.PendingLineHashes, "no hash is computed");
        Assert.AreEqual(50L, stream.Lines, "lines are still counted");
        Assert.AreSequenceEqual(SHA256.HashData(data), stream.GetHash(), "the hash of the content is still kept");
    }

    /// <summary>
    /// Without a writer the synchronous read works as before.
    /// </summary>
    [TestMethod]
    public void ContentStreamReadsSynchronouslyWithoutAWriter()
    {
        // Arrange
        var data = Encoding.UTF8.GetBytes(TestInputs.Log(50));
        using var stream = new ContentStream(new MemoryStream(data), data.Length, NoProgress);
        var buffer = new byte[10];

        // Act
        var first = stream.Read(buffer);

        // Assert
        Assert.AreEqual(10, first, "ten bytes are read");
        Assert.AreEqual(10L, stream.BytesRead, "and counted");
    }

    /// <summary>
    /// With a writer set, the synchronous read is refused.
    /// </summary>
    [TestMethod]
    public void ContentStreamRefusesSynchronousReadWhileLineHashesAreKept()
    {
        // Arrange
        var data = Encoding.UTF8.GetBytes(TestInputs.Log(5));
        using var stream = new ContentStream(new MemoryStream(data), data.Length, NoProgress);

        stream.LineHashWriter = Keep([]);

        // Act
        var span = Assert.ThrowsExactly<NotSupportedException>(() => stream.Read(new byte[10].AsSpan()), "span read");
        var array = Assert.ThrowsExactly<NotSupportedException>(() => stream.Read(new byte[10], 0, 10), "array read");

        // Assert
        Assert.AreEqual("the content is read asynchronously while line hashes are kept", span.Message, "span read message");
        Assert.AreEqual("the content is read asynchronously while line hashes are kept", array.Message, "array read message");
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
    /// Records a violation when a read of the inner stream starts while a complete row is not written.
    /// </summary>
    /// <param name="inner">The inner stream of two-byte lines</param>
    /// <param name="rows">The rows written so far</param>
    /// <param name="violations">Receives the violations</param>
    private static void CheckRows(ChunkedStream inner, List<ImportLineHashes> rows, List<string> violations)
    {
        var ends = inner.Consumed / 2;

        if (rows.Count < ends / StorageLimits.MaxLineHashes)
        {
            violations.Add($"{ends} line ends were read, {rows.Count} rows are written");
        }
    }

    /// <summary>
    /// Does nothing before a read.
    /// </summary>
    private static void Nothing()
    {
        // Nothing to do.
    }

    /// <summary>
    /// Creates a writer that keeps a copy of every row it gets.
    /// </summary>
    /// <param name="rows">Receives the copies</param>
    /// <returns>The writer</returns>
    private static Func<ImportLineHashes, CancellationToken, Task> Keep(List<ImportLineHashes> rows)
    {
        return (row, _) =>
               {
                   rows.Add(new ImportLineHashes
                            {
                                FileId = row.FileId,
                                Attempt = row.Attempt,
                                FirstOffset = row.FirstOffset,
                                LastOffset = row.LastOffset,
                                Hashes = [.. row.Hashes]
                            });

                   return Task.CompletedTask;
               };
    }

    #endregion // Methods
}