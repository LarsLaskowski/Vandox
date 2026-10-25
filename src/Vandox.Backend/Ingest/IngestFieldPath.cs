using System.Text;

namespace Vandox.Backend.Ingest;

/// <summary>
/// Reduces the path of a field error to a form that holds no text of the batch, so it can be logged.
/// </summary>
internal static class IngestFieldPath
{
    #region Constants

    /// <summary>
    /// The marker a cut key carries between its closing quote and the closing bracket.
    /// </summary>
    private const string CutMarker = "...";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Turns every quoted map key of a field path into <c>[]</c>. The reader walks each key as the path builder wrote it: the key is quoted,
    /// <c>"</c> is escaped as <c>\"</c> and <c>\</c> as <c>\\</c>, a cut key has <c>...</c> between its closing quote and <c>]</c>, and a
    /// key may hold <c>[</c>, <c>]</c> and <c>"]</c>. It fails closed: a path it cannot parse completely gives no value.
    /// </summary>
    /// <param name="field">The field path of a <c>FieldError</c></param>
    /// <returns>The reduced path; <c>null</c> when the path does not parse, so the caller logs no field</returns>
    internal static string? Reduce(string field)
    {
        var reduced = new StringBuilder(field.Length);
        var position = 0;

        while (position < field.Length)
        {
            var next = field[position] switch
                       {
                           '[' => ReadBracket(field, position, reduced),
                           ']' => -1,
                           var other => AppendPlain(other, position, reduced)
                       };

            if (next < 0)
            {
                return null;
            }

            position = next;
        }

        return reduced.ToString();
    }

    /// <summary>
    /// Appends a character that is no part of a bracket.
    /// </summary>
    /// <param name="character">The character</param>
    /// <param name="position">The position of the character</param>
    /// <param name="reduced">The reduced path so far</param>
    /// <returns>The position after the character</returns>
    private static int AppendPlain(char character, int position, StringBuilder reduced)
    {
        reduced.Append(character);

        return position + 1;
    }

    /// <summary>
    /// Reads the bracket that starts at <paramref name="start"/>: a list index, which stays, or a quoted key, which is reduced.
    /// </summary>
    /// <param name="field">The field path</param>
    /// <param name="start">The position of the opening bracket</param>
    /// <param name="reduced">The reduced path so far</param>
    /// <returns>The position after the closing bracket; -1 when the bracket does not parse</returns>
    private static int ReadBracket(string field, int start, StringBuilder reduced)
    {
        var position = start + 1;

        if (position < field.Length && field[position] == '"')
        {
            var end = SkipKey(field, position + 1);

            if (end > 0)
            {
                reduced.Append("[]");
            }

            return end;
        }

        var digits = position;

        while (digits < field.Length && char.IsAsciiDigit(field[digits]))
        {
            digits++;
        }

        if (digits > position && digits < field.Length && field[digits] == ']')
        {
            reduced.Append(field, start, digits + 1 - start);

            return digits + 1;
        }

        return -1;
    }

    /// <summary>
    /// Skips a quoted key, honouring the escapes, and its optional cut marker up to and including the closing bracket.
    /// </summary>
    /// <param name="field">The field path</param>
    /// <param name="start">The position after the opening quote</param>
    /// <returns>The position after the closing bracket; -1 when the key does not end as the path builder writes it</returns>
    private static int SkipKey(string field, int start)
    {
        var position = start;

        while (position < field.Length)
        {
            if (field[position] == '\\')
            {
                position += 2;
            }
            else if (field[position] == '"')
            {
                return SkipEnd(field, position + 1);
            }
            else
            {
                position++;
            }
        }

        return -1;
    }

    /// <summary>
    /// Skips the optional cut marker and the closing bracket after the closing quote of a key.
    /// </summary>
    /// <param name="field">The field path</param>
    /// <param name="start">The position after the closing quote</param>
    /// <returns>The position after the closing bracket; -1 when something else follows</returns>
    private static int SkipEnd(string field, int start)
    {
        var position = start;

        if (string.CompareOrdinal(field, position, CutMarker, 0, CutMarker.Length) == 0)
        {
            position += CutMarker.Length;
        }

        return position < field.Length && field[position] == ']' ? position + 1 : -1;
    }

    #endregion // Methods
}