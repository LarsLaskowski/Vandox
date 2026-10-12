using Vandox.Core.Incidents;

namespace Vandox.Storage;

/// <summary>
/// A stored incident with its number and times.
/// </summary>
public sealed class StoredIncident
{
    #region Properties

    /// <summary>
    /// Gets or sets the number.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Gets or sets the incident.
    /// </summary>
    public Incident Incident { get; set; } = new();

    /// <summary>
    /// Gets or sets the time it was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Gets or sets the time it was last changed.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; }

    #endregion // Properties
}