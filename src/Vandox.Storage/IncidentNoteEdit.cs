namespace Vandox.Storage;

/// <summary>
/// The save of one note.
/// </summary>
public sealed class IncidentNoteEdit
{
    #region Properties

    /// <summary>
    /// Gets or sets the number of the incident.
    /// </summary>
    public long IncidentId { get; set; }

    /// <summary>
    /// Gets or sets the number of the incident the note was written for.
    /// </summary>
    public long WrittenFor { get; set; }

    /// <summary>
    /// Gets or sets the text; empty deletes the note.
    /// </summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the time of the save.
    /// </summary>
    public DateTimeOffset SavedAt { get; set; }

    #endregion // Properties
}