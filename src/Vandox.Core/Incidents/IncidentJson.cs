using System.Text.Json;

namespace Vandox.Core.Incidents;

#pragma warning disable RH2003, S2325

/// <summary>
/// The JSON options incidents are serialized with, in the builder's budget and in the store alike.
/// </summary>
public static class IncidentJson
{
    #region Properties

    /// <summary>
    /// Gets the options: the default encoder, no indentation.
    /// </summary>
    public static JsonSerializerOptions Options => throw new NotImplementedException();

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Returns the number of UTF-8 bytes of a serialized timeline entry.
    /// </summary>
    /// <param name="entry">The entry</param>
    /// <returns>The length</returns>
    public static int Utf8Length(TimelineEntry entry)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}