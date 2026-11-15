using System.Globalization;

using Vandox.Core.Model;

namespace Vandox.Storage;

/// <summary>
/// Time arithmetic of the time-series query: clamped to the storable range, floor division on ticks, parsing of payload times.
/// </summary>
internal static class SeriesTime
{
    #region Constants

    /// <summary>
    /// The ticks of the Unix epoch.
    /// </summary>
    internal const long EpochTicks = 621355968000000000;

    /// <summary>
    /// The ticks of five minutes.
    /// </summary>
    internal const long FiveMinuteTicks = 5 * 60 * TimeSpan.TicksPerSecond;

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Adds a span to an instant and clamps the result to the storable range.
    /// </summary>
    /// <param name="instant">The instant</param>
    /// <param name="span">The span, negative to go back</param>
    /// <returns>The clamped instant</returns>
    internal static DateTimeOffset Add(DateTimeOffset instant, TimeSpan span)
    {
        var ticks = Math.Clamp(instant.UtcTicks + span.Ticks, StorableTime.Min.UtcTicks, StorableTime.Max.UtcTicks);

        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    /// <summary>
    /// Returns the earlier of two optional instants; <c>null</c> counts as none.
    /// </summary>
    /// <param name="first">The first instant</param>
    /// <param name="second">The second instant</param>
    /// <returns>The earlier instant, or <c>null</c> when both are none</returns>
    internal static DateTimeOffset? Earlier(DateTimeOffset? first, DateTimeOffset? second)
    {
        if (first is null)
        {
            return second;
        }

        if (second is null)
        {
            return first;
        }

        return first < second ? first : second;
    }

    /// <summary>
    /// Divides rounding towards negative infinity.
    /// </summary>
    /// <param name="value">The dividend</param>
    /// <param name="divisor">The positive divisor</param>
    /// <returns>The quotient</returns>
    internal static long FloorDiv(long value, long divisor)
    {
        var quotient = value / divisor;

        return value % divisor < 0 ? quotient - 1 : quotient;
    }

    /// <summary>
    /// Counts the UTC-aligned steps that overlap a range.
    /// </summary>
    /// <param name="fromTicks">The start of the range in ticks since the epoch</param>
    /// <param name="endTicks">The end of the range in ticks since the epoch</param>
    /// <param name="stepTicks">The step in ticks</param>
    /// <returns>The number of steps</returns>
    internal static long Overlapping(long fromTicks, long endTicks, long stepTicks)
    {
        return FloorDiv(endTicks - 1, stepTicks) - FloorDiv(fromTicks, stepTicks) + 1;
    }

    /// <summary>
    /// Parses a time of a stored payload as UTC.
    /// </summary>
    /// <param name="text">The text</param>
    /// <param name="value">The instant when the text is a time</param>
    /// <returns><c>true</c> when the text is a time</returns>
    internal static bool TryParse(string? text, out DateTimeOffset value)
    {
        value = default;

        return string.IsNullOrEmpty(text) == false && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);
    }

    #endregion // Methods
}