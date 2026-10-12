using System.Text.Json.Serialization;

using Vandox.Core.Signatures;

namespace Vandox.Core.Incidents;

#pragma warning disable RH2003, S2325

/// <summary>
/// One entry of the timeline of an incident.
/// </summary>
public sealed class TimelineEntry
{
    #region Properties

    /// <summary>
    /// Gets or sets the time.
    /// </summary>
    [JsonPropertyName("at")]
    public DateTimeOffset At { get; set; }

    /// <summary>
    /// Gets or sets the type: a signature ID or a backup marker.
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the host.
    /// </summary>
    [JsonPropertyName("host")]
    public string Host { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the text fields.
    /// </summary>
    [JsonPropertyName("texts")]
    public Dictionary<string, string> Texts { get; set; } = [];

    /// <summary>
    /// Gets or sets the number fields.
    /// </summary>
    [JsonPropertyName("numbers")]
    public Dictionary<string, long> Numbers { get; set; } = [];

    /// <summary>
    /// Gets or sets the record IDs of the lines.
    /// </summary>
    [JsonPropertyName("record_ids")]
    public List<long> RecordIds { get; set; } = [];

    /// <summary>
    /// Gets or sets the number of lines.
    /// </summary>
    [JsonPropertyName("line_count")]
    public long LineCount { get; set; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Maps a detected event.
    /// </summary>
    /// <param name="detected">The detected event</param>
    /// <returns>The entry</returns>
    public static TimelineEntry From(DetectedEvent detected)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}