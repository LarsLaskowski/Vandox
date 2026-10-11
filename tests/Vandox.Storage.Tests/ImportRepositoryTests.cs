namespace Vandox.Storage.Tests;

/// <summary>
/// Tests for <see cref="ImportRepository"/>
/// </summary>
[TestClass]
public class ImportRepositoryTests
{
    #region Methods

    /// <summary>
    /// A SHA-256 is found when its first 16 bytes equal one of the hashes at an aligned position.
    /// </summary>
    /// <param name="index">The index of the hash that equals the SHA-256</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void ImportRepositoryHoldsLineHashFindsHashAtEveryAlignedPosition(int index)
    {
        // Arrange
        var sha = Sha(7);
        var hashes = Hashes(3, index, sha);

        // Act
        var found = ImportRepository.HoldsLineHash(hashes, sha);

        // Assert
        Assert.IsTrue(found, $"the hash at index {index}");
    }

    /// <summary>
    /// Only the first 16 bytes of the SHA-256 are compared.
    /// </summary>
    [TestMethod]
    public void ImportRepositoryHoldsLineHashComparesFirst16BytesOnly()
    {
        // Arrange
        var sha = Sha(7);
        var hashes = Hashes(2, 1, sha);

        sha[20] ^= 0xFF;

        // Act
        var found = ImportRepository.HoldsLineHash(hashes, sha);

        // Assert
        Assert.IsTrue(found, "bytes after the 16th do not matter");
    }

    /// <summary>
    /// A hash that differs in one byte, or that lies at an unaligned position, is not found.
    /// </summary>
    /// <param name="what">The case</param>
    [TestMethod]
    [DataRow("one byte differs")]
    [DataRow("shifted by one byte")]
    [DataRow("first byte differs")]
    [DataRow("last byte differs")]
    public void ImportRepositoryHoldsLineHashRejectsNearMisses(string what)
    {
        // Arrange
        var sha = Sha(7);
        var hashes = Hashes(3, 99, sha);

        switch (what)
        {
            case "one byte differs":
                {
                    sha.AsSpan(0, 16).CopyTo(hashes.AsSpan(16));
                    hashes[16 + 9] ^= 1;
                }
                break;

            case "shifted by one byte":
                {
                    sha.AsSpan(0, 16).CopyTo(hashes.AsSpan(17));
                }
                break;

            case "first byte differs":
                {
                    sha.AsSpan(0, 16).CopyTo(hashes.AsSpan(0));
                    hashes[0] ^= 1;
                }
                break;

            default:
                {
                    sha.AsSpan(0, 16).CopyTo(hashes.AsSpan(32));
                    hashes[32 + 15] ^= 1;
                }
                break;
        }

        // Act
        var found = ImportRepository.HoldsLineHash(hashes, sha);

        // Assert
        Assert.IsFalse(found, what);
    }

    /// <summary>
    /// A trailing partial entry is ignored, and no hashes hold nothing.
    /// </summary>
    [TestMethod]
    public void ImportRepositoryHoldsLineHashIgnoresTrailingPartialEntryAndEmptyRow()
    {
        // Arrange
        var sha = Sha(7);
        var partial = Hashes(1, 99, sha).Concat(sha.Take(8)).ToArray();

        // Act
        var inPartial = ImportRepository.HoldsLineHash(partial, sha);
        var inEmpty = ImportRepository.HoldsLineHash([], sha);

        // Assert
        Assert.IsFalse(inPartial, "the first 8 bytes of the SHA-256 in a trailing partial entry are not a hash");
        Assert.IsFalse(inEmpty, "an empty row holds nothing");
    }

    /// <summary>
    /// Creates a SHA-256 sized value that differs for every seed.
    /// </summary>
    /// <param name="seed">The seed</param>
    /// <returns>32 bytes</returns>
    private static byte[] Sha(byte seed)
    {
        return [.. Enumerable.Range(0, 32).Select(index => (byte)(seed + index))];
    }

    /// <summary>
    /// Creates a row of hashes of which the one at <paramref name="index"/> is the start of a SHA-256 and the others are filler.
    /// </summary>
    /// <param name="count">The number of hashes</param>
    /// <param name="index">The index of the hash that equals the first bytes of <paramref name="sha"/>; none when it is out of range</param>
    /// <param name="sha">The SHA-256</param>
    /// <returns>The hashes</returns>
    private static byte[] Hashes(int count, int index, byte[] sha)
    {
        var hashes = new byte[count * StorageLimits.LineHashBytes];

        for (var position = 0; position < hashes.Length; position++)
        {
            hashes[position] = (byte)(200 + position);
        }

        if (index < count)
        {
            sha.AsSpan(0, StorageLimits.LineHashBytes).CopyTo(hashes.AsSpan(index * StorageLimits.LineHashBytes));
        }

        return hashes;
    }

    #endregion // Methods
}