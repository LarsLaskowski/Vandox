using System.Security.Claims;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Vandox.Backend.Security;

#pragma warning disable RH2003, S2325

/// <summary>
/// Keeps the signed-in sessions on the server; the cookie holds only the key of a session.
/// </summary>
internal sealed class SessionStore : ITicketStore
{
    #region Constants

    /// <summary>
    /// The claim that holds the key of the session.
    /// </summary>
    internal const string SessionClaimType = "vandox:session";

    /// <summary>
    /// The most sessions kept at once.
    /// </summary>
    internal const int MaxSessions = 16;

    #endregion // Constants

    #region Fields

    /// <summary>
    /// How long a session may be unused.
    /// </summary>
    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromHours(1);

    /// <summary>
    /// How long a session lasts at most.
    /// </summary>
    internal static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromHours(12);

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionStore"/> class.
    /// </summary>
    /// <param name="timeProvider">The clock</param>
    public SessionStore(TimeProvider timeProvider)
    {
        throw new NotImplementedException();
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the number of stored sessions.
    /// </summary>
    internal int Count => throw new NotImplementedException();

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Creates the key of a new session: 32 random bytes as base64url without padding.
    /// </summary>
    /// <returns>The key, 43 characters</returns>
    internal static string NewSessionId()
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Tells whether the session of a user is active and extends its idle time.
    /// </summary>
    /// <param name="user">The user</param>
    /// <returns><c>true</c> when the session is active</returns>
    internal bool Touch(ClaimsPrincipal user)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Tells whether the session of a user is active, without extending its idle time.
    /// </summary>
    /// <param name="user">The user</param>
    /// <returns><c>true</c> when the session is active</returns>
    internal bool IsActive(ClaimsPrincipal user)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods

    #region ITicketStore

    /// <inheritdoc/>
    public Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc/>
    public Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc/>
    public Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc/>
    public Task RemoveAsync(string key)
    {
        throw new NotImplementedException();
    }

    #endregion // ITicketStore
}