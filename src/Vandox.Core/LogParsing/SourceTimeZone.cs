#pragma warning disable RH2003, S2325 // Skeleton: bodies are replaced by the implementation tasks

using NodaTime;

namespace Vandox.Core.LogParsing;

/// <summary>
/// Finds the time zone of the imported server.
/// </summary>
internal static class SourceTimeZone
{
    #region Methods

    /// <summary>
    /// Finds a zone of the IANA time zone database by its ID, compared ordinally.
    /// </summary>
    /// <param name="name">The zone ID</param>
    /// <returns>The zone, or <c>null</c> when the ID is unknown</returns>
    internal static DateTimeZone? Find(string name)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}