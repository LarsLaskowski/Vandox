using System.Text.Json.Serialization;

namespace Vandox.Core.Incidents;

/// <summary>
/// One reconstructed incident: the OOM kills of overlapping windows with their timeline and cause hypothesis.
/// </summary>
public sealed class Incident
{
    #region Properties

    /// <summary>
    /// Gets or sets the start of the window.
    /// </summary>
    [JsonPropertyName("start")]
    public DateTimeOffset Start { get; set; }

    /// <summary>
    /// Gets or sets the end of the window.
    /// </summary>
    [JsonPropertyName("end")]
    public DateTimeOffset End { get; set; }

    /// <summary>
    /// Gets or sets the window before a kill in seconds.
    /// </summary>
    [JsonPropertyName("window_before_seconds")]
    public long WindowBeforeSeconds { get; set; }

    /// <summary>
    /// Gets or sets the window after a kill in seconds.
    /// </summary>
    [JsonPropertyName("window_after_seconds")]
    public long WindowAfterSeconds { get; set; }

    /// <summary>
    /// Gets or sets the time of the first kill.
    /// </summary>
    [JsonPropertyName("first_kill_at")]
    public DateTimeOffset FirstKillAt { get; set; }

    /// <summary>
    /// Gets or sets the time of the last kill.
    /// </summary>
    [JsonPropertyName("last_kill_at")]
    public DateTimeOffset LastKillAt { get; set; }

    /// <summary>
    /// Gets or sets the number of kills.
    /// </summary>
    [JsonPropertyName("kill_count")]
    public long KillCount { get; set; }

    /// <summary>
    /// Gets or sets the first kills.
    /// </summary>
    [JsonPropertyName("kills")]
    public List<IncidentKill> Kills { get; set; } = [];

    /// <summary>
    /// Gets or sets the timeline.
    /// </summary>
    [JsonPropertyName("timeline")]
    public List<TimelineEntry> Timeline { get; set; } = [];

    /// <summary>
    /// Gets or sets the number of events left out of the timeline.
    /// </summary>
    [JsonPropertyName("timeline_omitted")]
    public long TimelineOmitted { get; set; }

    /// <summary>
    /// Gets or sets the cause hypothesis.
    /// </summary>
    [JsonPropertyName("hypothesis")]
    public IncidentHypothesis Hypothesis { get; set; } = new();

    /// <summary>
    /// Gets or sets the metric extremes.
    /// </summary>
    [JsonPropertyName("metrics")]
    public List<MetricExtreme> Metrics { get; set; } = [];

    /// <summary>
    /// Gets or sets the connection peak; <c>null</c> without a connection snapshot.
    /// </summary>
    [JsonPropertyName("connections")]
    public ConnectionPeak? Connections { get; set; }

    /// <summary>
    /// Gets or sets the new log templates.
    /// </summary>
    [JsonPropertyName("new_templates")]
    public List<IncidentNewTemplate> NewTemplates { get; set; } = [];

    /// <summary>
    /// Gets or sets the template spikes.
    /// </summary>
    [JsonPropertyName("template_spikes")]
    public List<IncidentTemplateSpike> TemplateSpikes { get; set; } = [];

    #endregion // Properties
}