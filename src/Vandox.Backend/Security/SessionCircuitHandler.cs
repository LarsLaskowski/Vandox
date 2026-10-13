using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Vandox.Backend.Security;

#pragma warning disable RH2003, S2325

/// <summary>
/// Ends a live connection whose session has ended: no inbound activity is processed after that.
/// </summary>
internal sealed class SessionCircuitHandler : CircuitHandler
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionCircuitHandler"/> class.
    /// </summary>
    /// <param name="authenticationStateProvider">The authentication state of the circuit</param>
    /// <param name="sessions">The sessions</param>
    public SessionCircuitHandler(AuthenticationStateProvider authenticationStateProvider, SessionStore sessions)
    {
        throw new NotImplementedException();
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Checks that the session of the circuit is active and extends its idle time.
    /// </summary>
    /// <returns>A task that completes when the session is active</returns>
    /// <exception cref="SessionEndedException">The session has ended</exception>
    internal Task CheckSessionAsync()
    {
        throw new NotImplementedException();
    }

    #endregion // Methods

    #region CircuitHandler

    /// <inheritdoc/>
    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(Func<CircuitInboundActivityContext, Task> next)
    {
        throw new NotImplementedException();
    }

    #endregion // CircuitHandler
}