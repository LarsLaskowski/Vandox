using System.Text.Json;

namespace Vandox.Core.Incidents;

/// <summary>
/// The JSON options incidents are serialized with, in the builder's budget and in the store alike.
/// </summary>
public static class IncidentJson
{
    #region Properties

    /// <summary>
    /// Gets the options: the default encoder, no indentation.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = new()
                                                           {
                                                               WriteIndented = false
                                                           };

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Returns the number of UTF-8 bytes of a serialized timeline entry.
    /// </summary>
    /// <param name="entry">The entry</param>
    /// <returns>The length</returns>
    public static int Utf8Length(TimelineEntry entry)
    {
        return JsonSerializer.SerializeToUtf8Bytes(entry, Options).Length;
    }

    #endregion // Methods
}