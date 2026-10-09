using NodaTime;

namespace Vandox.Core.LogParsing;

/// <summary>
/// Finds the time zone of the imported server.
/// </summary>
internal static class SourceTimeZone
{
    #region Fields

    private static readonly HashSet<string> _ids = new(DateTimeZoneProviders.Tzdb.Ids, StringComparer.Ordinal);

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Finds a zone of the IANA time zone database by its ID, compared ordinally.
    /// </summary>
    /// <param name="name">The zone ID</param>
    /// <returns>The zone, or <c>null</c> when the ID is unknown</returns>
    internal static DateTimeZone? Find(string name)
    {
        return _ids.Contains(name) ? DateTimeZoneProviders.Tzdb.GetZoneOrNull(name) : null;
    }

    #endregion // Methods
}