using System.Security.Claims;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;

namespace Vandox.Backend.Tests;

/// <summary>
/// An <see cref="IAuthenticationService"/> that records the sign-ins and sign-outs it is asked for and authenticates nobody.
/// </summary>
internal sealed class RecordingAuthenticationService : IAuthenticationService
{
    #region Properties

    /// <summary>
    /// Gets the sign-ins: the scheme, the principal and the properties.
    /// </summary>
    internal List<(string? Scheme, ClaimsPrincipal Principal, AuthenticationProperties? Properties)> SignIns { get; } = [];

    /// <summary>
    /// Gets the schemes of the sign-outs.
    /// </summary>
    internal List<string?> SignOuts { get; } = [];

    #endregion // Properties

    #region IAuthenticationService

    /// <inheritdoc />
    public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme)
    {
        return Task.FromResult(AuthenticateResult.NoResult());
    }

    /// <inheritdoc />
    public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties)
    {
        SignIns.Add((scheme, principal, properties));

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
    {
        SignOuts.Add(scheme);

        return Task.CompletedTask;
    }

    #endregion // IAuthenticationService
}