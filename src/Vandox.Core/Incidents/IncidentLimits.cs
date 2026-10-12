namespace Vandox.Core.Incidents;

/// <summary>
/// Limits of an incident.
/// </summary>
public static class IncidentLimits
{
    #region Constants

    /// <summary>
    /// The most kills an incident keeps.
    /// </summary>
    public const int MaxKills = 16;

    /// <summary>
    /// The most record IDs of a kill or timeline entry.
    /// </summary>
    public const int MaxRecordIds = 16;

    /// <summary>
    /// The most timeline entries.
    /// </summary>
    public const int MaxTimelineEvents = 1000;

    /// <summary>
    /// The most UTF-8 bytes of the serialized timeline entries.
    /// </summary>
    public const int MaxTimelineBytes = 4194304;

    /// <summary>
    /// The most growth candidates, consumers and connection processes.
    /// </summary>
    public const int MaxCandidates = 3;

    /// <summary>
    /// The most new templates and template spikes.
    /// </summary>
    public const int MaxTemplates = 20;

    #endregion // Constants
}