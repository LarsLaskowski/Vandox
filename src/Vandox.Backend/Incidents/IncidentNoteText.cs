#pragma warning disable RH2003, S2325

namespace Vandox.Backend.Incidents;

/// <summary>
/// Normalizes and checks the text of an incident note.
/// </summary>
public static class IncidentNoteText
{
    #region Methods

    /// <summary>
    /// Converts CR LF and CR to LF, removes leading line feeds and turns blank text into an empty one.
    /// </summary>
    /// <param name="text">The text</param>
    /// <returns>The normalized text</returns>
    public static string Normalize(string text)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Tells whether a normalized text is at most 1,800 UTF-16 characters and holds no U+0000.
    /// </summary>
    /// <param name="normalized">The normalized text</param>
    /// <returns><c>true</c> when the text can be stored</returns>
    public static bool IsAcceptable(string normalized)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Parses the number an incident note was written for: one to 18 ASCII digits, greater than zero.
    /// </summary>
    /// <param name="value">The form value</param>
    /// <param name="writtenFor">The number</param>
    /// <returns><c>true</c> when the value is a valid number</returns>
    public static bool TryParseWrittenFor(string value, out long writtenFor)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}