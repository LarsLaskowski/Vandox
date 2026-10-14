using System.Globalization;
using System.Text;

namespace Vandox.Storage;

/// <summary>
/// Derives search words from a template.
/// </summary>
internal static class TemplateTerms
{
    #region Constants

    /// <summary>
    /// The most words of a search text.
    /// </summary>
    private const int MaxWords = 16;

    /// <summary>
    /// The most UTF-8 bytes of a search text.
    /// </summary>
    private const int MaxBytes = 1024;

    /// <summary>
    /// The size above which a template may have been cut inside its last word.
    /// </summary>
    private const int CutBytes = 1020;

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Returns the literal words of a template as a search text.
    /// </summary>
    /// <param name="template">The template text</param>
    /// <returns>The words joined with single spaces, or an empty text</returns>
    internal static string SearchText(string template)
    {
        var words = template.Split(' ');
        var count = Encoding.UTF8.GetByteCount(template) > CutBytes ? words.Length - 1 : words.Length;
        var kept = new List<string>();
        var bytes = 0;

        foreach (var word in words.Take(count).Where(IsLiteralWord))
        {
            var needed = Encoding.UTF8.GetByteCount(word) + (kept.Count > 0 ? 1 : 0);

            if (kept.Count >= MaxWords || bytes + needed > MaxBytes)
            {
                break;
            }

            kept.Add(word);
            bytes += needed;
        }

        return string.Join(' ', kept);
    }

    /// <summary>
    /// Tells whether a word is a literal word: no placeholder, a letter or a digit, no control or format character.
    /// </summary>
    /// <param name="word">The word</param>
    /// <returns><c>true</c> when the word can be searched for</returns>
    private static bool IsLiteralWord(string word)
    {
        if (word.Contains('<', StringComparison.Ordinal) || word.Contains('>', StringComparison.Ordinal))
        {
            return false;
        }

        var found = false;

        foreach (var rune in word.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);

            if (category is UnicodeCategory.Control or UnicodeCategory.Format)
            {
                return false;
            }

            found |= Rune.IsLetter(rune) || Rune.IsDigit(rune);
        }

        return found;
    }

    #endregion // Methods
}