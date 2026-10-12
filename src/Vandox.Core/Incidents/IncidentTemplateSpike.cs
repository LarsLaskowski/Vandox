using System.Text.Json.Serialization;

namespace Vandox.Core.Incidents;

/// <summary>
/// A log template hour above its usual level within an incident window.
/// </summary>
public sealed class IncidentTemplateSpike
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
    /// Gets or sets the hour.
    /// </summary>
    [JsonPropertyName("hour")]
    public DateTimeOffset Hour { get; set; }

    /// <summary>
    /// Gets or sets the number of lines.
    /// </summary>
    [JsonPropertyName("count")]
    public long Count { get; set; }

    /// <summary>
    /// Gets or sets the usual level.
    /// </summary>
    [JsonPropertyName("usual_level")]
    public double UsualLevel { get; set; }

    #endregion // Properties
}