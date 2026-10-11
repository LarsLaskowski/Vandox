using System.Security.Cryptography;
using System.Text;

namespace Vandox.Import.Tests;

/// <summary>
/// Tests for <see cref="ContentHead"/>
/// </summary>
[TestClass]
public class ContentHeadTests
{
    #region Constants

    private const int HeadBytes = 4096;

    #endregion // Constants

    #region Methods

    /// <summary>
    /// The key is the SHA-256 of the head up to and including its first line feed.
    /// </summary>
    [TestMethod]
    public void ContentHeadKeyHashesTheFirstLineWithItsLineFeed()
    {
        // Arrange
        var head = Encoding.UTF8.GetBytes("LOG first line\nLOG second line\n");

        // Act
        var key = ContentHead.Key(head);

        // Assert
        Assert.HasCount(32, key, "32 bytes");
        Assert.AreSequenceEqual(SHA256.HashData(Encoding.UTF8.GetBytes("LOG first line\n")), key, "the hash of the first line with its line feed");
    }

    /// <summary>
    /// Two heads with the same first line and different later bytes give equal keys, and another first line another key.
    /// </summary>
    [TestMethod]
    public void ContentHeadKeyIgnoresBytesAfterTheFirstLine()
    {
        // Arrange
        var first = Encoding.UTF8.GetBytes("LOG same\nLOG one\n");
        var second = Encoding.UTF8.GetBytes("LOG same\nsomething else entirely");
        var other = Encoding.UTF8.GetBytes("LOG other\nLOG one\n");

        // Act
        var firstKey = ContentHead.Key(first);
        var secondKey = ContentHead.Key(second);
        var otherKey = ContentHead.Key(other);

        // Assert
        Assert.AreSequenceEqual(firstKey, secondKey, "same first line, same key");
        Assert.AreNotEqual(Convert.ToHexString(firstKey), Convert.ToHexString(otherKey), "another first line, another key");
    }

    /// <summary>
    /// A head without a line feed is hashed as a whole.
    /// </summary>
    [TestMethod]
    public void ContentHeadKeyHashesTheWholeHeadWithoutLineFeed()
    {
        // Arrange
        var head = Encoding.UTF8.GetBytes("LOG only-part");

        // Act
        var key = ContentHead.Key(head);

        // Assert
        Assert.AreSequenceEqual(SHA256.HashData(head), key, "the hash of the whole head");
    }

    /// <summary>
    /// A first line longer than the head hashes the first 4,096 bytes, also when the line feed lies beyond them.
    /// </summary>
    /// <param name="lineFeedAt">The index of the line feed; -1 when there is none</param>
    [TestMethod]
    [DataRow(-1)]
    [DataRow(4500)]
    public void ContentHeadKeyCutsALongFirstLineAtTheHeadSize(int lineFeedAt)
    {
        // Arrange
        var head = Enumerable.Repeat((byte)'a', 5000).ToArray();

        if (lineFeedAt >= 0)
        {
            head[lineFeedAt] = (byte)'\n';
        }

        // Act
        var key = ContentHead.Key(head);

        // Assert
        Assert.AreSequenceEqual(SHA256.HashData(head.AsSpan(0, HeadBytes)), key, "the hash of the first 4,096 bytes");
    }

    /// <summary>
    /// A head that starts with a line feed hashes that single byte.
    /// </summary>
    [TestMethod]
    public void ContentHeadKeyOfALeadingLineFeedHashesThatByte()
    {
        // Arrange
        var head = Encoding.UTF8.GetBytes("\nLOG after the empty line\n");

        // Act
        var key = ContentHead.Key(head);

        // Assert
        Assert.AreSequenceEqual(SHA256.HashData([(byte)'\n']), key, "the hash of the single line feed");
    }

    /// <summary>
    /// A line feed at the last byte of a full head is part of the key.
    /// </summary>
    [TestMethod]
    public void ContentHeadKeyIncludesALineFeedAtTheLastByteOfTheHead()
    {
        // Arrange
        var head = Enumerable.Repeat((byte)'a', HeadBytes).ToArray();

        head[HeadBytes - 1] = (byte)'\n';

        // Act
        var key = ContentHead.Key(head);

        // Assert
        Assert.AreSequenceEqual(SHA256.HashData(head), key, "the whole head is the first line");
    }

    #endregion // Methods
}