namespace Vandox.Backend.Security;

/// <summary>
/// Signals that the session of a live connection has ended; it ends the circuit.
/// </summary>
public sealed class SessionEndedException : Exception
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionEndedException"/> class.
    /// </summary>
    public SessionEndedException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionEndedException"/> class.
    /// </summary>
    /// <param name="message">The message</param>
    public SessionEndedException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionEndedException"/> class.
    /// </summary>
    /// <param name="message">The message</param>
    /// <param name="innerException">The cause</param>
    public SessionEndedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    #endregion // Constructors
}