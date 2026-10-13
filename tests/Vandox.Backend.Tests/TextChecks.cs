namespace Vandox.Backend.Tests;

/// <summary>
/// Searches for numbers and words in the text of a report without regular expressions.
/// </summary>
internal static class TextChecks
{
    #region Methods

    /// <summary>
    /// Tells whether a text holds a number that stands alone: no digit, point or comma directly before it and, without a suffix, no digit directly after it.
    /// </summary>
    /// <param name="text">The text</param>
    /// <param name="number">The number as digits</param>
    /// <param name="suffix">The text that must follow the number directly, if any</param>
    /// <returns><c>true</c> when the text holds the number</returns>
    internal static bool ContainsNumber(string text, string number, string suffix = "")
    {
        var index = text.IndexOf(number + suffix, StringComparison.Ordinal);

        while (index >= 0)
        {
            var end = index + number.Length;
            var before = index > 0 && (char.IsAsciiDigit(text[index - 1]) || text[index - 1] is '.' or ',');
            var after = suffix.Length == 0 && end < text.Length && char.IsAsciiDigit(text[end]);

            if (before || after)
            {
                index = text.IndexOf(number + suffix, index + 1, StringComparison.Ordinal);
            }
            else
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Tells whether a text holds a word that is not part of a longer word.
    /// </summary>
    /// <param name="text">The text</param>
    /// <param name="word">The word</param>
    /// <returns><c>true</c> when the text holds the word</returns>
    internal static bool ContainsWord(string text, string word)
    {
        var index = text.IndexOf(word, StringComparison.Ordinal);

        while (index >= 0)
        {
            var end = index + word.Length;
            var before = index > 0 && char.IsLetter(text[index - 1]);
            var after = end < text.Length && char.IsLetter(text[end]);

            if (before || after)
            {
                index = text.IndexOf(word, index + 1, StringComparison.Ordinal);
            }
            else
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Tells whether a number follows a span within eight characters that are no digits.
    /// </summary>
    /// <param name="text">The text</param>
    /// <param name="span">The span after which the number stands</param>
    /// <param name="number">The number as digits</param>
    /// <returns><c>true</c> when the number follows the span</returns>
    internal static bool NumberFollows(string text, string span, string number)
    {
        var index = text.IndexOf(span, StringComparison.Ordinal);

        if (index < 0)
        {
            return false;
        }

        var start = index + span.Length;
        var window = text.AsSpan(start, Math.Min(text.Length - start, 8 + number.Length + 1));
        var first = window.IndexOfAnyInRange('0', '9');

        return first >= 0 && first <= 8 && window[first..].StartsWith(number) && ContainsNumber(window[first..].ToString(), number);
    }

    #endregion // Methods
}