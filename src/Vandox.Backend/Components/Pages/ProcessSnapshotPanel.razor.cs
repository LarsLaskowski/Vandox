using Microsoft.AspNetCore.Components;

using Vandox.Backend.History;

namespace Vandox.Backend.Components.Pages;

/// <summary>
/// Shows the process snapshot of a moment with its note, its tables and the button to the snapshot before it.
/// </summary>
public sealed partial class ProcessSnapshotPanel : ComponentBase
{
    #region Properties

    /// <summary>
    /// Gets or sets the process snapshot to show; the missing-snapshot text is shown while it is <see langword="null"/>.
    /// </summary>
    [Parameter]
    public ProcessSnapshotView? Process { get; set; }

    /// <summary>
    /// Gets or sets the moment of the snapshot before the shown one, or <see langword="null"/> when there is none.
    /// </summary>
    [Parameter]
    public DateTimeOffset? Before { get; set; }

    /// <summary>
    /// Gets or sets the callback that receives the moment of the snapshot before the shown one when its button is pressed.
    /// </summary>
    [Parameter]
    public EventCallback<DateTimeOffset> OnBefore { get; set; }

    #endregion // Properties
}