using System.Globalization;

namespace Vandox.Core.Configuration;

/// <summary>
/// Parses a retention period option.
/// </summary>
public static class RetentionPeriod
{
    #region Constants

    /// <summary>
    /// The text of a period that never expires.
    /// </summary>
    public const string Unlimited = "unlimited";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Parses a retention period: a whole number of days from <c>1d</c> to <c>9999d</c>, or <c>unlimited</c>.
    /// </summary>
    /// <param name="text">The option value</param>
    /// <param name="value">The period, or null for <c>unlimited</c></param>
    /// <returns>True when the text is a valid period</returns>
    public static bool TryParse(string text, out TimeSpan? value)
    {
        value = null;

        if (text == Unlimited)
        {
            return true;
        }

        if (text.Length < 2 || text.Length > 5 || text[^1] != 'd')
        {
            return false;
        }

        var digits = text.AsSpan(0, text.Length - 1);

        if (digits[0] == '0' || digits.ContainsAnyExceptInRange('0', '9'))
        {
            return false;
        }

        value = TimeSpan.FromDays(int.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture));

        return true;
    }

    #endregion // Methods
}