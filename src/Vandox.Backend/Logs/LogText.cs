using System.Buffers;
using System.Globalization;
using System.Text;

namespace Vandox.Backend.Logs;

/// <summary>
/// Makes external text safe to show: invisible characters become visible escapes.
/// </summary>
public static class LogText
{
    #region Constants

    /// <summary>
    /// The largest number of runs of a text.
    /// </summary>
    public const int MaxRuns = 256;

    /// <summary>
    /// The output cap of a message in UTF-16 characters.
    /// </summary>
    public const int MaxChars = 16384;

    /// <summary>
    /// The output cap of every other field in UTF-16 characters.
    /// </summary>
    public const int MaxShortChars = 2048;

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Splits a text into plain and escaped runs. The runs together hold at most <paramref name="maxChars"/> characters; a text that does not fit ends in an
    /// escaped run that names the characters not shown.
    /// </summary>
    /// <param name="text">The text</param>
    /// <param name="maxChars">The output cap</param>
    /// <returns>The runs, at most <see cref="MaxRuns"/></returns>
    public static IReadOnlyList<TextRun> Runs(string text, int maxChars)
    {
        return Build(text, maxChars, MaxRuns);
    }

    /// <summary>
    /// Escapes a text into one string.
    /// </summary>
    /// <param name="text">The text</param>
    /// <param name="maxChars">The output cap</param>
    /// <returns>The escaped text</returns>
    public static string Escape(string text, int maxChars)
    {
        return string.Concat(Build(text, maxChars, int.MaxValue).Select(run => run.Text));
    }

    /// <summary>
    /// Builds the runs of a text.
    /// </summary>
    /// <param name="text">The text</param>
    /// <param name="maxChars">The output cap</param>
    /// <param name="maxRuns">The most runs; the last one holds the rest</param>
    /// <returns>The runs</returns>
    private static List<TextRun> Build(string text, int maxChars, int maxRuns)
    {
        ArgumentNullException.ThrowIfNull(text);

        var runs = new List<TextRun>();
        var current = new StringBuilder();
        var currentEscaped = false;
        var used = 0;
        var index = 0;

        while (index < text.Length)
        {
            var (piece, length, escaped) = Next(text, index);

            if (used + piece.Length > maxChars)
            {
                break;
            }

            if (current.Length > 0 && currentEscaped != escaped)
            {
                runs.Add(new TextRun(current.ToString(), currentEscaped));
                current.Clear();
            }

            if (current.Length == 0 && runs.Count == maxRuns - 1)
            {
                var rest = Escape(text[index..], maxChars - used);

                runs.Add(new TextRun(rest, rest.Length != text.Length - index));

                return runs;
            }

            current.Append(piece);
            currentEscaped = escaped;
            used += piece.Length;
            index += length;
        }

        if (current.Length > 0)
        {
            runs.Add(new TextRun(current.ToString(), currentEscaped));
        }

        if (index < text.Length)
        {
            runs.Add(new TextRun($"[cut: {text.Length - index} more characters]", true));
        }

        return runs;
    }

    /// <summary>
    /// Reads the character at an index: its output text, the number of UTF-16 characters it takes and whether it is escaped.
    /// </summary>
    /// <param name="text">The text</param>
    /// <param name="index">The index</param>
    /// <returns>The output, the number of characters consumed and whether the output is an escape</returns>
    private static (string Piece, int Length, bool Escaped) Next(string text, int index)
    {
        if (Rune.DecodeFromUtf16(text.AsSpan(index), out var rune, out var consumed) != OperationStatus.Done)
        {
            return (Escape(text[index]), 1, true);
        }

        return NeedsEscape(rune) ? (Escape(rune), consumed, true) : (text.Substring(index, consumed), consumed, false);
    }

    /// <summary>
    /// Tells whether a character is shown as an escape: control characters other than line feed and tab, format characters, line and paragraph separators, private
    /// use and unassigned characters, and the replacement character.
    /// </summary>
    /// <param name="rune">The character</param>
    /// <returns><c>true</c> when the character becomes an escape</returns>
    private static bool NeedsEscape(Rune rune)
    {
        if (rune.Value is '\n' or '\t')
        {
            return false;
        }

        return rune.Value == 0xFFFD
               || Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control
                                                or UnicodeCategory.Format
                                                or UnicodeCategory.LineSeparator
                                                or UnicodeCategory.ParagraphSeparator
                                                or UnicodeCategory.PrivateUse
                                                or UnicodeCategory.OtherNotAssigned;
    }

    /// <summary>
    /// Writes the escape of a character: <c>\uXXXX</c>, or <c>\UXXXXXXXX</c> above U+FFFF, in lower-case hex.
    /// </summary>
    /// <param name="rune">The character</param>
    /// <returns>The escape</returns>
    private static string Escape(Rune rune)
    {
        return rune.Value <= 0xFFFF ? $"\\u{rune.Value:x4}" : $"\\U{rune.Value:x8}";
    }

    /// <summary>
    /// Writes the escape of a UTF-16 code unit that is not part of a valid pair.
    /// </summary>
    /// <param name="unit">The code unit</param>
    /// <returns>The escape</returns>
    private static string Escape(char unit)
    {
        return $"\\u{(int)unit:x4}";
    }

    #endregion // Methods
}