using Microsoft.AspNetCore.Components;

using Vandox.Backend.History;

namespace Vandox.Backend.Components.Pages;

/// <summary>
/// Shows one table of a snapshot of the history view; the cells are external text and are shown through the display of external text.
/// </summary>
public sealed partial class HistorySnapshotTable : ComponentBase
{
    #region Properties

    /// <summary>
    /// Gets or sets the table to show; nothing is shown for a table without rows.
    /// </summary>
    [Parameter]
    [EditorRequired]
    public SnapshotTableView Table { get; set; } = new();

    #endregion // Properties
}