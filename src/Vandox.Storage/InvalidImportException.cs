namespace Vandox.Storage;

/// <summary>
/// Reports an import start it refuses.
/// </summary>
public sealed class InvalidImportException : StoreException
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="InvalidImportException"/> class.
    /// </summary>
    /// <param name="message">The message</param>
    public InvalidImportException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="InvalidImportException"/> class.
    /// </summary>
    /// <param name="message">The message</param>
    /// <param name="innerException">The cause</param>
    public InvalidImportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    #endregion // Constructors
}