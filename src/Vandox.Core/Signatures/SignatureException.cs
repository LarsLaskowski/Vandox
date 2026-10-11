namespace Vandox.Core.Signatures;

/// <summary>
/// A signature catalog breaks a rule.
/// </summary>
public sealed class SignatureException : Exception
{
    #region Constructors

    /// <summary>
    /// Creates the exception.
    /// </summary>
    /// <param name="message">The error text</param>
    public SignatureException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Creates the exception with its cause.
    /// </summary>
    /// <param name="message">The error text</param>
    /// <param name="innerException">The cause</param>
    public SignatureException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    #endregion // Constructors
}