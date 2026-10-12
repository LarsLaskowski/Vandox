namespace Vandox.Core.Signatures;

/// <summary>
/// Converts captured text into numbers. The whole text is scanned character by character and only ASCII digits (and one
/// leading minus sign for integers) are accepted; no parsing API sees text that has not passed the scan.
/// </summary>
internal static class NumberGuard
{
    #region Constants

    private const int MaxDigits = 19;
    private const ulong MinValueMagnitude = 9223372036854775808UL;
    private const long MaxKibibytes = long.MaxValue / 1024;

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Converts text into an integer: an optional minus sign and 1 to 19 ASCII digits that fit 64 bits.
    /// </summary>
    /// <param name="text">The captured text</param>
    /// <param name="value">Receives the number</param>
    /// <returns><c>true</c> when the text is a number</returns>
    internal static bool TryInteger(string text, out long value)
    {
        value = 0;

        var negative = text.Length > 0 && text[0] == '-';
        var limit = negative ? MinValueMagnitude : long.MaxValue;

        if (TryDigits(text, negative ? 1 : 0, out var magnitude) && magnitude <= limit)
        {
            value = negative ? unchecked(-(long)magnitude) : (long)magnitude;

            return true;
        }

        return false;
    }

    /// <summary>
    /// Converts text of kibibytes into bytes: 1 to 19 ASCII digits, no sign, and a result that fits 64 bits.
    /// </summary>
    /// <param name="text">The captured text</param>
    /// <param name="value">Receives the number of bytes</param>
    /// <returns><c>true</c> when the text is a number</returns>
    internal static bool TryKibibytes(string text, out long value)
    {
        value = 0;

        if (TryDigits(text, 0, out var magnitude) && magnitude <= MaxKibibytes)
        {
            value = (long)magnitude * 1024;

            return true;
        }

        return false;
    }

    /// <summary>
    /// Reads the digits of a text from a start position to its end.
    /// </summary>
    /// <param name="text">The text</param>
    /// <param name="start">The index of the first digit</param>
    /// <param name="magnitude">Receives the number</param>
    /// <returns><c>true</c> when 1 to 19 characters follow and each is an ASCII digit</returns>
    private static bool TryDigits(string text, int start, out ulong magnitude)
    {
        magnitude = 0;

        var count = text.Length - start;

        if (count is < 1 or > MaxDigits)
        {
            return false;
        }

        for (var index = start; index < text.Length; index++)
        {
            var character = text[index];

            if (character is < '0' or > '9')
            {
                return false;
            }

            magnitude = (magnitude * 10) + (ulong)(character - '0');
        }

        return true;
    }

    #endregion // Methods
}