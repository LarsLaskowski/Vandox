namespace Vandox.Storage;

/// <summary>
/// The result of saving an incident.
/// </summary>
public sealed class IncidentSaveResult
{
    #region Properties

    /// <summary>
    /// Gets or sets the number of the stored incident.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Gets or sets what the save did.
    /// </summary>
    public IncidentSaveOutcome Outcome { get; set; }

    /// <summary>
    /// Gets or sets the number of stored incidents merged into it.
    /// </summary>
    public long Merged { get; set; }

    #endregion // Properties
}