namespace Vandox.Backend.Security;

/// <summary>
/// The state after a failed sign-in.
/// </summary>
/// <param name="Failures">The failures of the client within the window</param>
/// <param name="ClientLockedUntil">The end of the lock this failure started for the client; <c>null</c> when it started none</param>
/// <param name="AllLockedUntil">The end of the lock this failure started for all clients; <c>null</c> when it started none</param>
internal readonly record struct LoginFailure(int Failures, DateTimeOffset? ClientLockedUntil, DateTimeOffset? AllLockedUntil);