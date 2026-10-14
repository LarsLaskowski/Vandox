using Microsoft.AspNetCore.Components;

using Vandox.Backend.Incidents;

namespace Vandox.Backend.Components.Incidents;

/// <summary>
/// Shows the form of one note of an incident.
/// </summary>
public sealed partial class IncidentNoteForm : ComponentBase
{
    #region Properties

    /// <summary>
    /// Gets or sets the number of the incident the form posts to.
    /// </summary>
    [Parameter]
    [EditorRequired]
    public long IncidentId { get; set; }

    /// <summary>
    /// Gets or sets the number of the incident the note was written for.
    /// </summary>
    [Parameter]
    [EditorRequired]
    public long WrittenFor { get; set; }

    /// <summary>
    /// Gets or sets the text of the note.
    /// </summary>
    [Parameter]
    [EditorRequired]
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the time the note was last saved.
    /// </summary>
    [Parameter]
    [EditorRequired]
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the note was moved from a merged incident.
    /// </summary>
    [Parameter]
    [EditorRequired]
    public bool Merged { get; set; }

    /// <summary>
    /// Gets the address the form posts to.
    /// </summary>
    private string Action => IncidentsView.NoteAction(IncidentId);

    #endregion // Properties
}