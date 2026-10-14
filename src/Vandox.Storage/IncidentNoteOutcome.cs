namespace Vandox.Storage;

/// <summary>
/// The outcome of saving a note.
/// </summary>
public enum IncidentNoteOutcome
{
    #region Values

    /// <summary>
    /// The note was saved.
    /// </summary>
    Saved,

    /// <summary>
    /// The note was deleted.
    /// </summary>
    Deleted,

    /// <summary>
    /// The incident or the note does not exist.
    /// </summary>
    NotFound

    #endregion // Values
}