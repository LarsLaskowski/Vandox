namespace Vandox.Import;

/// <summary>
/// Classifies the container format of a file's content from its first bytes.
/// </summary>
internal static class FormatSniffer
{
    #region Constants

    private const int TarMagicOffset = 257;

    #endregion // Constants

    #region Fields

    private static readonly byte[] _gzipMagic = [0x1f, 0x8b, 0x08];

    private static readonly byte[][] _tarMagics = [[(byte)'u', (byte)'s', (byte)'t', (byte)'a', (byte)'r', 0, (byte)'0', (byte)'0'], [(byte)'u', (byte)'s', (byte)'t', (byte)'a', (byte)'r', (byte)' ', (byte)' ', 0]];

    private static readonly (byte[] Magic, string Name)[] _unsupported = [
                                                                             ([(byte)'B', (byte)'Z', (byte)'h'], "bzip2"),
                                                                             ([0xfd, (byte)'7', (byte)'z', (byte)'X', (byte)'Z', 0x00], "xz"),
                                                                             ([0x28, 0xb5, 0x2f, 0xfd], "zstd"),
                                                                             ([0x04, 0x22, 0x4d, 0x18], "lz4"),
                                                                             ([(byte)'P', (byte)'K', 0x03, 0x04], "zip"),
                                                                             ([(byte)'7', (byte)'z', 0xbc, 0xaf, 0x27, 0x1c], "7z")
                                                                         ];

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Classifies the head of a content; for <see cref="ContentFormat.Unsupported"/> it also returns the name of the format.
    /// </summary>
    /// <param name="head">The first bytes of the content</param>
    /// <returns>The format and, when unsupported, its name</returns>
    internal static (ContentFormat Format, string Name) Sniff(ReadOnlySpan<byte> head)
    {
        if (head.Length == 0)
        {
            return (ContentFormat.Empty, string.Empty);
        }

        if (head.StartsWith(_gzipMagic))
        {
            return (ContentFormat.Gzip, string.Empty);
        }

        foreach (var (magic, name) in _unsupported)
        {
            if (head.StartsWith(magic))
            {
                return (ContentFormat.Unsupported, name);
            }
        }

        return (IsTar(head) ? ContentFormat.Tar : ContentFormat.Plain, string.Empty);
    }

    /// <summary>
    /// Tells whether the head starts with a USTAR or GNU tar header.
    /// </summary>
    /// <param name="head">The head</param>
    /// <returns><c>true</c> when it does</returns>
    private static bool IsTar(ReadOnlySpan<byte> head)
    {
        foreach (var magic in _tarMagics)
        {
            if (head.Length >= TarMagicOffset + magic.Length && head.Slice(TarMagicOffset, magic.Length).SequenceEqual(magic))
            {
                return true;
            }
        }

        return false;
    }

    #endregion // Methods
}