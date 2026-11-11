using Microsoft.AspNetCore.Components.Authorization;

namespace Vandox.Backend.Tests;

/// <summary>
/// An <see cref="AuthenticationStateProvider"/> that has no state yet: like the framework's <c>ServerAuthenticationStateProvider</c> before a state is set, it throws <see cref="InvalidOperationException"/> when asked for one.
/// </summary>
internal sealed class UnsetAuthenticationStateProvider : AuthenticationStateProvider
{
    #region AuthenticationStateProvider

    /// <inheritdoc />
    public override Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        throw new InvalidOperationException("No authentication state has been set.");
    }

    #endregion // AuthenticationStateProvider
}