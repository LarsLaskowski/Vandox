#pragma warning disable RH2003, S2325 // Skeleton: bodies are replaced by the implementation tasks

namespace Vandox.Core.LogParsing;

/// <summary>
/// Decodes and cuts text to limits counted in UTF-8 bytes.
/// </summary>
internal static class Utf8Text
{
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
        throw new NotImplementedException();
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
        throw new NotImplementedException();
    }

    #endregion // Methods
}