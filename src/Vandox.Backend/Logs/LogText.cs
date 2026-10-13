namespace Vandox.Backend.Logs;

#pragma warning disable RH2003, S2325

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
    /// Splits a text into plain and escaped runs.
    /// </summary>
    /// <param name="text">The text</param>
    /// <param name="maxChars">The output cap</param>
    /// <returns>The runs</returns>
    public static IReadOnlyList<TextRun> Runs(string text, int maxChars)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Escapes a text into one string.
    /// </summary>
    /// <param name="text">The text</param>
    /// <param name="maxChars">The output cap</param>
    /// <returns>The escaped text</returns>
    public static string Escape(string text, int maxChars)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}