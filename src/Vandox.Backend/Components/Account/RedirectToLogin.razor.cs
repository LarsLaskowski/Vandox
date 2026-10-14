using Microsoft.AspNetCore.Components;

namespace Vandox.Backend.Components.Account;

/// <summary>
/// Sends the browser to the login page with a full page load.
/// </summary>
public sealed partial class RedirectToLogin
{
    #region Properties

    /// <summary>
    /// Gets or sets the navigation.
    /// </summary>
    [Inject]
    private NavigationManager Navigation { get; set; } = null!;

    #endregion // Properties

    #region ComponentBase

    /// <inheritdoc/>
    protected override void OnInitialized()
    {
        Navigation.NavigateTo("login", true);
    }

    #endregion // ComponentBase
}