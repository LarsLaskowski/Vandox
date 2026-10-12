using System.Text.Json.Serialization;

using Vandox.Core.Signatures;

namespace Vandox.Core.Incidents;

/// <summary>
/// One OOM kill of an incident.
/// </summary>
public sealed class IncidentKill
{
    #region Properties

    /// <summary>
    /// Gets or sets the time of the kill.
    /// </summary>
    [JsonPropertyName("at")]
    public DateTimeOffset At { get; set; }

    /// <summary>
    /// Gets or sets the host.
    /// </summary>
    [JsonPropertyName("host")]
    public string Host { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the process ID of the victim.
    /// </summary>
    [JsonPropertyName("victim_pid")]
    public long? VictimPid { get; set; }

    /// <summary>
    /// Gets or sets the command of the victim.
    /// </summary>
    [JsonPropertyName("victim_command")]
    public string VictimCommand { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the anonymous RSS of the victim in bytes.
    /// </summary>
    [JsonPropertyName("anon_rss_bytes")]
    public long? AnonRssBytes { get; set; }

    /// <summary>
    /// Gets or sets the OOM score adjustment of the victim.
    /// </summary>
    [JsonPropertyName("oom_score_adj")]
    public long? OomScoreAdj { get; set; }

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
    /// Maps a detected OOM kill.
    /// </summary>
    /// <param name="detected">The detected event</param>
    /// <returns>The kill</returns>
    public static IncidentKill From(DetectedEvent detected)
    {
        return new IncidentKill
               {
                   At = detected.CapturedAt,
                   Host = detected.Host,
                   VictimPid = Number(detected, "victim_pid"),
                   VictimCommand = detected.Texts.GetValueOrDefault("victim_command", string.Empty),
                   AnonRssBytes = Number(detected, "anon_rss_bytes"),
                   OomScoreAdj = Number(detected, "oom_score_adj"),
                   RecordIds = detected.RecordIds.Take(IncidentLimits.MaxRecordIds).ToList(),
                   LineCount = detected.LineCount
               };
    }

    /// <summary>
    /// Reads an optional number field.
    /// </summary>
    /// <param name="detected">The detected event</param>
    /// <param name="name">The field name</param>
    /// <returns>The number, or <c>null</c> when the field is absent</returns>
    private static long? Number(DetectedEvent detected, string name)
    {
        return detected.Numbers.TryGetValue(name, out var value) ? value : null;
    }

    #endregion // Methods
}