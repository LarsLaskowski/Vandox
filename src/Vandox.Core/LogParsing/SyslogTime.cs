#pragma warning disable RH2003, S2325 // Skeleton: bodies are replaced by the implementation tasks

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
    #region Properties

    /// <summary>
    /// Gets a value indicating whether the time carries a year and an offset (RFC 3339).
    /// </summary>
    internal bool HasYear => OffsetMinutes is not null;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Builds the instant of an RFC 3339 time; never throws.
    /// </summary>
    /// <param name="instant">The UTC instant on success</param>
    /// <returns><c>null</c> on success, else "invalid date" or "time outside the storable range"</returns>
    internal string? TryGetInstant(out DateTimeOffset instant)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}