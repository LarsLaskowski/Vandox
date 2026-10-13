namespace Vandox.Backend.Security;

/// <summary>
/// Signals that a text is not an accepted argon2id hash; the message is one of the fixed reasons.
/// </summary>
public sealed class PasswordHashException : Exception
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="PasswordHashException"/> class.
    /// </summary>
    public PasswordHashException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PasswordHashException"/> class.
    /// </summary>
    /// <param name="message">The reason</param>
    public PasswordHashException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PasswordHashException"/> class.
    /// </summary>
    /// <param name="message">The reason</param>
    /// <param name="innerException">The cause</param>
    public PasswordHashException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    #endregion // Constructors
}