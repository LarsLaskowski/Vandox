using System.Globalization;

using Microsoft.AspNetCore.Components;

using Vandox.Backend.Incidents;
using Vandox.Backend.Logs;

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

    /// <summary>
    /// Gets the label of the text area: the number the note was written for when it was moved from a merged incident.
    /// </summary>
    private string Label => Merged ? LogsView.Format(IncidentsView.WrittenFor, WrittenFor.ToString(CultureInfo.InvariantCulture)) : "Note";

    #endregion // Properties
}