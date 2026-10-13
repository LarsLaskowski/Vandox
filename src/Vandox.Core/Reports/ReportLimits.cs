namespace Vandox.Core.Reports;

/// <summary>
/// The bounds and thresholds of the forensics report.
/// </summary>
public static class ReportLimits
{
    #region Constants

    /// <summary>
    /// The most process snapshot points kept.
    /// </summary>
    public const int MaxSnapshots = 1000000;

    /// <summary>
    /// The most boots kept.
    /// </summary>
    public const int MaxBoots = 10000;

    /// <summary>
    /// The most runs of consecutive observed hours kept.
    /// </summary>
    public const int MaxObservedRuns = 100000;

    /// <summary>
    /// The most programs tracked.
    /// </summary>
    public const int MaxPrograms = 4096;

    /// <summary>
    /// The most consumers listed per kind.
    /// </summary>
    public const int MaxConsumers = 5;

    /// <summary>
    /// The fewest outages with a known answer a rule needs.
    /// </summary>
    public const int MinKnownIncidents = 3;

    /// <summary>
    /// The fewest matching outages a pattern needs.
    /// </summary>
    public const int MinMatchingIncidents = 2;

    /// <summary>
    /// The significance level of a pattern.
    /// </summary>
    public const double SignificanceLevel = 0.01;

    /// <summary>
    /// The share of snapshots a permanent consumer is present in.
    /// </summary>
    public const double PermanentShare = 0.9;

    /// <summary>
    /// The percentile of panel memory that counts as high.
    /// </summary>
    public const double PanelPercentile = 0.9;

    #endregion // Constants
}