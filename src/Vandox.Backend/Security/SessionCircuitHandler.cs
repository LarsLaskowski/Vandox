using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Vandox.Backend.Security;

/// <summary>
/// Ends a live connection whose session has ended: no inbound activity is processed after that.
/// </summary>
internal sealed class SessionCircuitHandler : CircuitHandler, IDisposable
{
    #region Fields

    private readonly AuthenticationStateProvider _authenticationStateProvider;
    private readonly SessionStore _sessions;

    private Task<AuthenticationState>? _state;

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

        _authenticationStateProvider.AuthenticationStateChanged += OnAuthenticationStateChanged;
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
        // The event comes from hub threads and the revalidation loop, not from the circuit's dispatcher: the first read
        // stores its state only if no change was announced meanwhile. A read that throws stores nothing.
        var announced = Volatile.Read(ref _state);

        if (announced is null)
        {
            var initial = _authenticationStateProvider.GetAuthenticationStateAsync();

            await initial.ConfigureAwait(false);

            announced = Interlocked.CompareExchange(ref _state, initial, null) ?? initial;
        }

        var state = await announced.ConfigureAwait(false);

        if (_sessions.Touch(state.User))
        {
            return;
        }

        throw new SessionEndedException("The session of the live connection has ended");
    }

    /// <summary>
    /// Stores the announced authentication state.
    /// </summary>
    /// <param name="task">The new authentication state</param>
    private void OnAuthenticationStateChanged(Task<AuthenticationState> task)
    {
        Volatile.Write(ref _state, task);
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

    #region IDisposable

    /// <inheritdoc/>
    public void Dispose()
    {
        _authenticationStateProvider.AuthenticationStateChanged -= OnAuthenticationStateChanged;
    }

    #endregion // IDisposable
}