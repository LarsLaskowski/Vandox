namespace Vandox.Backend.Reports;

/// <summary>
/// The counts of a report run.
/// </summary>
public sealed class ReportResult
{
    #region Properties

    /// <summary>
    /// Gets or sets the number of log lines read.
    /// </summary>
    public long LinesRead { get; set; }

    /// <summary>
    /// Gets or sets the number of process snapshots read.
    /// </summary>
    public long SnapshotsRead { get; set; }

    /// <summary>
    /// Gets or sets the number of incidents listed in the second pass.
    /// </summary>
    public long IncidentsListed { get; set; }

    #endregion // Properties
}