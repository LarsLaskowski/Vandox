using System.Buffers.Text;
using System.Security.Claims;
using System.Security.Cryptography;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Vandox.Backend.Security;

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

    private const int SessionIdBytes = 32;

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

    private readonly TimeProvider _timeProvider;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, StoredSession> _sessions = new(StringComparer.Ordinal);

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionStore"/> class.
    /// </summary>
    /// <param name="timeProvider">The clock</param>
    public SessionStore(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the number of stored sessions.
    /// </summary>
    internal int Count
    {
        get
        {
            lock (_lock)
            {
                return _sessions.Count;
            }
        }
    }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Creates the key of a new session: 32 random bytes as base64url without padding.
    /// </summary>
    /// <returns>The key, 43 characters</returns>
    internal static string NewSessionId()
    {
        return Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(SessionIdBytes));
    }

    /// <summary>
    /// Tells whether the session of a user is active and extends its idle time.
    /// </summary>
    /// <param name="user">The user</param>
    /// <returns><c>true</c> when the session is active</returns>
    internal bool Touch(ClaimsPrincipal user)
    {
        var key = user.FindFirst(SessionClaimType)?.Value;

        return key is not null && Find(key, true) is not null;
    }

    /// <summary>
    /// Tells whether the session of a user is active, without extending its idle time.
    /// </summary>
    /// <param name="user">The user</param>
    /// <returns><c>true</c> when the session is active</returns>
    internal bool IsActive(ClaimsPrincipal user)
    {
        var key = user.FindFirst(SessionClaimType)?.Value;

        return key is not null && Find(key, false) is not null;
    }

    /// <summary>
    /// Tells whether a session has been idle for too long or has reached its absolute lifetime.
    /// </summary>
    /// <param name="session">The session</param>
    /// <param name="now">The current time</param>
    /// <returns><c>true</c> when it has ended</returns>
    private static bool IsEnded(StoredSession session, DateTimeOffset now)
    {
        return now - session.LastActivity >= IdleTimeout || now - session.Created >= AbsoluteLifetime;
    }

    /// <summary>
    /// Finds an active session; an ended one is forgotten.
    /// </summary>
    /// <param name="key">The key of the session</param>
    /// <param name="extend">Whether the idle time is extended</param>
    /// <returns>The session; <c>null</c> when it is unknown or has ended</returns>
    private StoredSession? Find(string key, bool extend)
    {
        var now = _timeProvider.GetUtcNow();

        lock (_lock)
        {
            if (_sessions.TryGetValue(key, out var session))
            {
                return Activate(session, key, now, extend);
            }

            return null;
        }
    }

    /// <summary>
    /// Returns a session when it is still active, and forgets it when it has ended. The caller holds the lock.
    /// </summary>
    /// <param name="session">The session</param>
    /// <param name="key">The key of the session</param>
    /// <param name="now">The current time</param>
    /// <param name="extend">Whether the idle time is extended</param>
    /// <returns>The session; <c>null</c> when it has ended</returns>
    private StoredSession? Activate(StoredSession session, string key, DateTimeOffset now, bool extend)
    {
        if (IsEnded(session, now))
        {
            _sessions.Remove(key);

            return null;
        }

        if (extend)
        {
            session.LastActivity = now;
        }

        return session;
    }

    /// <summary>
    /// Forgets the oldest session when the table is full. The caller holds the lock.
    /// </summary>
    private void RemoveOldestWhenFull()
    {
        if (_sessions.Count >= MaxSessions)
        {
            _sessions.Remove(_sessions.MinBy(pair => pair.Value.Created).Key);
        }
    }

    /// <summary>
    /// Forgets the sessions that have ended. The caller holds the lock.
    /// </summary>
    /// <param name="now">The current time</param>
    private void RemoveEnded(DateTimeOffset now)
    {
        foreach (var key in _sessions.Where(pair => IsEnded(pair.Value, now)).Select(pair => pair.Key).ToList())
        {
            _sessions.Remove(key);
        }
    }

    #endregion // Methods

    #region ITicketStore

    /// <inheritdoc/>
    public Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var key = ticket.Principal.FindFirst(SessionClaimType)?.Value ?? throw new InvalidOperationException($"The ticket has no {SessionClaimType} claim");
        var now = _timeProvider.GetUtcNow();

        lock (_lock)
        {
            RemoveEnded(now);

            if (_sessions.ContainsKey(key))
            {
                _sessions[key] = new StoredSession(ticket, now);
            }
            else
            {
                RemoveOldestWhenFull();
                _sessions.Add(key, new StoredSession(ticket, now));
            }
        }

        return Task.FromResult(key);
    }

    /// <inheritdoc/>
    public Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(key, out var session))
            {
                session.Ticket = ticket;
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        return Task.FromResult(Find(key, true)?.Ticket);
    }

    /// <inheritdoc/>
    public Task RemoveAsync(string key)
    {
        lock (_lock)
        {
            _sessions.Remove(key);
        }

        return Task.CompletedTask;
    }

    #endregion // ITicketStore
}