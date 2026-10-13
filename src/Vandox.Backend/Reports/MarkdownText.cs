namespace Vandox.Backend.Reports;

#pragma warning disable RH2003, S2325

/// <summary>
/// Writes external text as a Markdown code span.
/// </summary>
internal static class MarkdownText
{
    #region Methods

    /// <summary>
    /// Wraps text in a code span: escapes backslashes and invisible characters, uses a fence longer than any backtick run and pads the text.
    /// </summary>
    /// <param name="text">The external text</param>
    /// <returns>The code span; a dash for empty text</returns>
    internal static string Code(string text)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}