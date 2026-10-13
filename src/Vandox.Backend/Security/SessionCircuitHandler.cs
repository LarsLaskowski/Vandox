using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Vandox.Backend.Security;

/// <summary>
/// Ends a live connection whose session has ended: no inbound activity is processed after that.
/// </summary>
internal sealed class SessionCircuitHandler : CircuitHandler
{
    #region Fields

    private readonly AuthenticationStateProvider _authenticationStateProvider;
    private readonly SessionStore _sessions;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionCircuitHandler"/> class.
    /// </summary>
    /// <param name="authenticationStateProvider">The authentication state of the circuit</param>
    /// <param name="sessions">The sessions</param>
    public SessionCircuitHandler(AuthenticationStateProvider authenticationStateProvider, SessionStore sessions)
    {
        _authenticationStateProvider = authenticationStateProvider;
        _sessions = sessions;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Checks that the session of the circuit is active and extends its idle time.
    /// </summary>
    /// <returns>A task that completes when the session is active</returns>
    /// <exception cref="SessionEndedException">The session has ended</exception>
    internal async Task CheckSessionAsync()
    {
        var state = await _authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);

        if (_sessions.Touch(state.User))
        {
            return;
        }

        throw new SessionEndedException("The session of the live connection has ended");
    }

    #endregion // Methods

    #region CircuitHandler

    /// <inheritdoc/>
    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(Func<CircuitInboundActivityContext, Task> next)
    {
        return async context =>
               {
                   await CheckSessionAsync().ConfigureAwait(false);
                   await next(context).ConfigureAwait(false);
               };
    }

    #endregion // CircuitHandler
}