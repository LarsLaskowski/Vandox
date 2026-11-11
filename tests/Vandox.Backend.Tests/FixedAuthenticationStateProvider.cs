using System.Security.Claims;

using Microsoft.AspNetCore.Components.Authorization;

namespace Vandox.Backend.Tests;

/// <summary>
/// An <see cref="AuthenticationStateProvider"/> that returns one given user until <see cref="ChangeUser"/> announces another, as the framework's provider does when the state of a circuit changes.
/// </summary>
internal sealed class FixedAuthenticationStateProvider : AuthenticationStateProvider
{
    #region Fields

    private ClaimsPrincipal _user;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="FixedAuthenticationStateProvider"/> class.
    /// </summary>
    /// <param name="user">The user every state holds until it is changed</param>
    internal FixedAuthenticationStateProvider(ClaimsPrincipal user)
    {
        _user = user;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Makes every later state hold another user and announces the new state through <see cref="AuthenticationStateProvider.AuthenticationStateChanged"/>.
    /// </summary>
    /// <param name="user">The new user</param>
    internal void ChangeUser(ClaimsPrincipal user)
    {
        _user = user;

        NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(user)));
    }

    #endregion // Methods

    #region AuthenticationStateProvider

    /// <inheritdoc />
    public override Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        return Task.FromResult(new AuthenticationState(_user));
    }

    #endregion // AuthenticationStateProvider
}