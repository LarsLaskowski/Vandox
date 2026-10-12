using System.Text.Json.Serialization;

namespace Vandox.Core.Incidents;

/// <summary>
/// The structured cause hypothesis of an incident.
/// </summary>
public sealed class IncidentHypothesis
{
    #region Properties

    /// <summary>
    /// Gets or sets the number of process snapshots of the snapshot period.
    /// </summary>
    [JsonPropertyName("snapshot_count")]
    public long SnapshotCount { get; set; }

    /// <summary>
    /// Gets or sets the time of the first snapshot of the period.
    /// </summary>
    [JsonPropertyName("first_snapshot_at")]
    public DateTimeOffset? FirstSnapshotAt { get; set; }

    /// <summary>
    /// Gets or sets the time of the last snapshot of the period.
    /// </summary>
    [JsonPropertyName("last_snapshot_at")]
    public DateTimeOffset? LastSnapshotAt { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether there are at least two snapshots.
    /// </summary>
    [JsonPropertyName("growth_known")]
    public bool GrowthKnown { get; set; }

    /// <summary>
    /// Gets or sets the top growth candidates.
    /// </summary>
    [JsonPropertyName("growth")]
    public List<ProgramGrowth> Growth { get; set; } = [];

    /// <summary>
    /// Gets or sets the top memory consumers of the last snapshot.
    /// </summary>
    [JsonPropertyName("consumers")]
    public List<ProgramUsage> Consumers { get; set; } = [];

    /// <summary>
    /// Gets or sets whether a backup program ran; <c>null</c> when unknown.
    /// </summary>
    [JsonPropertyName("backup_running")]
    public bool? BackupRunning { get; set; }

    /// <summary>
    /// Gets or sets the backup programs seen.
    /// </summary>
    [JsonPropertyName("backup_programs")]
    public List<string> BackupPrograms { get; set; } = [];

    /// <summary>
    /// Gets or sets the time of the last boot before the first kill.
    /// </summary>
    [JsonPropertyName("recent_boot_at")]
    public DateTimeOffset? RecentBootAt { get; set; }

    /// <summary>
    /// Gets or sets the number of service events before the first kill.
    /// </summary>
    [JsonPropertyName("service_events")]
    public long ServiceEvents { get; set; }

    #endregion // Properties
}