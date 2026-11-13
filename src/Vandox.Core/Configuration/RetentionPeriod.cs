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
        // Skeleton: removed with the implementation
#pragma warning disable RH2003
        throw new NotImplementedException();
#pragma warning restore RH2003
    }

    #endregion // Methods
}