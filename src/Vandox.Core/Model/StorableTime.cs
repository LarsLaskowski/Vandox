namespace Vandox.Core.Model;

/// <summary>
/// The range of instants the storage can hold.
/// </summary>
public static class StorableTime
{
    #region Fields

    /// <summary>
    /// The earliest storable instant, 1677-09-21T00:12:43.1452242Z.
    /// </summary>
    public static readonly DateTimeOffset Min = new(529122247631452242L, TimeSpan.Zero);

    /// <summary>
    /// The latest storable instant, 2262-04-11T23:47:16.8547758Z.
    /// </summary>
    public static readonly DateTimeOffset Max = new(713589688368547758L, TimeSpan.Zero);

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Tells whether an instant lies inside the storable range.
    /// </summary>
    /// <param name="instant">The instant</param>
    /// <returns><c>true</c> when <see cref="Min"/> &lt;= <paramref name="instant"/> &lt;= <see cref="Max"/></returns>
    public static bool Contains(DateTimeOffset instant)
    {
        return instant.UtcTicks >= Min.UtcTicks && instant.UtcTicks <= Max.UtcTicks;
    }

    #endregion // Methods
}