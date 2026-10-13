namespace Vandox.Core.Reports;

/// <summary>
/// What ran or happened before one outage.
/// </summary>
public sealed class IncidentActivities
{
    #region Properties

    /// <summary>
    /// Gets whether the backup program ran in the lookback.
    /// </summary>
    public Indicator Backup { get; init; }

    /// <summary>
    /// Gets whether the server booted in the lookback.
    /// </summary>
    public Indicator Boot { get; init; }

    /// <summary>
    /// Gets the last boot in the lookback; <c>null</c> without one.
    /// </summary>
    public DateTimeOffset? LastBootAt { get; init; }

    /// <summary>
    /// Gets whether the panel memory was high in the lookback.
    /// </summary>
    public Indicator PanelHigh { get; init; }

    #endregion // Properties
}