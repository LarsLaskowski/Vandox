using System.Text;

using Vandox.Core.Wire;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="GzipMemberReader"/>
/// </summary>
[TestClass]
public class GzipMemberReaderTests
{
    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// The content of concatenated members is read as one stream, by the asynchronous read.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task GzipMemberReaderReadsConcatenatedMembers()
    {
        // Arrange
        var first = Encoding.ASCII.GetBytes("first member\n");
        var second = Encoding.ASCII.GetBytes("second member\n");
        var third = Encoding.ASCII.GetBytes("third member, with a name\n");
        byte[] input = [.. GzipFixture.Member(first), .. GzipFixture.Member(second), .. GzipFixture.Member(third, nameBytes: 5)];
        byte[] expected = [.. first, .. second, .. third];

        // Act
        using var asyncInput = new MemoryStream(input);
        using var asyncOutput = new MemoryStream();

        await using (var reader = new GzipMemberReader(asyncInput))
        {
            await reader.CopyToAsync(asyncOutput, TestContext.CancellationToken);
        }

        // Assert
        Assert.AreSequenceEqual(expected, asyncOutput.ToArray(), "content read asynchronously");
    }

    /// <summary>
    /// The content of concatenated members is read as one stream by the synchronous read, in pieces smaller than a member.
    /// </summary>
    [TestMethod]
    public void GzipMemberReaderReadsConcatenatedMembersSynchronously()
    {
        // Arrange
        var first = Encoding.ASCII.GetBytes("first member\n");
        var second = Encoding.ASCII.GetBytes("second member\n");
        byte[] input = [.. GzipFixture.Member(first), .. GzipFixture.Member(second, commentBytes: 3)];
        using var output = new MemoryStream();
        using var reader = new GzipMemberReader(new MemoryStream(input));
        var buffer = new byte[7];

        // Act
        for (var read = reader.Read(buffer, 0, buffer.Length); read > 0; read = reader.Read(buffer, 0, buffer.Length))
        {
            output.Write(buffer, 0, read);
        }

        // Assert
        Assert.AreSequenceEqual([.. first, .. second], output.ToArray(), "content read in pieces of 7 bytes");
    }

    #endregion // Methods
}