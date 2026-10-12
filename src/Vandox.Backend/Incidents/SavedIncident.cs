using Vandox.Core.Incidents;
using Vandox.Storage;

namespace Vandox.Backend.Incidents;

/// <summary>
/// An incident after it was saved.
/// </summary>
public sealed class SavedIncident
{
    #region Properties

    /// <summary>
    /// Gets or sets the result of the save.
    /// </summary>
    public IncidentSaveResult Result { get; set; } = new();

    /// <summary>
    /// Gets or sets the incident.
    /// </summary>
    public Incident Incident { get; set; } = new();

    #endregion // Properties
}