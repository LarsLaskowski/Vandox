using Vandox.Storage;

namespace Vandox.Backend.Incidents;

/// <summary>
/// Normalizes and checks the text of an incident note.
/// </summary>
public static class IncidentNoteText
{
    #region Constants

    private const int MaxWrittenForDigits = 18;

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Converts CR LF and CR to LF, removes leading line feeds and turns blank text into an empty one.
    /// </summary>
    /// <param name="text">The text</param>
    /// <returns>The normalized text</returns>
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimStart('\n');
    }

    /// <summary>
    /// Tells whether a normalized text is at most 1,800 UTF-16 characters and holds no U+0000.
    /// </summary>
    /// <param name="normalized">The normalized text</param>
    /// <returns><c>true</c> when the text can be stored</returns>
    public static bool IsAcceptable(string normalized)
    {
        ArgumentNullException.ThrowIfNull(normalized);

        return normalized.Length <= StorageLimits.MaxNoteChars && normalized.IndexOf('\0') < 0;
    }

    /// <summary>
    /// Parses the number an incident note was written for: one to 18 ASCII digits, greater than zero.
    /// </summary>
    /// <param name="value">The form value</param>
    /// <param name="writtenFor">The number</param>
    /// <returns><c>true</c> when the value is a valid number</returns>
    public static bool TryParseWrittenFor(string value, out long writtenFor)
    {
        ArgumentNullException.ThrowIfNull(value);

        writtenFor = 0;

        if (value.Length is 0 or > MaxWrittenForDigits)
        {
            return false;
        }

        var number = 0L;

        foreach (var character in value)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }

            number = (number * 10) + (character - '0');
        }

        writtenFor = number;

        return number > 0;
    }

    #endregion // Methods
}