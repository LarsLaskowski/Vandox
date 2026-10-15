using Vandox.Backend.Reports;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="AsyncOnlyStream"/>
/// </summary>
[TestClass]
public class AsyncOnlyStreamTests
{
    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Both asynchronous writes and the asynchronous flush reach the stream below, which stays open when the wrapper is disposed.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task AsyncOnlyStreamWritesAsynchronouslyToTheInnerStream()
    {
        // Arrange
        var inner = new FlushCountingStream();
        var stream = new AsyncOnlyStream(inner);

        // Act
        await stream.WriteAsync(new byte[] { 1, 2, 3 }.AsMemory(), TestContext.CancellationToken);

        Func<byte[], int, int, CancellationToken, Task> writeArray = stream.WriteAsync;

        await writeArray(new byte[] { 9, 4, 5, 9 }, 1, 2, TestContext.CancellationToken);
        await stream.FlushAsync(TestContext.CancellationToken);
        await stream.DisposeAsync();

        // Assert
        Assert.AreSequenceEqual(new byte[] { 1, 2, 3, 4, 5 }, inner.ToArray(), "the bytes of both writes");
        Assert.AreEqual(1, inner.AsyncFlushes, "the flush reaches the inner stream");
        Assert.IsTrue(inner.CanWrite, "the inner stream stays open");
    }

    /// <summary>
    /// The synchronous flush does nothing and does not reach the stream below.
    /// </summary>
    [TestMethod]
    public void AsyncOnlyStreamFlushDoesNothing()
    {
        // Arrange
        var inner = new FlushCountingStream();
        var stream = new AsyncOnlyStream(inner);

        // Act
        stream.Flush();

        // Assert
        Assert.AreEqual(0, inner.SyncFlushes, "the inner stream is not flushed");
        Assert.AreEqual(0, inner.AsyncFlushes, "the inner stream is not flushed asynchronously");
        Assert.AreEqual(0L, inner.Length, "nothing is written");
    }

    /// <summary>
    /// A synchronous write, a read, a seek and the length and position are refused, and the capabilities are those of a write-only stream.
    /// </summary>
    [TestMethod]
    public void AsyncOnlyStreamRefusesSynchronousAndSeekingMembers()
    {
        // Arrange
        var inner = new FlushCountingStream();
        var stream = new AsyncOnlyStream(inner);

        // Act and Assert
        Assert.Throws<NotSupportedException>(() => stream.Write(new byte[] { 1 }, 0, 1), "write");
        Assert.Throws<NotSupportedException>(() => stream.Read(new byte[1], 0, 1), "read");
        Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin), "seek");
        Assert.Throws<NotSupportedException>(() => stream.SetLength(1), "set length");
        Assert.Throws<NotSupportedException>(() => _ = stream.Length, "length");
        Assert.Throws<NotSupportedException>(() => _ = stream.Position, "get position");
        Assert.Throws<NotSupportedException>(() => stream.Position = 1, "set position");
        Assert.IsFalse(stream.CanRead, "cannot read");
        Assert.IsFalse(stream.CanSeek, "cannot seek");
        Assert.IsTrue(stream.CanWrite, "can write");
        Assert.AreEqual(0L, inner.Length, "nothing reached the inner stream");
        Assert.Throws<ArgumentNullException>(() => new AsyncOnlyStream(null!), "the inner stream is required");
    }

    #endregion // Methods
}