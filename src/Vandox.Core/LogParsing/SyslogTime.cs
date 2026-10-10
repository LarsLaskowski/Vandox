using Vandox.Core.Model;

namespace Vandox.Core.LogParsing;

/// <summary>
/// The parsed digits of a syslog time stamp; no date or instant is built from them.
/// </summary>
/// <param name="Year">The year; 0 for a traditional time</param>
/// <param name="Month">The month</param>
/// <param name="Day">The day</param>
/// <param name="Hour">The hour</param>
/// <param name="Minute">The minute</param>
/// <param name="Second">The second</param>
/// <param name="FractionTicks">The fraction in ticks of 100 ns</param>
/// <param name="OffsetMinutes">The offset from UTC in minutes; <c>null</c> for a traditional time</param>
internal readonly record struct SyslogTime(int Year, int Month, int Day, int Hour, int Minute, int Second, int FractionTicks, int? OffsetMinutes)
{
    #region Constants

    /// <summary>
    /// The reason of a date that does not exist.
    /// </summary>
    internal const string InvalidDate = "invalid date";

    /// <summary>
    /// The reason of an instant the storage cannot hold.
    /// </summary>
    internal const string OutsideRange = "time outside the storable range";

    private const int FirstStorableYear = 1677;
    private const int LastStorableYear = 2262;

    #endregion // Constants

    #region Fields

    private static readonly int[] _monthDays = [31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets a value indicating whether the time carries a year and an offset (RFC 3339).
    /// </summary>
    internal bool HasYear => OffsetMinutes is not null;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Returns the number of days of a month in the proleptic Gregorian calendar; works for every year without building a date.
    /// </summary>
    /// <param name="year">The year</param>
    /// <param name="month">The month, 1 to 12</param>
    /// <returns>The number of days</returns>
    internal static int DaysInMonth(int year, int month)
    {
        var leap = year % 4 == 0 && (year % 100 != 0 || year % 400 == 0);

        return month == 2 && leap ? 29 : _monthDays[month - 1];
    }

    /// <summary>
    /// Tells whether the digits of the time can be a date and a time of day.
    /// </summary>
    /// <returns><c>true</c> when month, hour, minute and second are in range</returns>
    internal bool HasValidClock()
    {
        return Month is >= 1 and <= 12 && Hour is >= 0 and <= 23 && Minute is >= 0 and <= 59 && Second is >= 0 and <= 59 && Day >= 1;
    }

    /// <summary>
    /// Builds the instant of an RFC 3339 time; never throws.
    /// </summary>
    /// <param name="instant">The UTC instant on success</param>
    /// <returns><c>null</c> on success, else "invalid date" or "time outside the storable range"</returns>
    internal string? TryGetInstant(out DateTimeOffset instant)
    {
        instant = default;

        if (OffsetMinutes is { } offset && HasValidClock() && FractionTicks is >= 0 and < (int)TimeSpan.TicksPerSecond && Day <= DaysInMonth(Year, Month))
        {
            return Build(offset, out instant);
        }

        return InvalidDate;
    }

    /// <summary>
    /// Builds the instant of a time whose digits are a valid date; the year is checked as a number before any date is built.
    /// </summary>
    /// <param name="offset">The offset in minutes</param>
    /// <param name="instant">The UTC instant on success</param>
    /// <returns><c>null</c> on success, else "time outside the storable range"</returns>
    private string? Build(int offset, out DateTimeOffset instant)
    {
        instant = default;

        if (Year is >= FirstStorableYear and <= LastStorableYear && offset is >= -14 * 60 and <= 14 * 60)
        {
            var utc = new DateTimeOffset(Year, Month, Day, Hour, Minute, Second, TimeSpan.FromMinutes(offset)).AddTicks(FractionTicks).ToUniversalTime();

            if (StorableTime.Contains(utc))
            {
                instant = utc;

                return null;
            }
        }

        return OutsideRange;
    }

    #endregion // Methods
}