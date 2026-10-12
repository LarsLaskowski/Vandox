namespace Vandox.Core.Configuration;

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
        value = TimeSpan.Zero;

        if (text is null || text.Length < 2 || text.Length > MaxDigits + 1)
        {
            return false;
        }

        var unit = text[^1];

        if (unit is not ('m' or 'h'))
        {
            return false;
        }

        var amount = 0;

        for (var index = 0; index < text.Length - 1; index++)
        {
            var character = text[index];

            if (character is < '0' or > '9')
            {
                return false;
            }

            amount = (amount * 10) + (character - '0');
        }

        value = unit == 'm' ? TimeSpan.FromMinutes(amount) : TimeSpan.FromHours(amount);

        return true;
    }

    #endregion // Methods
}