using System.Text;

using Vandox.Core.LogParsing;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="Utf8Text"/>
/// </summary>
[TestClass]
public class Utf8TextTests
{
    #region Methods

    /// <summary>
    /// Valid UTF-8 within the limit is returned unchanged and is not marked as cut.
    /// </summary>
    /// <param name="text">The text</param>
    /// <param name="limit">The limit in UTF-8 bytes</param>
    [TestMethod]
    [DataRow("", 0)]
    [DataRow("", 16)]
    [DataRow("hello", 5)]
    [DataRow("hello", 16384)]
    [DataRow("héllo € \U0001F600", 64)]
    public void Utf8TextDecodeValidTextWithinLimitIsUnchanged(string text, int limit)
    {
        // Arrange
        var bytes = Encoding.UTF8.GetBytes(text);

        // Act
        var decoded = Utf8Text.Decode(bytes, limit, out var truncated);

        // Assert
        Assert.AreEqual(text, decoded, "the text");
        Assert.IsFalse(truncated, "not cut");
    }

    /// <summary>
    /// Every invalid byte becomes one replacement character.
    /// </summary>
    [TestMethod]
    public void Utf8TextDecodeInvalidBytesBecomeReplacementCharacters()
    {
        // Arrange
        byte[] bytes = [(byte)'a', 0xFF, (byte)'b', 0xC3, (byte)'c', 0x80];

        // Act
        var decoded = Utf8Text.Decode(bytes, 100, out var truncated);

        // Assert
        Assert.AreEqual("a�b�c�", decoded, "the text with replacement characters");
        Assert.IsFalse(truncated, "not cut");
    }

    /// <summary>
    /// The result never exceeds the limit in UTF-8 bytes, is marked as cut and is valid text.
    /// </summary>
    /// <param name="kind">What the input consists of</param>
    /// <param name="count">The number of input bytes</param>
    /// <param name="limit">The limit in UTF-8 bytes</param>
    [TestMethod]
    [DataRow("invalid", 16384, 16384)]
    [DataRow("invalid", 20000, 16384)]
    [DataRow("invalid", 1024, 1024)]
    [DataRow("invalid", 5, 4)]
    [DataRow("ascii", 20000, 16384)]
    [DataRow("ascii", 10, 0)]
    [DataRow("invalid", 10, 0)]
    public void Utf8TextDecodeLongInputIsCutToTheLimit(string kind, int count, int limit)
    {
        // Arrange
        var bytes = Enumerable.Repeat(kind == "invalid" ? (byte)0xFF : (byte)'a', count).ToArray();

        // Act
        var decoded = Utf8Text.Decode(bytes, limit, out var truncated);

        // Assert
        Assert.IsLessThanOrEqualTo(limit, Encoding.UTF8.GetByteCount(decoded), "at most the limit in UTF-8 bytes");
        Assert.IsTrue(truncated, "marked as cut");
        Assert.IsTrue(IsWellFormed(decoded), "no half surrogate pair");

        if (kind == "ascii")
        {
            Assert.AreEqual(limit, decoded.Length, "an ASCII text is cut exactly at the limit");
        }
        else
        {
            Assert.AreEqual(limit / 3, decoded.Length, "an invalid-byte text keeps as many whole replacement characters as fit");
        }
    }

    /// <summary>
    /// A character that straddles the limit is dropped whole, with no replacement character in its place.
    /// </summary>
    /// <param name="text">The text</param>
    /// <param name="limit">The limit in UTF-8 bytes</param>
    /// <param name="expected">The expected result</param>
    [TestMethod]
    [DataRow("ab\U0001F600cd", 4, "ab")]
    [DataRow("ab\U0001F600cd", 5, "ab")]
    [DataRow("ab\U0001F600cd", 6, "ab\U0001F600")]
    [DataRow("a€b", 3, "a")]
    [DataRow("a€b", 4, "a€")]
    [DataRow("aéb", 2, "a")]
    [DataRow("\U0001F600", 3, "")]
    [DataRow("abc", 0, "")]
    public void Utf8TextDecodeCharacterOverTheLimitIsDroppedWhole(string text, int limit, string expected)
    {
        // Arrange
        var bytes = Encoding.UTF8.GetBytes(text);

        // Act
        var decoded = Utf8Text.Decode(bytes, limit, out var truncated);

        // Assert
        Assert.AreEqual(expected, decoded, "the cut text");
        Assert.IsTrue(truncated, "marked as cut");
    }

    /// <summary>
    /// A text of exactly the limit is not cut.
    /// </summary>
    [TestMethod]
    public void Utf8TextDecodeTextOfExactlyTheLimitIsNotCut()
    {
        // Arrange
        var bytes = Encoding.UTF8.GetBytes("ab\U0001F600");

        // Act
        var decoded = Utf8Text.Decode(bytes, 6, out var truncated);

        // Assert
        Assert.AreEqual("ab\U0001F600", decoded, "the text");
        Assert.IsFalse(truncated, "not cut");
    }

    /// <summary>
    /// Cutting a string gives the same result as decoding its bytes.
    /// </summary>
    /// <param name="text">The text</param>
    /// <param name="limit">The limit in UTF-8 bytes</param>
    /// <param name="expected">The expected result</param>
    /// <param name="expectedTruncated">Whether the text is expected to be cut</param>
    [TestMethod]
    [DataRow("ab\U0001F600cd", 4, "ab", true)]
    [DataRow("ab\U0001F600cd", 6, "ab\U0001F600", true)]
    [DataRow("ab\U0001F600", 6, "ab\U0001F600", false)]
    [DataRow("a€b", 3, "a", true)]
    [DataRow("hello", 5, "hello", false)]
    [DataRow("hello", 0, "", true)]
    [DataRow("", 0, "", false)]
    public void Utf8TextCutGivesTheSameCutAsDecode(string text, int limit, string expected, bool expectedTruncated)
    {
        // Act
        var cut = Utf8Text.Cut(text, limit, out var truncated);
        var decoded = Utf8Text.Decode(Encoding.UTF8.GetBytes(text), limit, out var decodedTruncated);

        // Assert
        Assert.AreEqual(expected, cut, "the cut text");
        Assert.AreEqual(expectedTruncated, truncated, "cut flag");
        Assert.AreEqual(cut, decoded, "same text as decoding the bytes");
        Assert.AreEqual(truncated, decodedTruncated, "same flag as decoding the bytes");
    }

    /// <summary>
    /// A string with a lone surrogate is cut without ever ending in half a pair.
    /// </summary>
    [TestMethod]
    public void Utf8TextCutNeverEndsInHalfASurrogatePair()
    {
        // Arrange
        var text = $"{new string('a', 10)}\U0001F600\U0001F600";

        // Act
        var cut = Utf8Text.Cut(text, 12, out var truncated);

        // Assert
        Assert.IsTrue(truncated, "marked as cut");
        Assert.IsTrue(IsWellFormed(cut), "well-formed text");
        Assert.IsLessThanOrEqualTo(12, Encoding.UTF8.GetByteCount(cut), "at most the limit");
    }

    /// <summary>
    /// Tells whether a string holds no lone surrogate.
    /// </summary>
    /// <param name="text">The text</param>
    /// <returns><c>true</c> when every surrogate has its partner</returns>
    private static bool IsWellFormed(string text)
    {
        return string.Equals(Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(text)), text, StringComparison.Ordinal);
    }

    #endregion // Methods
}