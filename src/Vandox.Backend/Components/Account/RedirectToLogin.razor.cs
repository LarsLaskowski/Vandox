using Microsoft.AspNetCore.Components;

namespace Vandox.Backend.Components.Account;

#pragma warning disable RH2003, S2325

/// <summary>
/// Sends the browser to the login page with a full page load.
/// </summary>
public sealed partial class RedirectToLogin
{
    #region ComponentBase

    /// <inheritdoc/>
    protected override void OnInitialized()
    {
        throw new NotImplementedException();
    }

    #endregion // ComponentBase
}