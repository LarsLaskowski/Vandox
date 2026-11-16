using Vandox.Storage;

namespace Vandox.Backend.Overview;

/// <summary>
/// The reads one refresh of the overview makes.
/// </summary>
public sealed class OverviewData
{
    #region Properties

    /// <summary>
    /// Gets or sets the freshness of the agent's data.
    /// </summary>
    public AgentFreshness Freshness { get; set; } = new();

    /// <summary>
    /// Gets or sets the newest values of the series.
    /// </summary>
    public IReadOnlyList<LatestSeries> Values { get; set; } = [];

    /// <summary>
    /// Gets or sets the newest MariaDB status, if any.
    /// </summary>
    public MariaDbLatest? MariaDb { get; set; }

    /// <summary>
    /// Gets or sets the newest service states.
    /// </summary>
    public ServiceStatePage Services { get; set; } = new();

    /// <summary>
    /// Gets or sets the newest incident summaries.
    /// </summary>
    public IReadOnlyList<IncidentSummary> Incidents { get; set; } = [];

    #endregion // Properties
}