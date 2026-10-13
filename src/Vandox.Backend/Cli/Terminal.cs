using System.Globalization;
using System.Text;

namespace Vandox.Backend.Cli;

/// <summary>
/// Makes text that comes from input (paths, file names, reasons) safe to print.
/// </summary>
public static class Terminal
{
    #region Methods

    /// <summary>
    /// Quotes a text in double quotes and escapes backslashes, quotes, control characters (including C1 controls and DEL),
    /// format characters, line and paragraph separators and invalid characters, so a path or file name cannot inject escape
    /// sequences or extra lines into the output.
    /// </summary>
    /// <param name="value">The text</param>
    /// <returns>The quoted text</returns>
    public static string Quote(string value)
    {
        var builder = new StringBuilder(value.Length + 2);

        builder.Append('"');

        foreach (var rune in value.EnumerateRunes())
        {
            Append(builder, rune);
        }

        builder.Append('"');

        return builder.ToString();
    }

    /// <summary>
    /// Tells whether a character is written as an escape: a control, format, line or paragraph separator, private use, unassigned
    /// or invalid character.
    /// </summary>
    /// <param name="rune">The character</param>
    /// <returns><c>true</c> when the character is escaped</returns>
    internal static bool NeedsEscape(Rune rune)
    {
        var category = Rune.GetUnicodeCategory(rune);

        return category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned || rune == Rune.ReplacementChar;
    }

    /// <summary>
    /// Writes a character as <c>\u</c> and four lower-case hex digits, or <c>\U</c> and eight above U+FFFF.
    /// </summary>
    /// <param name="rune">The character</param>
    /// <returns>The escape</returns>
    internal static string HexEscape(Rune rune)
    {
        return rune.Value > 0xFFFF ? $"\\U{rune.Value:x8}" : $"\\u{rune.Value:x4}";
    }

    /// <summary>
    /// Appends one character, escaped when needed.
    /// </summary>
    /// <param name="builder">The builder</param>
    /// <param name="rune">The character</param>
    private static void Append(StringBuilder builder, Rune rune)
    {
        switch (rune.Value)
        {
            case '"':
                {
                    builder.Append("\\\"");

                    return;
                }
            case '\\':
                {
                    builder.Append("\\\\");

                    return;
                }
            case '\n':
                {
                    builder.Append("\\n");

                    return;
                }
            case '\r':
                {
                    builder.Append("\\r");

                    return;
                }
            case '\t':
                {
                    builder.Append("\\t");

                    return;
                }
        }

        builder.Append(NeedsEscape(rune) ? HexEscape(rune) : rune.ToString());
    }

    #endregion // Methods
}