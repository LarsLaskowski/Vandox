using Microsoft.AspNetCore.Components;

namespace Vandox.Backend.Components.Pages;

/// <summary>
/// The page <c>/incidents/{Id}</c> of the web UI.
/// </summary>
public sealed partial class IncidentPage : ComponentBase
{
    #region Properties

    /// <summary>
    /// Gets or sets the number of the incident.
    /// </summary>
    [Parameter]
    public long Id { get; set; }

    #endregion // Properties
}