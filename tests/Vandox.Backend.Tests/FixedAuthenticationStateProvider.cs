using System.Security.Claims;

using Microsoft.AspNetCore.Components.Authorization;

namespace Vandox.Backend.Tests;

/// <summary>
/// An <see cref="AuthenticationStateProvider"/> that returns one given user.
/// </summary>
internal sealed class FixedAuthenticationStateProvider : AuthenticationStateProvider
{
    #region Fields

    private readonly ClaimsPrincipal _user;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="FixedAuthenticationStateProvider"/> class.
    /// </summary>
    /// <param name="user">The user every state holds</param>
    internal FixedAuthenticationStateProvider(ClaimsPrincipal user)
    {
        _user = user;
    }

    #endregion // Constructors

    #region AuthenticationStateProvider

    /// <inheritdoc />
    public override Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        return Task.FromResult(new AuthenticationState(_user));
    }

    #endregion // AuthenticationStateProvider
}