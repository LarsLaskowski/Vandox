namespace Vandox.Storage;

/// <summary>
/// A note of an incident.
/// </summary>
public sealed class IncidentNote
{
    #region Properties

    /// <summary>
    /// Gets or sets the number of the incident the note was written for.
    /// </summary>
    public long WrittenFor { get; set; }

    /// <summary>
    /// Gets or sets the number of the incident the note belongs to now.
    /// </summary>
    public long IncidentId { get; set; }

    /// <summary>
    /// Gets or sets the text.
    /// </summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the time it was last saved.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; }

    #endregion // Properties
}