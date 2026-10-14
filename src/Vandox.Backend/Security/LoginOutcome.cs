namespace Vandox.Backend.Security;

/// <summary>
/// The result of a sign-in attempt.
/// </summary>
internal enum LoginOutcome
{
    /// <summary>
    /// The password was right and the session is signed in.
    /// </summary>
    SignedIn,

    /// <summary>
    /// The password was wrong.
    /// </summary>
    WrongPassword,

    /// <summary>
    /// The attempt was not checked: the client is locked or the check did not get its turn.
    /// </summary>
    Throttled
}