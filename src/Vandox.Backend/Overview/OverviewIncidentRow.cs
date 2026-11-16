using Vandox.Backend.Logs;

namespace Vandox.Backend.Overview;

/// <summary>
/// The row of an incident.
/// </summary>
public sealed class OverviewIncidentRow
{
    #region Properties

    /// <summary>
    /// Gets or sets the number of the incident.
    /// </summary>
    public long Number { get; set; }

    /// <summary>
    /// Gets or sets the link to the incident.
    /// </summary>
    public string Href { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the time of the first kill.
    /// </summary>
    public string FirstKill { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the victim as text runs.
    /// </summary>
    public IReadOnlyList<TextRun> Victim { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether the incident is dated in the future.
    /// </summary>
    public bool Future { get; set; }

    #endregion // Properties
}