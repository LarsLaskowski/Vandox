namespace Vandox.Storage;

/// <summary>
/// The notes of one incident.
/// </summary>
public sealed class IncidentNotePage
{
    #region Properties

    /// <summary>
    /// Gets or sets the notes.
    /// </summary>
    public IReadOnlyList<IncidentNote> Notes { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether more notes exist than the list holds.
    /// </summary>
    public bool More { get; set; }

    #endregion // Properties
}