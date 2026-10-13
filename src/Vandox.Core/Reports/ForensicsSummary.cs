namespace Vandox.Core.Reports;

/// <summary>
/// The whole-period result of the forensics analysis.
/// </summary>
public sealed class ForensicsSummary
{
    #region Properties

    /// <summary>
    /// Gets the generation time.
    /// </summary>
    public DateTimeOffset GeneratedAt { get; init; }

    /// <summary>
    /// Gets the lookback before an outage.
    /// </summary>
    public TimeSpan Lookback { get; init; }

    /// <summary>
    /// Gets the number of log lines read.
    /// </summary>
    public long LogLines { get; init; }

    /// <summary>
    /// Gets the capture time of the first log line.
    /// </summary>
    public DateTimeOffset? FirstLogLineAt { get; init; }

    /// <summary>
    /// Gets the capture time of the last log line.
    /// </summary>
    public DateTimeOffset? LastLogLineAt { get; init; }

    /// <summary>
    /// Gets the number of observed hours.
    /// </summary>
    public long ObservedHours { get; init; }

    /// <summary>
    /// Gets the number of process snapshots read.
    /// </summary>
    public long Snapshots { get; init; }

    /// <summary>
    /// Gets the capture time of the first snapshot.
    /// </summary>
    public DateTimeOffset? FirstSnapshotAt { get; init; }

    /// <summary>
    /// Gets the capture time of the last snapshot.
    /// </summary>
    public DateTimeOffset? LastSnapshotAt { get; init; }

    /// <summary>
    /// Gets the number of outages.
    /// </summary>
    public long Incidents { get; init; }

    /// <summary>
    /// Gets the number of kills.
    /// </summary>
    public long Kills { get; init; }

    /// <summary>
    /// Gets the time of the first outage.
    /// </summary>
    public DateTimeOffset? FirstOutageAt { get; init; }

    /// <summary>
    /// Gets the time of the last outage.
    /// </summary>
    public DateTimeOffset? LastOutageAt { get; init; }

    /// <summary>
    /// Gets the time-of-day rule per UTC hour; 24 entries.
    /// </summary>
    public IReadOnlyList<PatternResult> TimeOfDay { get; init; } = [];

    /// <summary>
    /// Gets the backup correlation.
    /// </summary>
    public PatternResult Backup { get; init; } = new();

    /// <summary>
    /// Gets the boot correlation.
    /// </summary>
    public PatternResult Boot { get; init; } = new();

    /// <summary>
    /// Gets the panel memory correlation.
    /// </summary>
    public PatternResult PanelHigh { get; init; } = new();

    /// <summary>
    /// Gets the panel memory that counts as high, in bytes.
    /// </summary>
    public ulong PanelThresholdBytes { get; init; }

    /// <summary>
    /// Gets the largest permanent consumers.
    /// </summary>
    public IReadOnlyList<ConsumerSummary> Permanent { get; init; } = [];

    /// <summary>
    /// Gets the largest transient consumers.
    /// </summary>
    public IReadOnlyList<ConsumerSummary> Transient { get; init; } = [];

    /// <summary>
    /// Gets the program entries not tracked because of the cap.
    /// </summary>
    public long UntrackedProgramEntries { get; init; }

    /// <summary>
    /// Gets a value indicating whether snapshots beyond the cap were dropped.
    /// </summary>
    public bool SnapshotsTruncated { get; init; }

    /// <summary>
    /// Gets a value indicating whether boots beyond the cap were dropped.
    /// </summary>
    public bool BootsTruncated { get; init; }

    /// <summary>
    /// Gets a value indicating whether observed hours beyond the cap were dropped.
    /// </summary>
    public bool ObservedHoursTruncated { get; init; }

    #endregion // Properties
}