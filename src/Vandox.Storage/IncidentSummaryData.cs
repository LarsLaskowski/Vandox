using System.Text.Json.Serialization;

using Vandox.Core.Incidents;

namespace Vandox.Storage;

/// <summary>
/// The part of an incident document the list reads; the rest of the document is skipped by the reader.
/// </summary>
internal sealed class IncidentSummaryData
{
    #region Properties

    /// <summary>
    /// Gets or sets the time of the last kill.
    /// </summary>
    [JsonPropertyName("last_kill_at")]
    public DateTimeOffset LastKillAt { get; set; }

    /// <summary>
    /// Gets or sets the kills.
    /// </summary>
    [JsonPropertyName("kills")]
    public List<IncidentSummaryKill> Kills { get; set; } = [];

    /// <summary>
    /// Gets or sets the hypothesis.
    /// </summary>
    [JsonPropertyName("hypothesis")]
    public IncidentHypothesis Hypothesis { get; set; } = new();

    #endregion // Properties
}