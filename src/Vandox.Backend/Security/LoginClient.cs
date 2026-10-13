namespace Vandox.Backend.Security;

/// <summary>
/// The failures and the lock of one client.
/// </summary>
internal sealed class LoginClient
{
    #region Properties

    /// <summary>
    /// Gets the times of the failures within the window, oldest first.
    /// </summary>
    public Queue<DateTimeOffset> Failures { get; } = new();

    /// <summary>
    /// Gets or sets the end of the lock; <c>null</c> when the client is not locked.
    /// </summary>
    public DateTimeOffset? LockedUntil { get; set; }

    /// <summary>
    /// Gets or sets the time of the last failure.
    /// </summary>
    public DateTimeOffset LastFailure { get; set; }

    #endregion // Properties
}