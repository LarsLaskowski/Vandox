using System.Text.Json.Serialization;

namespace Vandox.Core.Incidents;

/// <summary>
/// The connection snapshot with the most sockets within an incident window.
/// </summary>
public sealed class ConnectionPeak
{
    #region Properties

    /// <summary>
    /// Gets or sets the number of connection snapshots.
    /// </summary>
    [JsonPropertyName("snapshots")]
    public long Snapshots { get; set; }

    /// <summary>
    /// Gets or sets the time of the peak.
    /// </summary>
    [JsonPropertyName("at")]
    public DateTimeOffset At { get; set; }

    /// <summary>
    /// Gets or sets the record ID of the peak.
    /// </summary>
    [JsonPropertyName("record_id")]
    public long RecordId { get; set; }

    /// <summary>
    /// Gets or sets the number of sockets.
    /// </summary>
    [JsonPropertyName("sockets")]
    public long Sockets { get; set; }

    /// <summary>
    /// Gets or sets the top processes.
    /// </summary>
    [JsonPropertyName("processes")]
    public List<ConnectionProcess> Processes { get; set; } = [];

    #endregion // Properties
}