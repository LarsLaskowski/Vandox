using System.Text.Json.Serialization;

namespace Vandox.Storage;

/// <summary>
/// The part of a kill the list reads.
/// </summary>
internal sealed class IncidentSummaryKill
{
    #region Properties

    /// <summary>
    /// Gets or sets the command of the victim.
    /// </summary>
    [JsonPropertyName("victim_command")]
    public string VictimCommand { get; set; } = string.Empty;

    #endregion // Properties
}