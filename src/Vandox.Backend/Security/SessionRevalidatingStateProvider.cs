using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.Extensions.Logging;

namespace Vandox.Backend.Security;

/// <summary>
/// Provides the authentication state of a circuit and revalidates the session at an interval.
/// </summary>
internal sealed class SessionRevalidatingStateProvider : RevalidatingServerAuthenticationStateProvider
{
    #region Fields

    /// <summary>
    /// The interval of the revalidation.
    /// </summary>
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly SessionStore _sessions;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionRevalidatingStateProvider"/> class.
    /// </summary>
    /// <param name="loggerFactory">The logger factory</param>
    /// <param name="sessions">The sessions</param>
    public SessionRevalidatingStateProvider(ILoggerFactory loggerFactory, SessionStore sessions)
        : base(loggerFactory)
    {
        _sessions = sessions;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Tells whether the session of an authentication state is active.
    /// </summary>
    /// <param name="authenticationState">The state</param>
    /// <returns><c>true</c> when the session is active</returns>
    internal bool IsValid(AuthenticationState authenticationState)
    {
        return _sessions.IsActive(authenticationState.User);
    }

    #endregion // Methods

    #region RevalidatingServerAuthenticationStateProvider

    /// <inheritdoc/>
    protected override TimeSpan RevalidationInterval => Interval;

    /// <inheritdoc/>
    protected override Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState, CancellationToken cancellationToken)
    {
        return Task.FromResult(IsValid(authenticationState));
    }

    #endregion // RevalidatingServerAuthenticationStateProvider
}