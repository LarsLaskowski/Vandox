namespace Vandox.Storage;

/// <summary>
/// Converts instants to and from the nanoseconds since the Unix epoch the database stores.
/// </summary>
internal static class StorageTime
{
    #region Constants

    private const long TicksPerNanosecondInverse = 100;
    private const long UnixEpochTicks = 621355968000000000;
    private const long MaxTickOffset = long.MaxValue / TicksPerNanosecondInverse;
    private const long MinTickOffset = long.MinValue / TicksPerNanosecondInverse;

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Tells whether an instant can be stored as an int64 count of nanoseconds: between 1677-09-21 and 2262-04-11.
    /// </summary>
    /// <param name="instant">The instant</param>
    /// <returns><c>true</c> when the instant can be stored</returns>
    internal static bool InStorableRange(DateTimeOffset instant)
    {
        var offset = instant.UtcTicks - UnixEpochTicks;

        return offset is >= MinTickOffset and <= MaxTickOffset;
    }

    /// <summary>
    /// Tells whether an instant cannot be stored as an int64 count of nanoseconds.
    /// </summary>
    /// <param name="instant">The instant</param>
    /// <returns><c>true</c> when the instant is outside the storable range</returns>
    internal static bool IsOutsideStorableRange(DateTimeOffset instant)
    {
        var offset = instant.UtcTicks - UnixEpochTicks;

        return offset < MinTickOffset || offset > MaxTickOffset;
    }

    /// <summary>
    /// Returns the nanoseconds since the Unix epoch of an instant that is in the storable range.
    /// </summary>
    /// <param name="instant">The instant</param>
    /// <returns>The nanoseconds</returns>
    internal static long ToNanoseconds(DateTimeOffset instant)
    {
        return (instant.UtcTicks - UnixEpochTicks) * TicksPerNanosecondInverse;
    }

    /// <summary>
    /// Returns the instant for a count of nanoseconds since the Unix epoch, in UTC.
    /// </summary>
    /// <param name="nanoseconds">The nanoseconds</param>
    /// <returns>The instant</returns>
    internal static DateTimeOffset FromNanoseconds(long nanoseconds)
    {
        return new DateTimeOffset(UnixEpochTicks + (nanoseconds / TicksPerNanosecondInverse), TimeSpan.Zero);
    }

    #endregion // Methods
}