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

        var category = Rune.GetUnicodeCategory(rune);

        if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned || rune == Rune.ReplacementChar)
        {
            builder.Append(rune.Value > 0xFFFF ? $"\\U{rune.Value:x8}" : $"\\u{rune.Value:x4}");
        }
        else
        {
            builder.Append(rune.ToString());
        }
    }

    #endregion // Methods
}