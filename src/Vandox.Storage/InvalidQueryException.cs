namespace Vandox.Storage;

/// <summary>
/// Reports a query it rejects.
/// </summary>
public sealed class InvalidQueryException : StoreException
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="InvalidQueryException"/> class.
    /// </summary>
    /// <param name="message">The message</param>
    public InvalidQueryException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="InvalidQueryException"/> class.
    /// </summary>
    /// <param name="message">The message</param>
    /// <param name="innerException">The cause</param>
    public InvalidQueryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    #endregion // Constructors
}