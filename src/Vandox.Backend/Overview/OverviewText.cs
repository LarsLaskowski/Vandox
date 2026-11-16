using System.Globalization;

namespace Vandox.Backend.Overview;

/// <summary>
/// Formats the ages, sizes and shares of the overview.
/// </summary>
public static class OverviewText
{
    #region Constants

    private const int SecondsPerMinute = 60;

    private const int SecondsPerHour = 3600;

    private const int SecondsPerDay = 86400;

    private const double BinaryStep = 1024;

    #endregion // Constants

    #region Fields

    /// <summary>
    /// The binary units from kibibytes up.
    /// </summary>
    private static readonly string[] _units = ["KiB", "MiB", "GiB", "TiB"];

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Formats an age as whole seconds below a minute, whole minutes below an hour, hours and minutes below a day, days and hours above.
    /// </summary>
    /// <param name="age">The age</param>
    /// <returns>The text</returns>
    public static string Age(TimeSpan age)
    {
        var seconds = (long)age.TotalSeconds;

        if (seconds < SecondsPerMinute)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{seconds} s");
        }

        if (seconds < SecondsPerHour)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{seconds / SecondsPerMinute} min");
        }

        if (seconds < SecondsPerDay)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{seconds / SecondsPerHour} h {seconds % SecondsPerHour / SecondsPerMinute} min");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{seconds / SecondsPerDay} d {seconds % SecondsPerDay / SecondsPerHour} h");
    }

    /// <summary>
    /// Formats a size in bytes in the largest binary unit it reaches, with one decimal.
    /// </summary>
    /// <param name="bytes">The size in bytes</param>
    /// <returns>The text</returns>
    public static string Size(double bytes)
    {
        if (bytes < BinaryStep)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{bytes:0} B");
        }

        var value = bytes / BinaryStep;
        var unit = 0;

        while (value >= BinaryStep && unit < _units.Length - 1)
        {
            value /= BinaryStep;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{value:0.0} {_units[unit]}");
    }

    /// <summary>
    /// Formats a part of a total as a percentage with one decimal.
    /// </summary>
    /// <param name="part">The part</param>
    /// <param name="total">The total</param>
    /// <returns>The text</returns>
    public static string Share(double part, double total)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{part / total * 100:0.0} %");
    }

    #endregion // Methods
}