using System.Globalization;
using System.Text;

namespace Vandox.Storage;

/// <summary>
/// Turns a search text into an FTS5 expression of quoted literal terms, so the text never reaches the FTS5 operator
/// syntax.
/// </summary>
internal static class FtsQuery
{
    #region Fields

    private static readonly UTF8Encoding _strictUtf8 = new(false, true);

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Builds the expression.
    /// </summary>
    /// <param name="text">The search text</param>
    /// <returns>The FTS5 expression</returns>
    /// <exception cref="InvalidQueryException">The text breaks a rule; the message never contains the text</exception>
    internal static string Build(string text)
    {
        Require(IsValidUtf16(text), "search text is not valid UTF-8");
        Require(Encoding.UTF8.GetByteCount(text) <= StorageLimits.MaxSearchBytes, $"search text is longer than {StorageLimits.MaxSearchBytes} bytes");
        Require(HasNoControlCharacters(text), "search text contains a control or format character");

        var terms = Split(text);

        Require(terms.Count > 0, "search text has no terms");
        Require(terms.Count <= StorageLimits.MaxSearchTerms, $"search text has more than {StorageLimits.MaxSearchTerms} terms");

        foreach (var term in terms)
        {
            Require(HasLetterOrDigit(term), "every search term needs a letter or a digit");
        }

        return string.Join(' ', terms.Select(term => $"\"{term.Replace("\"", "\"\"", StringComparison.Ordinal)}\""));
    }

    /// <summary>
    /// Throws an error for a rejected query unless the condition holds.
    /// </summary>
    /// <param name="condition">The rule</param>
    /// <param name="rule">The rule that is broken</param>
    private static void Require(bool condition, string rule)
    {
        if (condition)
        {
            return;
        }

        throw new InvalidQueryException($"store: invalid query: {rule}");
    }

    /// <summary>
    /// Tells whether every surrogate of the text is part of a pair.
    /// </summary>
    /// <param name="text">The text</param>
    /// <returns><c>true</c> when the text can be encoded as UTF-8 without loss</returns>
    private static bool IsValidUtf16(string text)
    {
        try
        {
            return _strictUtf8.GetByteCount(text) >= 0;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>
    /// Tells whether the text holds no control or format character other than white space.
    /// </summary>
    /// <param name="text">The text</param>
    /// <returns><c>true</c> when the text holds none</returns>
    private static bool HasNoControlCharacters(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);

            if (Rune.IsWhiteSpace(rune))
            {
                continue;
            }

            if (category is UnicodeCategory.Control or UnicodeCategory.Format)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Tells whether the text holds a letter or a decimal digit.
    /// </summary>
    /// <param name="term">The text</param>
    /// <returns><c>true</c> when it does</returns>
    private static bool HasLetterOrDigit(string term)
    {
        return term.EnumerateRunes().Any(rune => Rune.IsLetter(rune) || Rune.IsDigit(rune));
    }

    /// <summary>
    /// Splits a text at white space.
    /// </summary>
    /// <param name="text">The text</param>
    /// <returns>The terms</returns>
    private static List<string> Split(string text)
    {
        var terms = new List<string>();
        var current = new StringBuilder();

        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                Flush(terms, current);
            }
            else
            {
                current.Append(rune.ToString());
            }
        }

        Flush(terms, current);

        return terms;
    }

    /// <summary>
    /// Moves the text collected so far to the terms.
    /// </summary>
    /// <param name="terms">The terms</param>
    /// <param name="current">The text collected so far</param>
    private static void Flush(List<string> terms, StringBuilder current)
    {
        if (current.Length > 0)
        {
            terms.Add(current.ToString());
            current.Clear();
        }
    }

    #endregion // Methods
}