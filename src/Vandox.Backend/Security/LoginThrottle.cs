using System.Net;
using System.Net.Sockets;

namespace Vandox.Backend.Security;

/// <summary>
/// Counts failed sign-ins per client and overall, and tells which clients are locked out.
/// </summary>
internal sealed class LoginThrottle
{
    #region Constants

    /// <summary>
    /// The failures within the window that lock a client.
    /// </summary>
    internal const int MaxFailures = 5;

    /// <summary>
    /// The failures within the global window that lock every client.
    /// </summary>
    internal const int MaxGlobalFailures = 100;

    /// <summary>
    /// The most clients tracked at once.
    /// </summary>
    internal const int MaxClients = 4096;

    /// <summary>
    /// The key of a request without a client address.
    /// </summary>
    internal const string UnknownClient = "unknown";

    #endregion // Constants

    #region Fields

    /// <summary>
    /// The window of the per-client failures.
    /// </summary>
    internal static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The duration of a client lock.
    /// </summary>
    internal static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The window of the overall failures.
    /// </summary>
    internal static readonly TimeSpan GlobalWindow = TimeSpan.FromHours(1);

    private readonly TimeProvider _timeProvider;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, LoginClient> _clients = new(StringComparer.Ordinal);
    private readonly Queue<DateTimeOffset> _globalFailures = new();

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="LoginThrottle"/> class.
    /// </summary>
    /// <param name="timeProvider">The clock</param>
    internal LoginThrottle(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the number of tracked clients.
    /// </summary>
    internal int TrackedClients
    {
        get
        {
            lock (_lock)
            {
                return _clients.Count;
            }
        }
    }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Returns the key a client is tracked under: the address, an IPv6 address as its /64 network.
    /// </summary>
    /// <param name="address">The address; <c>null</c> when unknown</param>
    /// <returns>The key</returns>
    internal static string ClientKey(IPAddress? address)
    {
        if (address is null)
        {
            return UnknownClient;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4().ToString();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        var bytes = new byte[16];

        address.GetAddressBytes().AsSpan(0, 8).CopyTo(bytes);

        return new IPAddress(bytes) + "/64";
    }

    /// <summary>
    /// Tells whether a client, or everyone, is locked out now.
    /// </summary>
    /// <param name="clientKey">The key of the client</param>
    /// <returns><c>true</c> when sign-ins are not checked</returns>
    internal bool IsLockedOut(string clientKey)
    {
        var now = _timeProvider.GetUtcNow();

        lock (_lock)
        {
            Prune(_globalFailures, now, GlobalWindow);

            if (_globalFailures.Count >= MaxGlobalFailures)
            {
                return true;
            }

            return _clients.TryGetValue(clientKey, out var client) && client.LockedUntil > now;
        }
    }

    /// <summary>
    /// Records a failed sign-in.
    /// </summary>
    /// <param name="clientKey">The key of the client</param>
    /// <returns>The failure count and the locks the failure started</returns>
    internal LoginFailure RecordFailure(string clientKey)
    {
        var now = _timeProvider.GetUtcNow();

        lock (_lock)
        {
            var allLockedUntil = RecordGlobalFailure(now);
            var client = FindOrAdd(clientKey);

            if (client.LockedUntil <= now)
            {
                client.Failures.Clear();
                client.LockedUntil = null;
            }

            Prune(client.Failures, now, FailureWindow);
            client.Failures.Enqueue(now);
            client.LastFailure = now;

            DateTimeOffset? clientLockedUntil = null;

            if (client.Failures.Count >= MaxFailures && client.LockedUntil is null)
            {
                clientLockedUntil = now + LockoutDuration;
                client.LockedUntil = clientLockedUntil;
            }

            return new LoginFailure(client.Failures.Count, clientLockedUntil, allLockedUntil);
        }
    }

    /// <summary>
    /// Records a successful sign-in: the client's failures are forgotten.
    /// </summary>
    /// <param name="clientKey">The key of the client</param>
    internal void RecordSuccess(string clientKey)
    {
        lock (_lock)
        {
            _clients.Remove(clientKey);
        }
    }

    /// <summary>
    /// Removes the times that are not younger than the window.
    /// </summary>
    /// <param name="times">The times, oldest first</param>
    /// <param name="now">The current time</param>
    /// <param name="window">The window</param>
    private static void Prune(Queue<DateTimeOffset> times, DateTimeOffset now, TimeSpan window)
    {
        while (times.Count > 0 && now - times.Peek() >= window)
        {
            times.Dequeue();
        }
    }

    /// <summary>
    /// Adds a failure to the overall queue.
    /// </summary>
    /// <param name="now">The time of the failure</param>
    /// <returns>The end of the overall lock when this failure reached the limit; otherwise <c>null</c></returns>
    private DateTimeOffset? RecordGlobalFailure(DateTimeOffset now)
    {
        Prune(_globalFailures, now, GlobalWindow);
        _globalFailures.Enqueue(now);

        while (_globalFailures.Count > MaxGlobalFailures)
        {
            _globalFailures.Dequeue();
        }

        return _globalFailures.Count >= MaxGlobalFailures ? _globalFailures.Peek() + GlobalWindow : null;
    }

    /// <summary>
    /// Returns the state of a client, adding it and forgetting the client with the oldest failure when the table is full.
    /// </summary>
    /// <param name="clientKey">The key of the client</param>
    /// <returns>The state</returns>
    private LoginClient FindOrAdd(string clientKey)
    {
        if (_clients.TryGetValue(clientKey, out var client))
        {
            return client;
        }

        if (_clients.Count >= MaxClients)
        {
            var oldest = _clients.MinBy(pair => pair.Value.LastFailure).Key;

            _clients.Remove(oldest);
        }

        client = new LoginClient();
        _clients.Add(clientKey, client);

        return client;
    }

    #endregion // Methods
}