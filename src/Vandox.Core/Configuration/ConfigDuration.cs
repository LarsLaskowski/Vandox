namespace Vandox.Core.Configuration;

#pragma warning disable RH2003, S2325

/// <summary>
/// Parses the durations of the configuration: one to four ASCII digits followed by <c>m</c> (minutes) or <c>h</c> (hours).
/// </summary>
public static class ConfigDuration
{
    #region Constants

    /// <summary>
    /// The largest number of digits.
    /// </summary>
    public const int MaxDigits = 4;

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Parses a duration.
    /// </summary>
    /// <param name="text">The text</param>
    /// <param name="value">Receives the duration</param>
    /// <returns><c>true</c> when the text has the form</returns>
    public static bool TryParse(string text, out TimeSpan value)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}