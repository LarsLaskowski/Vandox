namespace Vandox.Backend.Reports;

#pragma warning disable RH2003, S2325

/// <summary>
/// A piece of the text of a block: plain text of the report or external text as stored.
/// </summary>
public sealed class ReportPiece
{
    #region Properties

    /// <summary>
    /// Gets the plain text, or the external text as stored.
    /// </summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>
    /// Gets a value indicating whether the text is external text.
    /// </summary>
    public bool External { get; init; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Creates a piece of plain text.
    /// </summary>
    /// <param name="text">The text</param>
    /// <returns>The piece</returns>
    public static ReportPiece Plain(string text)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Creates a piece of external text; an empty text gives the plain dash.
    /// </summary>
    /// <param name="text">The external text as stored</param>
    /// <returns>The piece</returns>
    public static ReportPiece Code(string text)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}