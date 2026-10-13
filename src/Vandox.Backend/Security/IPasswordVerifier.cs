namespace Vandox.Backend.Security;

/// <summary>
/// Checks a password against the configured web password.
/// </summary>
internal interface IPasswordVerifier
{
    #region Methods

    /// <summary>
    /// Checks a password.
    /// </summary>
    /// <param name="password">The password</param>
    /// <returns><c>true</c> when it is the web password</returns>
    bool Verify(string password);

    #endregion // Methods
}