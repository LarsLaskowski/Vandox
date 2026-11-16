namespace Vandox.Backend.Overview;

/// <summary>
/// The texts and rows of the overview page.
/// </summary>
public sealed class OverviewModel
{
    #region Properties

    /// <summary>
    /// Gets or sets the text of the last live data.
    /// </summary>
    public string LiveData { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the text of the last data received.
    /// </summary>
    public string Received { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the text of the backfill.
    /// </summary>
    public string Backfill { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the text of the agent spool.
    /// </summary>
    public string Spool { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the text that tells the services are stale, empty when they are not.
    /// </summary>
    public string ServicesStale { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the text shown instead of the services, empty when there are rows.
    /// </summary>
    public string ServicesText { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the rows of the units.
    /// </summary>
    public IReadOnlyList<OverviewUnitRow> Units { get; set; } = [];

    /// <summary>
    /// Gets or sets the text of the units not shown, empty when all are shown.
    /// </summary>
    public string MoreUnits { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the rows of the key figures.
    /// </summary>
    public IReadOnlyList<OverviewFigureRow> Figures { get; set; } = [];

    /// <summary>
    /// Gets or sets the text of the mounts not shown, empty when all are shown.
    /// </summary>
    public string MoreMounts { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the open incidents.
    /// </summary>
    public IReadOnlyList<OverviewIncidentRow> OpenIncidents { get; set; } = [];

    /// <summary>
    /// Gets or sets the latest incident, if any.
    /// </summary>
    public OverviewIncidentRow? LatestIncident { get; set; }

    /// <summary>
    /// Gets or sets the text shown instead of the incidents, empty when there is none.
    /// </summary>
    public string IncidentsText { get; set; } = string.Empty;

    #endregion // Properties
}