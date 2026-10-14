namespace Vandox.Storage;

/// <summary>
/// The position of an incident in the list: its start and number.
/// </summary>
public sealed class IncidentPosition
{
    #region Properties

    /// <summary>
    /// Gets or sets the start of the incident.
    /// </summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>
    /// Gets or sets the number of the incident.
    /// </summary>
    public long Id { get; set; }

    #endregion // Properties
}