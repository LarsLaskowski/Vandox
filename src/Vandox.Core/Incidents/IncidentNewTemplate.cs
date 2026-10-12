using System.Text.Json.Serialization;

namespace Vandox.Core.Incidents;

/// <summary>
/// A log template first seen within an incident window.
/// </summary>
public sealed class IncidentNewTemplate
{
    #region Properties

    /// <summary>
    /// Gets or sets the source.
    /// </summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the program.
    /// </summary>
    [JsonPropertyName("program")]
    public string Program { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the template.
    /// </summary>
    [JsonPropertyName("template")]
    public string Template { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the time first seen.
    /// </summary>
    [JsonPropertyName("first_seen")]
    public DateTimeOffset FirstSeen { get; set; }

    /// <summary>
    /// Gets or sets the number of lines.
    /// </summary>
    [JsonPropertyName("count")]
    public long Count { get; set; }

    #endregion // Properties
}