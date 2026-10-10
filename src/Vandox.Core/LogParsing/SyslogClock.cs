using System.Globalization;

using NodaTime;

using Vandox.Core.Model;

namespace Vandox.Core.LogParsing;

/// <summary>
/// Turns year-less local times of one file into UTC instants. The year follows the date of the file (the name date, else the
/// modification time) and the order of the lines; every number is checked as an integer before a date is built, and the zone
/// is used only through <see cref="DateTimeZone.MapLocal"/>, so nothing here throws for input.
/// </summary>
internal sealed class SyslogClock
{
    #region Constants

    private const string YearUnknown = "year unknown: the file has no usable date";
    private const int FirstStorableYear = 1677;
    private const int LastStorableYear = 2262;
    private const int SecondsPerDay = 86400;
    private const int YearAdvanceDays = 180;
    private const int NameDateDigits = 8;

    #endregion // Constants

    #region Fields

    /// <summary>
    /// How far back a local time in the repeated hour may step and still take the earlier offset.
    /// </summary>
    internal static readonly TimeSpan BackwardTolerance = TimeSpan.FromMinutes(10);

    private static readonly int[] _daysBeforeMonth = [0, 31, 60, 91, 121, 152, 182, 213, 244, 274, 305, 335];

    private readonly DateTimeZone _timeZone;
    private readonly DateTime? _anchor;
    private bool _hasPredecessor;
    private int _predecessorYear;
    private long _predecessorSeconds;
    private DateTimeOffset? _previous;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="SyslogClock"/> class.
    /// </summary>
    /// <param name="timeZone">The zone of the file</param>
    /// <param name="file">The file</param>
    internal SyslogClock(DateTimeZone timeZone, LogFile file)
    {
        _timeZone = timeZone;
        _anchor = AnchorFromName(file.Name) ?? AnchorFromModTime(file.ModTime);
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets a value indicating whether a name date or modification time inside the storable range exists.
    /// </summary>
    internal bool HasAnchor => _anchor is not null;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Resolves a local time that carries its year (<see cref="SyslogTime.Year"/>; the offset is ignored) to a UTC instant; never throws.
    /// A time in the repeated hour takes the earlier offset unless that lies more than <see cref="BackwardTolerance"/> before
    /// <paramref name="previous"/>, and a time in the skipped hour is shifted forward.
    /// </summary>
    /// <param name="timeZone">The zone of the time</param>
    /// <param name="time">The time</param>
    /// <param name="previous">The last instant that was resolved; <c>null</c> for none</param>
    /// <param name="instant">The instant on success, else the default value</param>
    /// <returns><c>null</c> on success, else "invalid date" or "time outside the storable range"</returns>
    internal static string? ResolveLocal(DateTimeZone timeZone, SyslogTime time, DateTimeOffset? previous, out DateTimeOffset instant)
    {
        instant = default;

        if (time.HasValidClock() && time.Day <= SyslogTime.DaysInMonth(time.Year, time.Month))
        {
            return ResolveInRange(timeZone, time, previous, out instant);
        }

        return SyslogTime.InvalidDate;
    }

    /// <summary>
    /// Resolves a year-less time to a UTC instant; never throws.
    /// </summary>
    /// <param name="time">The time</param>
    /// <param name="instant">The instant on success</param>
    /// <returns><c>null</c> on success, else "invalid date", "time outside the storable range" or "year unknown: the file has no usable date"</returns>
    internal string? Resolve(SyslogTime time, out DateTimeOffset instant)
    {
        instant = default;

        if (_anchor is not { } anchor)
        {
            return YearUnknown;
        }

        if (time.HasValidClock())
        {
            return ResolveInYear(time, InferYear(time, anchor), out instant);
        }

        return SyslogTime.InvalidDate;
    }

    /// <summary>
    /// Counts the seconds from the start of a leap year, so dates compare without building them (also 31 November).
    /// </summary>
    /// <param name="month">The month</param>
    /// <param name="day">The day</param>
    /// <param name="hour">The hour</param>
    /// <param name="minute">The minute</param>
    /// <param name="second">The second</param>
    /// <returns>The seconds</returns>
    private static long SecondsOfYear(int month, int day, int hour, int minute, int second)
    {
        return ((_daysBeforeMonth[month - 1] + day - 1) * (long)SecondsPerDay) + (hour * 3600L) + (minute * 60L) + second;
    }

    /// <summary>
    /// Tells whether a base name ends in a dash and eight digits.
    /// </summary>
    /// <param name="baseName">The base name</param>
    /// <returns><c>true</c> when it ends in <c>-YYYYMMDD</c></returns>
    private static bool HasNameDate(string baseName)
    {
        return baseName.Length > NameDateDigits && baseName[^(NameDateDigits + 1)] == '-' && baseName[^NameDateDigits..].All(char.IsAsciiDigit);
    }

    /// <summary>
    /// Subtracts an offset from a local time. The offset may have seconds (local mean time), which a <see cref="DateTimeOffset"/>
    /// with an offset would refuse.
    /// </summary>
    /// <param name="local">The local time</param>
    /// <param name="offset">The offset of the zone</param>
    /// <returns>The instant in UTC</returns>
    private static DateTimeOffset Utc(DateTime local, Offset offset)
    {
        return new DateTimeOffset(local.Ticks - offset.ToTimeSpan().Ticks, TimeSpan.Zero);
    }

    /// <summary>
    /// Resolves a time whose digits are a valid date: the year is checked as a number before any date is built.
    /// </summary>
    /// <param name="timeZone">The zone</param>
    /// <param name="time">The time</param>
    /// <param name="previous">The last instant that was resolved</param>
    /// <param name="instant">The instant on success</param>
    /// <returns><c>null</c> on success, else "time outside the storable range"</returns>
    private static string? ResolveInRange(DateTimeZone timeZone, SyslogTime time, DateTimeOffset? previous, out DateTimeOffset instant)
    {
        instant = default;

        if (time.Year is >= FirstStorableYear and <= LastStorableYear)
        {
            var resolved = ToInstant(timeZone, new DateTime(time.Year, time.Month, time.Day, time.Hour, time.Minute, time.Second, DateTimeKind.Unspecified), previous);

            if (StorableTime.Contains(resolved))
            {
                instant = resolved;

                return null;
            }
        }

        return SyslogTime.OutsideRange;
    }

    /// <summary>
    /// Maps a local time of the zone to an instant: a time in the repeated hour takes the earlier offset unless that puts it
    /// more than <see cref="BackwardTolerance"/> before the previous instant, and a time in the skipped hour is shifted forward
    /// by the gap.
    /// </summary>
    /// <param name="timeZone">The zone</param>
    /// <param name="local">The local time</param>
    /// <param name="previous">The last instant that was resolved; <c>null</c> for none</param>
    /// <returns>The instant in UTC</returns>
    private static DateTimeOffset ToInstant(DateTimeZone timeZone, DateTime local, DateTimeOffset? previous)
    {
        var mapping = timeZone.MapLocal(new LocalDateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, local.Second));

        if (mapping.Count == 0)
        {
            return Utc(local, mapping.EarlyInterval.WallOffset);
        }

        var early = Utc(local, mapping.First().Offset);

        if (mapping.Count == 1 || previous is not { } last || early >= last - BackwardTolerance)
        {
            return early;
        }

        return Utc(local, mapping.Last().Offset);
    }

