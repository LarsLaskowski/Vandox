using System.Text.Json.Serialization;

namespace Vandox.Core.Incidents;

/// <summary>
/// The extreme value of one metric within an incident window.
/// </summary>
public sealed class MetricExtreme
{
    #region Properties

    /// <summary>
    /// Gets or sets the metric name.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the labels.
    /// </summary>
    [JsonPropertyName("labels")]
    public Dictionary<string, string>? Labels { get; set; }

    /// <summary>
    /// Gets or sets whether it is the minimum or maximum.
    /// </summary>
    [JsonPropertyName("extreme")]
    public string Extreme { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the source.
    /// </summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the time.
    /// </summary>
    [JsonPropertyName("at")]
    public DateTimeOffset At { get; set; }

    /// <summary>
    /// Gets or sets the value.
    /// </summary>
    [JsonPropertyName("value")]
    public double Value { get; set; }

    /// <summary>
    /// Gets or sets the number of samples.
    /// </summary>
    [JsonPropertyName("samples")]
    public long Samples { get; set; }

    #endregion // Properties
}