using System.Text;

using Vandox.Core.Wire;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="Crc32"/>
/// </summary>
[TestClass]
public class Crc32Tests
{
    #region Methods

    /// <summary>
    /// The checksums of well-known inputs match the published reference values.
    /// </summary>
    [TestMethod]
    public void Crc32MatchesReferenceVectors()
    {
        // Arrange
        var vectors = new (string Text, uint Expected)[]
                      {
                          (string.Empty, 0x00000000u),
                          ("a", 0xe8b7be43u),
                          ("123456789", 0xcbf43926u),
                          ("The quick brown fox jumps over the lazy dog", 0x414fa339u)
                      };

        foreach (var (text, expected) in vectors)
        {
            // Act
            var actual = Crc32.Update(0, Encoding.ASCII.GetBytes(text));

            // Assert
            Assert.AreEqual(expected, actual, $"CRC-32 of \"{text}\"");
        }
    }

    /// <summary>
    /// Continuing a checksum over the pieces of the data gives the checksum of the whole, and the table-driven result equals a bitwise computation.
    /// </summary>
    [TestMethod]
    public void Crc32UpdateOverPiecesEqualsWholeAndBitwiseReference()
    {
        // Arrange
        var data = Enumerable.Range(0, 1000).Select(value => (byte)((value * 31) + 7)).ToArray();

        // Act
        var whole = Crc32.Update(0, data);
        var pieces = Crc32.Update(Crc32.Update(Crc32.Update(0, data.AsSpan(0, 1)), data.AsSpan(1, 600)), data.AsSpan(601));

        // Assert
        Assert.AreEqual(GzipFixture.Reference(data), whole, "whole data against the bitwise reference");
        Assert.AreEqual(whole, pieces, "data in three pieces");
    }

    #endregion // Methods
}