    /// <summary>
    /// Builds the instant of a time in an inferred year.
    /// </summary>
    /// <param name="time">The time</param>
    /// <param name="year">The year</param>
    /// <param name="instant">The instant on success</param>
    /// <returns><c>null</c> on success, else the reason</returns>
    private string? ResolveInYear(SyslogTime time, int year, out DateTimeOffset instant)
    {
        var reason = ResolveLocal(_timeZone, time with { Year = year }, _previous, out instant);

        if (reason is null)
        {
            _previous = instant;
        }

        return reason;
    }

    /// <summary>
    /// Reads the date of a <c>-YYYYMMDD</c> suffix of the base name and returns the end of that local day.
    /// </summary>
    /// <param name="name">The name of the file</param>
    /// <returns>The local time at the end of the day, or <c>null</c> when the name has no usable date</returns>
    private DateTime? AnchorFromName(string name)
    {
        var baseName = name[(name.LastIndexOf('/') + 1)..];

        return HasNameDate(baseName) ? AnchorFromDate(baseName[^NameDateDigits..]) : null;
    }

    /// <summary>
    /// Returns the end of the local day a date names.
    /// </summary>
    /// <param name="digits">The date as <c>YYYYMMDD</c></param>
    /// <returns>The local time at the end of the day, or <c>null</c> when the date does not exist or its end is outside the storable range</returns>
    private DateTime? AnchorFromDate(string digits)
    {
        var year = int.Parse(digits[..4], CultureInfo.InvariantCulture);
        var month = int.Parse(digits[4..6], CultureInfo.InvariantCulture);
        var day = int.Parse(digits[6..], CultureInfo.InvariantCulture);

        if (year is < FirstStorableYear or > LastStorableYear || month is < 1 or > 12 || day < 1 || day > SyslogTime.DaysInMonth(year, month))
        {
            return null;
        }

        var end = new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Unspecified).AddDays(1);

        return StorableTime.Contains(ToInstant(_timeZone, end, null)) ? end : null;
    }

    /// <summary>
    /// Converts the modification time to the local time of the zone.
    /// </summary>
    /// <param name="modTime">The modification time; may be <c>null</c></param>
    /// <returns>The local time, or <c>null</c> when it is unknown or outside the storable range</returns>
    private DateTime? AnchorFromModTime(DateTimeOffset? modTime)
    {
        if (modTime is { } time && StorableTime.Contains(time))
        {
            var offset = _timeZone.GetUtcOffset(Instant.FromDateTimeOffset(time));

            return new DateTime(time.UtcTicks + offset.ToTimeSpan().Ticks, DateTimeKind.Unspecified);
        }

        return null;
    }

    /// <summary>
    /// Chooses the year of a time: for the first line from the anchor, afterwards from the line before it.
    /// </summary>
    /// <param name="time">The time</param>
    /// <param name="anchor">The local time of the anchor</param>
    /// <returns>The year; not necessarily a storable one</returns>
    private int InferYear(SyslogTime time, DateTime anchor)
    {
        var seconds = SecondsOfYear(time.Month, time.Day, time.Hour, time.Minute, time.Second);
        int year;

        if (_hasPredecessor)
        {
            var advance = _predecessorSeconds - seconds > YearAdvanceDays * (long)SecondsPerDay;

            year = advance && _predecessorYear <= LastStorableYear ? _predecessorYear + 1 : _predecessorYear;
        }
        else
        {
            var limit = anchor.AddDays(1);
            var within = limit.Year > anchor.Year || seconds <= SecondsOfYear(limit.Month, limit.Day, limit.Hour, limit.Minute, limit.Second);

            year = within ? anchor.Year : anchor.Year - 1;
        }

        _hasPredecessor = true;
        _predecessorYear = year;
        _predecessorSeconds = seconds;

        return year;
    }

    #endregion // Methods
}