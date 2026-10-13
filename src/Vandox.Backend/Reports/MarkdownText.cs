using System.Text;

using Vandox.Backend.Cli;

namespace Vandox.Backend.Reports;

/// <summary>
/// Writes external text as a Markdown code span.
/// </summary>
internal static class MarkdownText
{
    #region Constants

    private const string EmptyText = "—";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Wraps text in a code span: escapes backslashes and invisible characters, uses a fence longer than any backtick run and pads the text.
    /// </summary>
    /// <param name="text">The external text</param>
    /// <returns>The code span; a dash for empty text</returns>
    internal static string Code(string text)
    {
        if (text.Length == 0)
        {
            return EmptyText;
        }

        var spacesOnly = text.All(character => character == ' ');
        var escaped = new StringBuilder(text.Length + 4);

        foreach (var rune in text.EnumerateRunes())
        {
            escaped.Append(Escape(rune, spacesOnly));
        }

        var content = escaped.ToString();
        var fence = new string('`', LongestRun(content, '`') + 1);

        return $"{fence} {content} {fence}";
    }

    /// <summary>
    /// Writes one character of a code span: as it is, or as an escape that Markdown leaves literal inside a span.
    /// </summary>
    /// <param name="rune">The character</param>
    /// <param name="spacesOnly">Whether the whole text is spaces, which are then written as escapes</param>
    /// <returns>The text of the character</returns>
    private static string Escape(Rune rune, bool spacesOnly)
    {
        switch (rune.Value)
        {
            case '\\':
                {
                    return "\\\\";
                }
            case '\n':
                {
                    return "\\n";
                }
            case '\r':
                {
                    return "\\r";
                }
            case '\t':
                {
                    return "\\t";
                }
            case ' ' when spacesOnly:
                {
                    return Terminal.HexEscape(rune);
                }
        }

        return Terminal.NeedsEscape(rune) ? Terminal.HexEscape(rune) : rune.ToString();
    }

    /// <summary>
    /// Finds the longest run of one character.
    /// </summary>
    /// <param name="text">The text</param>
    /// <param name="character">The character</param>
    /// <returns>The length of the longest run</returns>
    private static int LongestRun(string text, char character)
    {
        var longest = 0;
        var current = 0;

        foreach (var next in text)
        {
            current = next == character ? current + 1 : 0;
            longest = Math.Max(longest, current);
        }

        return longest;
    }

    #endregion // Methods
}