using System.Text;

namespace Vandox.Import.Tests;

/// <summary>
/// Tests for <see cref="TarHeaderGuardStream"/>
/// </summary>
[TestClass]
public class TarHeaderGuardStreamTests
{
    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// An extended header above the limit is refused as soon as its header block is read, whatever its type.
    /// </summary>
    /// <param name="type">The type flag</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow('x')]
    [DataRow('g')]
    [DataRow('L')]
    [DataRow('K')]
    public async Task TarHeaderGuardStreamRefusesLargeMetadataHeader(char type)
    {
        // Arrange
        await using var guard = new TarHeaderGuardStream(new MemoryStream(Header("pax", type, OctalSize(ImportLimits.MaxTarMetadataBytes + 1))));
        var buffer = new byte[1024];

        // Act and assert
        await Assert.ThrowsExactlyAsync<TarMetadataTooLargeException>(async () => await guard.ReadAtLeastAsync(buffer, 512, true, TestContext.CancellationToken));
    }

    /// <summary>
    /// A base-256 size that does not fit is refused as well.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task TarHeaderGuardStreamRefusesHugeBase256Size()
    {
        // Arrange
        var size = new byte[12];

        size.AsSpan().Fill(0xFF);

        await using var guard = new TarHeaderGuardStream(new MemoryStream(Header("pax", 'x', size)));
        var buffer = new byte[1024];

        // Act and assert
        await Assert.ThrowsExactlyAsync<TarMetadataTooLargeException>(async () => await guard.ReadAtLeastAsync(buffer, 512, true, TestContext.CancellationToken));
    }

    /// <summary>
    /// Metadata at the limit, large file data, link entries and an invalid size field pass; only the block structure is followed.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task TarHeaderGuardStreamPassesEntriesWithinLimits()
    {
        // Arrange
        var stream = new MemoryStream();
        var data = new byte[1024];

        await stream.WriteAsync(Header("a", '0', OctalSize(1024)), TestContext.CancellationToken);
        await stream.WriteAsync(data, TestContext.CancellationToken);
        await stream.WriteAsync(Header("link", '2', OctalSize(5L << 30)), TestContext.CancellationToken);
        await stream.WriteAsync(Header("junk", '0', Encoding.ASCII.GetBytes("zzzzzzzzzzz\0")), TestContext.CancellationToken);
        await stream.WriteAsync(Header("pax", 'x', OctalSize(ImportLimits.MaxTarMetadataBytes)), TestContext.CancellationToken);
        await stream.WriteAsync(new byte[(int)ImportLimits.MaxTarMetadataBytes], TestContext.CancellationToken);
        await stream.WriteAsync(Header("b", '0', OctalSize(0)), TestContext.CancellationToken);
        stream.Position = 0;

        await using var guard = new TarHeaderGuardStream(stream);
        var read = 0;
        var buffer = new byte[700];

        // Act
        for (var count = await guard.ReadAsync(buffer, TestContext.CancellationToken); count > 0; count = await guard.ReadAsync(buffer, TestContext.CancellationToken))
        {
            read += count;
        }

        // Assert
        Assert.AreEqual(stream.Length, read, "every byte is passed on");
    }

    /// <summary>
    /// A PAX header (entry or global) with a size record is refused, however the value is written, because the tar reader
    /// and the guard could otherwise disagree about where the next header starts.
    /// </summary>
    /// <param name="type">The type flag of the PAX header</param>
    /// <param name="text">The records</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow('x', "10 size=0\n")]
    [DataRow('x', "13 size= 1024\n")]
    [DataRow('x', "13 size=+1024\n")]
    [DataRow('x', "9 size=\n")]
    [DataRow('g', "17 size=1000000000\n")]
    [DataRow('x', "21 path=a\n12 size=5\n")]
    public async Task TarHeaderGuardStreamRefusesPaxSizeRecord(char type, string text)
    {
        // Arrange
        var stream = new MemoryStream();
        var record = Encoding.ASCII.GetBytes(text);
        var padded = new byte[512];

        record.CopyTo(padded, 0);

        await stream.WriteAsync(Header("pax", type, OctalSize(record.Length)), TestContext.CancellationToken);
        await stream.WriteAsync(padded, TestContext.CancellationToken);
        await stream.WriteAsync(Header("file", '0', OctalSize(0)), TestContext.CancellationToken);
        stream.Position = 0;

        await using var guard = new TarHeaderGuardStream(stream);
        var buffer = new byte[4096];

        // Act and assert
        await Assert.ThrowsExactlyAsync<TarSizeRecordException>(async () => await DrainAsync(guard, buffer, TestContext.CancellationToken));
    }

    /// <summary>
    /// A PAX header without a size record passes, and the size field of the entry after it decides where the next header starts.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task TarHeaderGuardStreamPassesPaxHeaderWithoutSizeRecord()
    {
        // Arrange
        var stream = new MemoryStream();
        var record = Encoding.ASCII.GetBytes("27 path=var/log/size.log\n");
        var padded = new byte[512];

        record.CopyTo(padded, 0);

        await stream.WriteAsync(Header("pax", 'x', OctalSize(record.Length)), TestContext.CancellationToken);
        await stream.WriteAsync(padded, TestContext.CancellationToken);
        await stream.WriteAsync(Header("file", '0', OctalSize(512)), TestContext.CancellationToken);
        await stream.WriteAsync(new byte[512], TestContext.CancellationToken);
        await stream.WriteAsync(Header("bomb", 'x', OctalSize(1500000000)), TestContext.CancellationToken);
        stream.Position = 0;

        await using var guard = new TarHeaderGuardStream(stream);
        var buffer = new byte[4096];

        // Act and assert
        await Assert.ThrowsExactlyAsync<TarMetadataTooLargeException>(async () => await DrainAsync(guard, buffer, TestContext.CancellationToken));
    }

    /// <summary>
    /// Reads a stream to its end.
    /// </summary>
    /// <param name="stream">The stream</param>
    /// <param name="buffer">The read buffer</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>The number of bytes read</returns>
    private static async Task<long> DrainAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0L;

        for (var count = await stream.ReadAsync(buffer, cancellationToken); count > 0; count = await stream.ReadAsync(buffer, cancellationToken))
        {
            total += count;
        }

        return total;
    }

    /// <summary>
    /// Builds a tar header block.
    /// </summary>
    /// <param name="name">The entry name</param>
    /// <param name="type">The type flag</param>
    /// <param name="size">The size field of 12 bytes</param>
    /// <returns>The block</returns>
    private static byte[] Header(string name, char type, byte[] size)
    {
        var block = new byte[512];

        Encoding.ASCII.GetBytes(name).CopyTo(block, 0);
        size.CopyTo(block, 124);
        block[156] = (byte)type;

        return block;
    }

    /// <summary>
    /// Writes a size as the 12-byte octal field of a tar header.
    /// </summary>
    /// <param name="size">The size</param>
    /// <returns>The field</returns>
    private static byte[] OctalSize(long size)
    {
        return Encoding.ASCII.GetBytes(Convert.ToString(size, 8).PadLeft(11, '0') + "\0");
    }

    #endregion // Methods
}