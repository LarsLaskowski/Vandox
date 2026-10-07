namespace Vandox.Storage;

/// <summary>
/// Reports a batch it rejects before writing.
/// </summary>
public sealed class InvalidBatchException : StoreException
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="InvalidBatchException"/> class.
    /// </summary>
    /// <param name="message">The message</param>
    public InvalidBatchException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="InvalidBatchException"/> class.
    /// </summary>
    /// <param name="message">The message</param>
    /// <param name="innerException">The cause</param>
    public InvalidBatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    #endregion // Constructors
}