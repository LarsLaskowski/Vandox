using System.Text;

namespace Vandox.Core.LogParsing;

/// <summary>
/// Decodes and cuts text to limits counted in UTF-8 bytes.
/// </summary>
internal static class Utf8Text
{
    #region Constants

    private const int MaxBytesPerChar = 3;

    #endregion // Constants

    #region Fields

    private static readonly UTF8Encoding _utf8 = new(false, false);

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Decodes bytes with U+FFFD for invalid ones, then cuts the text to the limit.
    /// </summary>
    /// <param name="bytes">The bytes</param>
    /// <param name="limit">The most UTF-8 bytes the result may take</param>
    /// <param name="truncated">Set to <c>true</c> when the text was cut</param>
    /// <returns>The text</returns>
    internal static string Decode(ReadOnlySpan<byte> bytes, int limit, out bool truncated)
    {
        return Cut(_utf8.GetString(bytes), limit, out truncated);
    }

    /// <summary>
    /// Cuts a text at a character boundary to at most the limit in UTF-8 bytes.
    /// </summary>
    /// <param name="text">The text</param>
    /// <param name="limit">The most UTF-8 bytes the result may take</param>
    /// <param name="truncated">Set to <c>true</c> when the text was cut</param>
    /// <returns>The text</returns>
    internal static string Cut(string text, int limit, out bool truncated)
    {
        truncated = false;

        // A text of at most limit / 3 chars cannot exceed the limit: every char takes at most three bytes (a surrogate pair takes four for two chars).
        if (text.Length <= limit / MaxBytesPerChar || _utf8.GetByteCount(text) <= limit)
        {
            return text;
        }

        truncated = true;

        var bytes = 0;
        var end = 0;

        foreach (var rune in text.EnumerateRunes())
        {
            bytes += rune.Utf8SequenceLength;

            if (bytes > limit)
            {
                break;
            }

            end += rune.Utf16SequenceLength;
        }

        return text[..end];
    }

    #endregion // Methods
}