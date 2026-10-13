using System.Net;

namespace Vandox.Backend.Security;

#pragma warning disable RH2003, S2325

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

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="LoginThrottle"/> class.
    /// </summary>
    /// <param name="timeProvider">The clock</param>
    internal LoginThrottle(TimeProvider timeProvider)
    {
        throw new NotImplementedException();
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the number of tracked clients.
    /// </summary>
    internal int TrackedClients => throw new NotImplementedException();

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Returns the key a client is tracked under: the address, an IPv6 address as its /64 network.
    /// </summary>
    /// <param name="address">The address; <c>null</c> when unknown</param>
    /// <returns>The key</returns>
    internal static string ClientKey(IPAddress? address)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Tells whether a client, or everyone, is locked out now.
    /// </summary>
    /// <param name="clientKey">The key of the client</param>
    /// <returns><c>true</c> when sign-ins are not checked</returns>
    internal bool IsLockedOut(string clientKey)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Records a failed sign-in.
    /// </summary>
    /// <param name="clientKey">The key of the client</param>
    /// <returns>The failure count and the locks the failure started</returns>
    internal LoginFailure RecordFailure(string clientKey)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Records a successful sign-in: the client's failures are forgotten.
    /// </summary>
    /// <param name="clientKey">The key of the client</param>
    internal void RecordSuccess(string clientKey)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}