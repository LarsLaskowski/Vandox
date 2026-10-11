using System.Security.Cryptography;

namespace Vandox.Import;

/// <summary>
/// The head key of a content: the SHA-256 of its first line, used to find the contents a longer one may continue.
/// </summary>
internal static class ContentHead
{
    #region Methods

    /// <summary>
    /// Returns the head key: the SHA-256 of the head up to and including its first line feed, of the whole head when it holds none.
    /// </summary>
    /// <param name="head">The first up to <c>ILogParser.SniffBytes</c> bytes of the decompressed content</param>
    /// <returns>The key, 32 bytes</returns>
    internal static byte[] Key(ReadOnlySpan<byte> head)
    {
        var length = Math.Min(head.Length, 4096);
        var line = head[..length].IndexOf((byte)'\n');

        return SHA256.HashData(line >= 0 ? head[..(line + 1)] : head[..length]);
    }

    #endregion // Methods
}