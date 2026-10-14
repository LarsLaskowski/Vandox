using Microsoft.AspNetCore.Components;

using Vandox.Backend.Logs;

namespace Vandox.Backend.Components.Logs;

/// <summary>
/// Shows external text as text nodes, with escaped runs marked.
/// </summary>
public sealed partial class ExternalText
{
    #region Properties

    /// <summary>
    /// Gets or sets the runs to show; the caller builds them with the cap of <see cref="LogText"/>.
    /// </summary>
    [Parameter]
    [EditorRequired]
    public IReadOnlyList<TextRun> Runs { get; set; } = [];

    #endregion // Properties
}