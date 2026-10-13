namespace Vandox.Backend.Logs;

#pragma warning disable RH2003, S2325

/// <summary>
/// A run of external text, either plain or escaped.
/// </summary>
public sealed class TextRun
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="TextRun"/> class.
    /// </summary>
    /// <param name="text">The text of the run</param>
    /// <param name="escaped">Whether the run holds escapes</param>
    public TextRun(string text, bool escaped)
    {
        throw new NotImplementedException();
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the text of the run.
    /// </summary>
    public string Text { get; }

    /// <summary>
    /// Gets a value indicating whether the run holds escapes.
    /// </summary>
    public bool Escaped { get; }

    #endregion // Properties
}