namespace Vandox.Core.Reports;

/// <summary>
/// Why a rule could not be decided.
/// </summary>
public enum UnknownReason
{
    /// <summary>
    /// The rule was decided.
    /// </summary>
    None,

    /// <summary>
    /// Fewer outages with a known answer than the rule needs.
    /// </summary>
    TooFewIncidents,

    /// <summary>
    /// No observed hour to compare with.
    /// </summary>
    NoBaseData,

    /// <summary>
    /// More process snapshots than the report keeps.
    /// </summary>
    TooManySnapshots,

    /// <summary>
    /// More boots than the report keeps.
    /// </summary>
    TooManyBoots,

    /// <summary>
    /// More runs of observed hours than the report keeps.
    /// </summary>
    TooManyObservedRuns,

    /// <summary>
    /// No panel memory in any snapshot.
    /// </summary>
    NoPanelMemory
}